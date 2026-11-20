using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AdvancedMetadata.Store;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AdvancedMetadata.Scanning;

public class ScanTask : IScheduledTask
{
    private const int CommitEvery = 500;

    private readonly ILibraryManager _library;
    private readonly MetadataStore _store;
    private readonly ILogger<ScanTask> _logger;

    public ScanTask(ILibraryManager library, MetadataStore store, ILogger<ScanTask> logger)
    {
        _library = library;
        _store = store;
        _logger = logger;
    }

    public string Name => "Advanced Metadata Scan";

    public string Key => "AdvancedMetadataScan";

    public string Description => "Reads the mapped tags from your audio files into the local Advanced Metadata index.";

    public string Category => "Advanced Metadata";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        };
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
        => Task.Run(() => Run(progress, ct), ct);

    private void Run(IProgress<double> progress, CancellationToken ct)
    {
        var plugin = Plugin.Instance!;
        var cfg = plugin.Configuration;

        var full = cfg.FullRescanRequested || Plugin.PendingFullRescan;
        cfg.FullRescanRequested = false;
        Plugin.PendingFullRescan = false;

        // The in-process handoff wins; the config value is the fallback for scheduled runs.
        var requestedMapping = Plugin.PendingScanMappingTag;
        Plugin.PendingScanMappingTag = string.Empty;
        cfg.PendingScanMappingId = string.Empty;

        // Optional single-library scope
        var scopeId = Guid.Empty;
        var scopeName = string.Empty;
        Folder? scopeItem = null;
        // Only a manually started scan carries a scope. The scheduled run is always global.
        var requestedScope = Plugin.PendingScanLibraryId;
        if (string.IsNullOrWhiteSpace(requestedScope)) { requestedScope = cfg.PendingScanLibraryId; }
        Plugin.PendingScanLibraryId = string.Empty;
        cfg.PendingScanLibraryId = string.Empty;

        if (Guid.TryParse(requestedScope, out var wantedScope) && wantedScope != Guid.Empty)
        {
            scopeItem = _library.GetItemById(wantedScope) as Folder;
            if (scopeItem is null)
            {
                _logger.LogWarning(
                    "Advanced Metadata: scan library {Id} no longer exists, scanning every library instead",
                    wantedScope);
            }
            else
            {
                scopeId = wantedScope;
                scopeName = scopeItem.Name;
            }
        }

       var scoped = scopeId != Guid.Empty;
        cfg.LastScanLibrary = scopeName;

        cfg.LastScanStartedUtc = DateTime.UtcNow;
        cfg.LastScanFinishedUtc = null;
        cfg.LastScanError = string.Empty;
        cfg.LastScanItems = 0;
        cfg.LastScanSkipped = 0;
        cfg.LastScanValues = 0;
        cfg.LastScanRemoved = 0;
        cfg.LastScanRemovedItems = 0;
        cfg.LastScanErrors = 0;
        cfg.LastScanBackfill = 0;
        cfg.LastScanTotal = 0;
        cfg.LastScanType = full ? "Full Scan" : "Incremental Scan";
        cfg.LastScanStatus = "Running";
        cfg.LastScanStage = "Starting";
        cfg.LastScanSummary = string.Empty;
        plugin.UpdateConfiguration(cfg);

        var declared = cfg.Mappings.Where(m => !string.IsNullOrWhiteSpace(m.SourceTag)).ToList();

        var mappings = declared
            .GroupBy(m => m.SourceTag.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // SyncMappings prunes anything missing, so it must always see every mapping,
        // even when this run is scoped to one.
        var forSync = mappings.ToList();

        if (mappings.Count != declared.Count)
        {
            _logger.LogWarning(
                "Advanced Metadata: {Dup} duplicate mapping(s) collapsed by source tag",
                declared.Count - mappings.Count);
        }
        // Optional single-mapping scope: read every item, but only for this one tag.
        var mappingScoped = false;
        if (!string.IsNullOrWhiteSpace(requestedMapping))
        {
            var only = mappings
                .Where(m => string.Equals(m.SourceTag, requestedMapping, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (only.Count > 0) { mappings = only; mappingScoped = true; }
        }

        cfg.LastScanMapping = mappingScoped && mappings.Count > 0
            ? mappings[0].SourceTag
            : (string.IsNullOrWhiteSpace(requestedMapping) ? string.Empty : "requested mapping \"" + requestedMapping + "\" no longer exists");

        var knownIds = cfg.KnownMappingIds.ToHashSet();

        var backfill = mappings
            .Where(m => !knownIds.Contains(m.Id))
            .Select(m => m.SourceTag)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (full) { backfill.Clear(); }

        // A scoped scan stays incremental: unchanged files are skipped, so it is quick.
        // Only a mapping that has never been scanned gets a forced pass.
        if (mappingScoped && mappings.Count > 0 && backfill.Contains(mappings[0].SourceTag))
        {
            backfill.Clear();
            backfill.Add(mappings[0].SourceTag);
        }

        cfg.LastScanType = full ? "Full Scan" : "Incremental Scan";

        plugin.UpdateConfiguration(cfg);
        cfg.LastScanBackfill = backfill.Count;
        plugin.UpdateConfiguration(cfg);

        void Stage(string s)
        {
            cfg.LastScanStage = s;
            plugin.UpdateConfiguration(cfg);
            _logger.LogInformation("Advanced Metadata: stage -> {Stage}", s);
        }

        try
        {
            var delims = string.IsNullOrWhiteSpace(cfg.Delimiters)
                ? new[] { '/', '|', ';', ':' }
                : cfg.Delimiters.ToCharArray();

            Stage("Pruning and syncing mappings");
            _store.SyncMappings(forSync);

            // No pre-clear: a full pass replaces every item's values in the loop, so an
            // interrupted run leaves the previous index intact instead of an empty one.

            foreach (var m in mappings)
            {
                _logger.LogDebug("Advanced Metadata: mapping {Tag} applies to {Hierarchy}", m.SourceTag, m.Hierarchy);
            }

            Stage("Reading library item list");
            List<BaseItem> items;

            if (scoped)
            {
                var folder = _library.GetItemById(scopeId) as Folder;
                items = (folder?.GetRecursiveChildren() ?? Enumerable.Empty<BaseItem>())
                    .Where(i => !i.IsFolder && !i.IsVirtualItem && !string.IsNullOrEmpty(i.Path))
                    .ToList();
            }
            else
            {
                items = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Audio },
                    Recursive = true,
                    IsVirtualItem = false,
                    EnableTotalRecordCount = false
                }).ToList();
            }

            if (items.Count == 0)
            {
                cfg.LastScanTotal = 0;
                cfg.LastScanStage = "Nothing to scan";
                cfg.LastScanStatus = "Complete";
                cfg.LastScanFinishedUtc = DateTime.UtcNow;
                cfg.LastScanSummary = scoped
                    ? "No items found in " + scopeName + "."
                    : "No audio items found.";
                plugin.UpdateConfiguration(cfg);
                _logger.LogWarning("Advanced Metadata: nothing to scan ({Summary})", cfg.LastScanSummary);
                return;
            }

            cfg.LastScanTotal = items.Count;
            plugin.UpdateConfiguration(cfg);
            Stage("Scanning " + items.Count + " tracks");

            _logger.LogInformation(
                "Advanced Metadata: {Type} over {Count} tracks (backfill: {Backfill} mapping(s))",
                cfg.LastScanType, items.Count, backfill.Count);

            _store.BeginBatch();

           var done = 0;
            var skipped = 0;
            var values = 0;
            var removed = 0;
            var errors = 0;
            var rolled = 0;
            var removedItems = 0;
            var startedAt = DateTime.UtcNow;
            var runTag = Guid.NewGuid().ToString("N");
            var cfgGate = new object();

            try
            {
              Parallel.ForEach(
                    items,
                   new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
                    item =>
                {
                    var n = Interlocked.Increment(ref done);

                    var itemClock = System.Diagnostics.Stopwatch.StartNew();

                    if (items.Count > 0) { progress.Report(n * 100.0 / items.Count); }

                    if (n % CommitEvery == 0)
                    {
                        _store.CommitBatch();
                        lock (cfgGate)
                        {
                            cfg.LastScanItems = n;
                            cfg.LastScanSkipped = Volatile.Read(ref skipped);
                        }

                       _store.CheckpointPassive();
                        plugin.UpdateConfiguration(cfg);
                    }

                    if (n % 100 == 0)
                    {
                        var secs = Math.Max(1.0, (DateTime.UtcNow - startedAt).TotalSeconds);
                        _logger.LogInformation(
                            "Advanced Metadata: heartbeat {Done}/{Total} at {Rate:F2}/s (skipped {Skipped}, indexed {Values})",
                            n, items.Count, n / secs, Volatile.Read(ref skipped), Volatile.Read(ref values));
                    }

                    var path = item.Path;
                    var key = item.Id.ToString("N");

                    // Marked before the existence check: a row whose file has gone still has to
                    // be recorded as seen by this run, so the prune can tell it from another
                    // library's rows.
                    _store.TouchItem(key, runTag);

                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return; }
                    long mtime;
                    long size;
                    try
                    {
                        var fi = new FileInfo(path);
                        mtime = fi.LastWriteTimeUtc.Ticks;
                        size = fi.Length;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errors);
                        _logger.LogWarning(ex, "Advanced Metadata: could not stat {Path}", path);
                        return;
                    }

                   var unchanged = !full && _store.IsUnchanged(key, mtime, size);
                   if (unchanged && backfill.Count == 0 && !_store.ItemMissingMeta(key))
                    {
                        var seen = Interlocked.Increment(ref skipped);
                        if (seen <= 5)
                        {
                            _logger.LogDebug(
                                "Advanced Metadata: skipped {Path} (mtime {Mtime}, size {Size})",
                                path, mtime, size);
                        }

                        return;
                    }

                    var raw = ReadRawTags(path, mappings.Select(m => m.SourceTag)
                        .Concat(new[] { "ARTIST", "ALBUMARTIST", "ALBUM", "TRACKNUMBER", "DISCNUMBER" }), out var readable);

                    var albumId = ResolveAlbumId(item);

                    var artist = ArtistNames(item) ?? GetStringList(item, "AlbumArtists");
                    var albumArtist = GetStringList(item, "AlbumArtists") ?? artist;
                    var album = GetStringProperty(item, "Album");

                    if (string.IsNullOrWhiteSpace(artist))
                    {
                        artist = SplitValues(raw, "ARTIST", delims).FirstOrDefault();
                    }

                    if (string.IsNullOrWhiteSpace(albumArtist))
                    {
                        albumArtist = SplitValues(raw, "ALBUMARTIST", delims).FirstOrDefault() ?? artist;
                    }

                   if (string.IsNullOrWhiteSpace(album))
                    {
                        album = SplitValues(raw, "ALBUM", delims).FirstOrDefault();
                    }

                   if (!albumId.HasValue && !string.IsNullOrWhiteSpace(album))
                    {
                        albumId = FindByName(album, BaseItemKind.MusicAlbum)?.Id;
                    }

                    var albumArtistLinks = new List<(Guid Id, string? Name)>(GetAlbumArtists(item));
                    if (albumArtistLinks.Count == 0)
                    {
                        foreach (var nm in (albumArtist ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var trimmed = nm.Trim();
                            if (trimmed.Length == 0) { continue; }
                            albumArtistLinks.Add((FindByName(trimmed, BaseItemKind.MusicArtist)?.Id ?? Guid.Empty, trimmed));
                        }
                    }

                    long itemId;
                    try
                    {
                        itemId = _store.UpsertItem(key, item.GetType().Name,
                            item.Name ?? Path.GetFileNameWithoutExtension(path), artist, album, path, mtime, size);
                        if (albumId.HasValue) { _store.SetItemParent(key, albumId.Value.ToString("N")); }

                        var primaryArtist = GetArtists(item).FirstOrDefault();
                        if (primaryArtist.Id != Guid.Empty)
                        {
                            _store.SetItemArtist(key, primaryArtist.Id.ToString("N"));
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errors);
                        _logger.LogWarning(ex, "Advanced Metadata: could not index {Path}", path);
                        return;
                    }

                    // Discs are commonly written "1/3", so take the leading number and fall
                    // back to the parsed tag. Without a disc, multi-disc albums interleave.
                    var discNo = LeadInt(raw.TryGetValue("DISCNUMBER", out var discRaw) ? discRaw : null);
                    var trackNo = LeadInt(raw.TryGetValue("TRACKNUMBER", out var trackRaw) ? trackRaw : null);
                    if (discNo > 0 || trackNo > 0) { _store.SetItemOrder(key, discNo, trackNo); }

                    foreach (var m in mappings)
                    {
                        if (unchanged && !backfill.Contains(m.SourceTag)) { continue; }

                        var extra = (m.Delimiters ?? string.Empty).Trim();
                        var mode = (m.DelimiterMode ?? string.Empty).Trim().ToLowerInvariant();
                        if (mode.Length == 0) { mode = "remove"; }
                        char[] mDelims;
                        if (extra.Length == 0) { mDelims = delims; }
                        else if (mode == "only") { mDelims = extra.ToCharArray(); }
                        else if (mode == "remove") { mDelims = delims.Where(c => extra.IndexOf(c) < 0).ToArray(); }
                        else { mDelims = delims.Concat(extra.ToCharArray()).Distinct().ToArray(); }
                        if (mDelims.Length == 0) { mDelims = new[] { '/', '|', ';', ':' }; }
                        var vals = SplitValues(raw, m.SourceTag, mDelims);

                        var reachesAlbums = m.Hierarchy.Contains("albums", StringComparison.OrdinalIgnoreCase);
                        var reachesArtists = m.Hierarchy.Contains("artists", StringComparison.OrdinalIgnoreCase);

                        // No values left means the tag was removed, so the stored ones have to go too.
                        // Only an unreadable file is allowed to skip.
                        if (vals.Count == 0 && !readable) { continue; }

                        try
                        {
                            var hadValues = _store.CountValues(itemId, m.SourceTag);
                            _store.ReplaceFieldValues(itemId, m.SourceTag, vals);
                            var nowValues = _store.CountValues(itemId, m.SourceTag);
                            if (nowValues < hadValues) { Interlocked.Add(ref removed, hadValues - nowValues); }
                            _store.ApplyOverrides(key, m.SourceTag);
                            Interlocked.Add(ref values, vals.Count);

                            if (albumId.HasValue && reachesAlbums)
                            {
                                var aid = albumId.Value.ToString("N");
                                var albumName = album ?? item.Name ?? "Unknown album";
                                var albumBy = albumArtist ?? artist ?? albumName;
                                _store.UpsertItem(aid, "MusicAlbum", albumName, albumBy, albumName, null, 0, 0);
                                var link = albumArtistLinks.FirstOrDefault();
                                if (!string.IsNullOrWhiteSpace(link.Name)) { _store.SetItemArtist(aid, ArtistItemId(link)); }
                                if (vals.Count > 0) { Interlocked.Increment(ref rolled); }

                                // Derived from the tracks, so a value gone from all of them goes here too.
                                _store.RebuildRollup(aid, m.SourceTag, false);
                                _store.ApplyOverrides(aid, m.SourceTag);
                            }

                            if (reachesArtists && (vals.Count > 0 || reachesAlbums))
                            {
                                var names = new List<(Guid Id, string? Name)>(GetArtists(item));

                                if (names.Count == 0)
                                {
                                    var joined = GetStringList(item, "Artists") ?? artist ?? string.Empty;
                                    foreach (var nm in joined.Split('/', StringSplitOptions.RemoveEmptyEntries))
                                    {
                                        var trimmed = nm.Trim();
                                        if (trimmed.Length == 0) { continue; }
                                        names.Add((FindByName(trimmed, BaseItemKind.MusicArtist)?.Id ?? Guid.Empty, trimmed));
                                    }
                                }

                                foreach (var a in names)
                                {
                                    var an = string.IsNullOrWhiteSpace(a.Name) ? (artist ?? "Unknown artist") : a.Name;
                                    var aid = ArtistItemId(a);
                                    var tid = _store.UpsertItem(aid, "MusicArtist", an, an, null, null, 0, 0);
                                    if (reachesAlbums)
                                    {
                                        _store.RebuildRollup(aid, m.SourceTag, true);
                                    }
                                    else
                                    {
                                        _store.AddValues(tid, m.SourceTag, vals);
                                    }

                                    _store.ApplyOverrides(aid, m.SourceTag);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref errors);
                            _logger.LogWarning(ex, "Advanced Metadata: mapping {Tag} failed on {Path}", m.SourceTag, path);
                        }
                    }

                    if (n <= 20)
                    {
                        _logger.LogDebug(
                            "Advanced Metadata: item {Done} took {Ms}ms ({Mappings} mapping(s))",
                            n, itemClock.ElapsedMilliseconds, mappings.Count);
                    }
                });
            }
           finally
            {
                cfg.LastScanValues = values;
                cfg.LastScanRemoved = removed;
                cfg.LastScanSkipped = skipped;
                cfg.LastScanErrors = errors;
                cfg.LastScanItems = done;
                cfg.LastScanRemovedItems = removedItems;
                plugin.UpdateConfiguration(cfg);
                _store.EndBatch();
            }

            // A full pass has now seen every track, so anything it did not see has gone.
            if (full)
            {
                Stage("Removing values that no longer exist");
                removedItems = _store.PruneMissing(runTag, !scoped);
                cfg.LastScanRemovedItems = removedItems;
                plugin.UpdateConfiguration(cfg);
                _logger.LogInformation(
                    "Advanced Metadata: {Count} item(s) with no file left were removed", removedItems);
            }

            progress.Report(100);
            cfg.LastScanStage = "Complete";
            var knownNow = cfg.KnownMappingIds.ToList();
            foreach (var m in mappings) { if (!knownNow.Contains(m.Id)) { knownNow.Add(m.Id); } }
            cfg.KnownMappingIds = knownNow;
            cfg.LastScanItems = done;
            cfg.LastScanSkipped = skipped;
            cfg.LastScanValues = values;
            cfg.LastScanRemoved = removed;
            cfg.LastScanErrors = errors;
            cfg.LastScanFinishedUtc = DateTime.UtcNow;
            cfg.LastScanStatus = "Complete";
            // Any full pass counts, scoped or not; the Library row says what it covered.
            if (full) { cfg.LastFullScanUtc = DateTime.UtcNow; }
            cfg.LastScanSummary = $"{done} read, {skipped} unchanged, {values} indexed, "
                + $"{removedItems} items gone, {errors} errors.";
            plugin.UpdateConfiguration(cfg);

            _logger.LogInformation("Advanced Metadata: scan complete. {Summary}", cfg.LastScanSummary);
        }
        catch (OperationCanceledException)
        {
            cfg.LastScanStatus = "Cancelled";
            cfg.LastScanFinishedUtc = DateTime.UtcNow;
            plugin.UpdateConfiguration(cfg);
            throw;
        }
        catch (Exception ex)
        {
            var detail = ex.ToString();
            cfg.LastScanStatus = "Failed To Execute";
            cfg.LastScanError = detail.Length > 700 ? detail.Substring(0, 700) : detail;
            cfg.LastScanFinishedUtc = DateTime.UtcNow;
            plugin.UpdateConfiguration(cfg);
            _logger.LogError(ex, "Advanced Metadata scan failed");
            throw;
        }
    }

   private BaseItem? FindByName(string name, BaseItemKind kind)
    {
        if (string.IsNullOrWhiteSpace(name)) { return null; }

        try
        {
            return _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { kind },
                Name = name,
                Recursive = true,
                Limit = 1
            }).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static Guid? ResolveAlbumId(BaseItem item)
    {
        var byProperty = GetGuidProperty(item, "AlbumEntityId") ?? GetGuidProperty(item, "AlbumId");
        if (byProperty.HasValue) { return byProperty; }
        if (item.ParentId != Guid.Empty) { return item.ParentId; }
        return null;
    }

    private static Guid? GetGuidProperty(BaseItem item, string name)
    {
        var p = item.GetType().GetProperty(name);
        if (p?.GetValue(item) is Guid g && g != Guid.Empty) { return g; }
        return null;
    }

    private static string? GetStringProperty(BaseItem item, string name)
    {
        var p = item.GetType().GetProperty(name);
        return p?.GetValue(item) as string;
    }

    private static string? GetStringList(BaseItem item, string name)
    {
        var p = item.GetType().GetProperty(name);
        if (p?.GetValue(item) is string[] arr && arr.Length > 0)
        {
            return string.Join(" / ", arr.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        return null;
    }

    private static string? ArtistNames(BaseItem? item)
    {
        if (item is null) { return null; }

        var direct = GetStringList(item, "Artists");
        if (!string.IsNullOrWhiteSpace(direct)) { return direct; }

        var p = item.GetType().GetProperty("ArtistItems");
        if (p?.GetValue(item) is System.Collections.IEnumerable list)
        {
            var names = new List<string>();
            foreach (var e in list)
            {
                var n = e?.GetType().GetProperty("Name")?.GetValue(e) as string;
                if (!string.IsNullOrWhiteSpace(n)) { names.Add(n); }
            }

            if (names.Count > 0) { return string.Join(" / ", names); }
        }

        return null;
    }

    private static string ArtistItemId((Guid Id, string? Name) a)
        => a.Id != Guid.Empty
            ? a.Id.ToString("N")
            : "artist-" + (a.Name ?? "unknown").ToLowerInvariant().Replace(' ', '-');

    private static IEnumerable<(Guid Id, string? Name)> GetAlbumArtists(BaseItem item)
    {
        var p = item.GetType().GetProperty("AlbumArtists");
        if (p?.GetValue(item) is System.Collections.IEnumerable list)
        {
            foreach (var entry in list)
            {
                if (entry is null) { continue; }
                var idProp = entry.GetType().GetProperty("Id");
                var nameProp = entry.GetType().GetProperty("Name");
                if (idProp?.GetValue(entry) is Guid g && g != Guid.Empty)
                {
                    yield return (g, nameProp?.GetValue(entry) as string);
                }
            }
        }
    }

    private static IEnumerable<(Guid Id, string? Name)> GetArtists(BaseItem item)
    {
        var p = item.GetType().GetProperty("ArtistItems");
        if (p?.GetValue(item) is System.Collections.IEnumerable list)
        {
            foreach (var entry in list)
            {
                if (entry is null) { continue; }
                var idProp = entry.GetType().GetProperty("Id");
                var nameProp = entry.GetType().GetProperty("Name");
                if (idProp?.GetValue(entry) is Guid g && g != Guid.Empty)
                {
                    yield return (g, nameProp?.GetValue(entry) as string);
                }
            }
        }
    }

/// <summary>Leading number of the first value that carries one, else zero.</summary>
    private static int LeadInt(List<string>? values)
    {
        if (values is not null)
        {
            foreach (var value in values)
            {
                var s = (value ?? string.Empty).Trim();
                var cut = 0;
                while (cut < s.Length && char.IsDigit(s[cut])) { cut++; }
                if (cut > 0
                    && int.TryParse(s.Substring(0, cut), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                    && n > 0)
                {
                    return n;
                }
            }
        }

       return 0;
    }

   private static Dictionary<string, List<string>> ReadRawTags(string path, IEnumerable<string> tags, out bool ok)
    {
        ok = false;
        var wanted = tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in wanted) { result[t] = new List<string>(); }

        try
        {
            using var f = TagLib.File.Create(path);
            var t = f.Tag;
            var id3 = f.GetTag(TagLib.TagTypes.Id3v2) as TagLib.Id3v2.Tag;
            var xiph = f.GetTag(TagLib.TagTypes.Xiph) as TagLib.Ogg.XiphComment;

            foreach (var name in wanted)
            {
                var up = name.ToUpperInvariant();
                var list = result[name];

                // The file's own tag wins. A FLAC or OGG carries a Vorbis comment, and a stray
                // ID3v2 block beside it often still holds frames that have already been deleted,
                // so reading the combined tag resurrects them.
                if (xiph is not null)
                {
                    var field = xiph.GetField(up);
                    if (field is not null) { list.AddRange(field); }

                    if (up == "TRACKNUMBER" && list.Count == 0 && t.Track > 0)
                    {
                        list.Add(t.Track.ToString(CultureInfo.InvariantCulture));
                    }

                    if (up == "DISCNUMBER" && list.Count == 0 && t.Disc > 0)
                    {
                        list.Add(t.Disc.ToString(CultureInfo.InvariantCulture));
                    }

                    if (up == "YEAR" && list.Count == 0 && t.Year > 0)
                    {
                        list.Add(t.Year.ToString(CultureInfo.InvariantCulture));
                    }

                    continue;
                }

                switch (up)
                {
                    case "ARTIST": list.AddRange(t.Performers); break;
                    case "ALBUMARTIST": list.AddRange(t.AlbumArtists); break;
                    case "ALBUM": if (!string.IsNullOrWhiteSpace(t.Album)) { list.Add(t.Album); } break;
                    case "TITLE": if (!string.IsNullOrWhiteSpace(t.Title)) { list.Add(t.Title); } break;
                    case "GENRE": list.AddRange(t.Genres); break;
                    case "COMPOSER": list.AddRange(t.Composers); break;
                    case "YEAR": if (t.Year > 0) { list.Add(t.Year.ToString(CultureInfo.InvariantCulture)); } break;
                    case "TRACKNUMBER": if (t.Track > 0) { list.Add(t.Track.ToString(CultureInfo.InvariantCulture)); } break;
                    case "DISCNUMBER": if (t.Disc > 0) { list.Add(t.Disc.ToString(CultureInfo.InvariantCulture)); } break;
                }

                if (id3 is not null)
                {
                    foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                    {
                        if (string.Equals(frame.Description, up, StringComparison.OrdinalIgnoreCase))
                        {
                            list.AddRange(frame.Text);
                        }
                    }
                }
            }

            ok = true;
        }
        catch
        {
            // unreadable file; every mapping simply sees no values
        }

        return result;
    }

    private static List<string> SplitValues(Dictionary<string, List<string>> raw, string tag, char[] delimiters)
    {
        if (!raw.TryGetValue(tag.Trim(), out var list) || list.Count == 0) { return new List<string>(); }

        return list
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .SelectMany(v => v.Split(delimiters, StringSplitOptions.RemoveEmptyEntries))
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
