# Backup format

An XIV Vault backup is an ordinary ZIP file. Any ZIP tool can open it. The manifest inside is the only source of metadata; the file name is just for people.

## File names

| Name | Made by |
|---|---|
| `xiv-vault-YYYY-MM-DD-HHmmss.zip` | Back Up Now, `xiv-vault backup`, scheduled backups |
| `pre-restore-YYYY-MM-DD-HHmmss.zip` | The safety snapshot every restore takes first |

Times are local. A second backup in the same second gets `-2`, `-3` and so on. While a backup is being written it is named `….zip.tmp`; it is renamed only after it has been verified.

## Layout

```
xiv-vault-2026-10-04-131900.zip
├── manifest.json
└── payload/
    ├── dalamudConfig.json
    ├── dalamudVfs.db
    ├── dalamudUI.ini            (only when the UI layout option is on)
    ├── pluginConfigs/
    │   ├── Artisan.json
    │   └── AutoRetainer/
    │       └── profiles/main.json
    └── game/                    (the game's own settings, version 2 and later)
        ├── FFXIV.cfg
        ├── MACROSYS.dat
        ├── FFXIV_CHARA_01.dat
        └── FFXIV_CHR004000174A1B2C3D/
            ├── HOTBAR.DAT
            └── KEYBIND.DAT
```

Entry names use forward slashes and are relative. Every file under `payload/` is listed in the manifest, and nothing else is in the archive.

`payload/game/` comes from `Documents\My Games\FINAL FANTASY XIV - A Realm Reborn`: `FFXIV.cfg` (system settings such as graphics and sound), `MACROSYS.dat` (shared macros), the `FFXIV_CHARA_*.dat` appearance saves, and the `.DAT` files directly inside each `FFXIV_CHR…` character folder (HUD layout, hotbars, keybinds, macros, gear sets). The files are copied byte for byte and never parsed. A character folder is named by a content ID that identifies the character, so XIV Vault never shows or logs it; it counts characters instead.

## manifest.json

```json
{
  "schemaVersion": 2,
  "xivVaultVersion": "0.1.0",
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
    "dalamudUi": false,
    "characterSettings": true,
    "systemSettings": true
  },
  "statistics": {
    "pluginConfigCount": 43,
    "pluginConfigDirectories": 12,
    "characterCount": 2,
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
| `schemaVersion` | Format version. This document describes version 2; see Compatibility for version 1. |
| `xivVaultVersion` | The XIV Vault version that wrote the archive. |
| `createdAtUtc` | When the backup was taken, UTC, whole seconds. |
| `backupType` | `manual`, `scheduled` or `preRestore`. |
| `source.layout` | `standard` or `dalamudUserData`: where Dalamud kept its files. Informational; a restore follows the target PC's layout. |
| `contents` | Which allowlisted items are present. `characterSettings` covers everything under `game/` apart from `FFXIV.cfg`, which is `systemSettings`. |
| `statistics.pluginConfigCount` | Distinct plugins in `pluginConfigs/`. `Foo.json` and `Foo/` count as one plugin. |
| `statistics.characterCount` | Distinct character folders under `game/`. |
| `files[].path` | Archive path, always under `payload/`. |
| `files[].size` | Uncompressed size in bytes. |
| `files[].sha256` | Lowercase hex SHA-256 of the uncompressed file. |

## Validation

An archive is restored only if all of these hold:

- It opens as a ZIP and has exactly one `manifest.json`.
- The manifest parses, has all required fields, and `schemaVersion` is supported. A missing version counts as unsupported; a higher one means "made by a newer XIV Vault".
- Every path is relative and safe: no `..`, no leading `/`, no drive letters or `:`, no backslashes, no device names such as `NUL`, no trailing dots or spaces.
- Every manifest path is on the allowlist: `payload/dalamudConfig.json`, `payload/dalamudVfs.db`, `payload/dalamudUI.ini`, a file under `payload/pluginConfigs/`, or one of the game files above under `payload/game/`. Paths that backups skip are refused too: `logs`, `cache` and `temp` folders, temporary files such as `*.tmp`, and in the game folder chat logs, screenshots, `cfgcopy`, `*.old` and `FFXIV_BOOT.cfg`. A version 1 archive can't hold game files.
- `contents` and `statistics` match the file list, so the restore review describes what would actually be written.
- Every file in the archive is in the manifest, and every manifest file is in the archive, once.
- Sizes and SHA-256 hashes match.
- The declared total is at most 2 GB and the archive lists at most 100,000 files, so a crafted archive can't fill the disk or tie up the PC.

Restore re-checks every hash after extracting to a temp folder, so an archive changed after validation is still caught.

## Compatibility

Version 1 archives will stay restorable by later versions. Version 1 is version 2 without `payload/game/`, `contents.characterSettings`, `contents.systemSettings` and `statistics.characterCount`; a version 1 manifest that lacks them reads as having no game settings. XIV Vault 0.3 and earlier refuse version 2 archives as "made by a newer XIV Vault". A future format will get a new `schemaVersion`, and this document will describe it.
