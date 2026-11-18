# Advanced Metadata

A Jellyfin plugin that builds a searchable index of custom audio metadata
(ID3 / Vorbis tags) for the companion Advanced Search plugin.

**Jellyfin is never modified.** The plugin reads your files and Jellyfin's
metadata, and writes only to its own store.

## Status

Pre-release. Expect breakage between builds and keep a backup before upgrading.

Stable channel: `manifest.json`.
Pre-release channel: `dev-manifest.json` (add both as repository URLs; the
pre-release list is the one to test from, and reinstalling from the stable list
rolls you back).

## What it does

- Scans chosen libraries and indexes the tag fields you map.
- Groups results by artist, album or song so a mapping can be browsed the way
  the data is shaped.
- Lets you correct or delete an indexed value by hand; edits survive later scans.
- Never writes to your audio files.

## Install (manual, for testing)

1. Download `advanced-metadata.zip` from the release you want.
2. Unzip into your Jellyfin `plugins/AdvancedMetadata/` folder.
3. Restart Jellyfin.
4. Dashboard → Plugins → Advanced Metadata.

## Scanning

- **Standard scan** reads only new and changed files.
- **Full rescan** reads everything. It is the only scan that removes entries for
  files you have deleted or renamed, and the only one that picks up files added
  outside Jellyfin.

Scope either to a library to work on that library alone.

## Storage

Everything lives in one SQLite file under the plugin data folder. Nothing is
written into Jellyfin's own database, and removing the plugin leaves your files
untouched.

## Licence

MIT.
