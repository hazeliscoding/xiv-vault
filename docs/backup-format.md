# Backup format

An XIV Vault backup is an ordinary ZIP file. Any ZIP tool can open it. The manifest inside is the only source of metadata; the file name is just for people.

## File names

| Name | Made by |
|---|---|
| `xivault-YYYY-MM-DD-HHmmss.zip` | Back Up Now, `xivault backup`, scheduled backups |
| `pre-restore-YYYY-MM-DD-HHmmss.zip` | The safety snapshot every restore takes first |

Times are local. A second backup in the same second gets `-2`, `-3` and so on. While a backup is being written it is named `….zip.tmp`; it is renamed only after it has been verified.

## Layout

```
xivault-2026-10-04-131900.zip
├── manifest.json
└── payload/
    ├── dalamudConfig.json
    ├── dalamudVfs.db
    ├── dalamudUI.ini            (only when the UI layout option is on)
    └── pluginConfigs/
        ├── Artisan.json
        └── AutoRetainer/
            └── profiles/main.json
```

Entry names use forward slashes and are relative. Every file under `payload/` is listed in the manifest, and nothing else is in the archive.

## manifest.json

```json
{
  "schemaVersion": 1,
  "xivaultVersion": "0.1.0",
  "createdAtUtc": "2026-10-04T18:19:00Z",
  "backupType": "manual",
  "source": {
    "platform": "windows",
    "layout": "standard"
  },
  "contents": {
    "pluginConfigs": true,
    "dalamudConfig": true,
    "dalamudVfs": true,
    "dalamudUi": false
  },
  "statistics": {
    "pluginConfigCount": 43,
    "pluginConfigDirectories": 12,
    "fileCount": 61,
    "totalBytes": 18874368
  },
  "files": [
    {
      "path": "payload/dalamudConfig.json",
      "size": 48213,
      "sha256": "9f2a…c41e"
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `schemaVersion` | Format version. This document describes version 1. |
| `xivaultVersion` | The XIV Vault version that wrote the archive. |
| `createdAtUtc` | When the backup was taken, UTC, whole seconds. |
| `backupType` | `manual`, `scheduled` or `preRestore`. |
| `source.layout` | `standard` or `dalamudUserData`: where Dalamud kept its files. Informational; a restore follows the target PC's layout. |
| `contents` | Which allowlisted items are present. |
| `statistics.pluginConfigCount` | Distinct plugins in `pluginConfigs/`. `Foo.json` and `Foo/` count as one plugin. |
| `files[].path` | Archive path, always under `payload/`. |
| `files[].size` | Uncompressed size in bytes. |
| `files[].sha256` | Lowercase hex SHA-256 of the uncompressed file. |

## Validation

An archive is restored only if all of these hold:

- It opens as a ZIP and has exactly one `manifest.json`.
- The manifest parses, has all required fields, and `schemaVersion` is supported. A missing version counts as unsupported; a higher one means "made by a newer XIV Vault".
- Every path is relative and safe: no `..`, no leading `/`, no drive letters or `:`, no backslashes, no device names such as `NUL`, no trailing dots or spaces.
- Every manifest path is on the allowlist: `payload/dalamudConfig.json`, `payload/dalamudVfs.db`, `payload/dalamudUI.ini`, or a file under `payload/pluginConfigs/`. Paths that backups skip are refused too: `logs`, `cache` and `temp` folders, and temporary files such as `*.tmp`.
- `contents` and `statistics` match the file list, so the restore review describes what would actually be written.
- Every file in the archive is in the manifest, and every manifest file is in the archive, once.
- Sizes and SHA-256 hashes match.
- The declared total is at most 2 GB and the archive lists at most 100,000 files, so a crafted archive can't fill the disk or tie up the PC.

Restore re-checks every hash after extracting to a temp folder, so an archive changed after validation is still caught.

## Compatibility

Version 1 archives will stay restorable by later versions. A future format will get a new `schemaVersion`, and this document will describe both.
