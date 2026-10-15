# Command line reference

Everything `xiv-vault` can do, and what stays the same for scripts. The README's [Command line](../README.md#command-line) section has the everyday examples.

## What stays the same in 1.x

From 1.0 on, scripts can rely on these until the next major version:

- **Commands and options.** Every command, option and argument below keeps working and keeps its meaning. New ones may be added.
- **Exit codes.** Each code keeps its meaning. New codes may be added for new kinds of failure.
- **JSON output.** Fields are never renamed or removed and keep their type, and the values listed under [Values](#values) keep their meaning. New fields and new values may be added, so skip what you don't recognize.
- **Settings.** `config set` keys and values keep working, and settings carry over through every update.

Text meant for people can change in any release: tables, messages, the `doctor --report` text, and the JSON fields that hold wording (`message`, `problem`, `attentionReason`, `summary`, `title`, `label` and `detail`).

### What a major version may change

A major version may rename or remove commands, options, JSON fields or exit codes, and the CHANGELOG lists each one. Two things never change, even then:

- Every backup made by any version of XIV Vault restores in every later version. See [backup-format.md](backup-format.md#compatibility).
- A scheduled task set up by an earlier version keeps working after an update, so `XIV-Vault.exe --scheduled-backup` and `xiv-vault backup --scheduled` stay.

## Commands

Every command also takes `--verbose` (each step as it happens) and `--help`.

| Command | Options |
|---|---|
| `backup` | `--destination <PATH>`, `--source <PATH>` (an XIVLauncher folder), `--quiet`, `--scheduled` (what the scheduled task runs: wait for FFXIV to close, record the result) |
| `restore [BACKUP]` | `BACKUP` is `latest`, a file name in the backup folder, or a path; leave it out to choose from a list. `--yes`, `--source <PATH>`, `--plugin <NAME>` (repeatable), `--dalamud-settings`, `--character-settings`, `--system-settings`. With none of the last four, everything in the backup is restored. |
| `list` | `--json`, `--verify` (check every backup, not just the latest) |
| `status` | `--json` |
| `doctor` | `--json`, `--report` (plain text to paste into an issue) |
| `schedule daily` | `--time <HH:MM>` |
| `schedule weekly` | `--day <DAY>` (repeatable), `--time <HH:MM>` |
| `schedule logon` | |
| `schedule status` | `--json` |
| `schedule remove` | |
| `config`, `config show` | `--json` |
| `config set <KEY> <VALUE>` | `destination` (a folder), `retention` (a number of backups), `include-ui` and `include-game` (`yes` or `no`), `compression` (`fast`, `balanced` or `maximum`), `source` (an XIVLauncher folder, or `auto`) |
| `config path` | |
| `version` | |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Unexpected failure |
| 2 | Invalid arguments or configuration |
| 3 | XIVLauncher not found |
| 4 | Backup validation failure |
| 5 | Restore validation failure |
| 6 | XIVLauncher or FFXIV is running |
| 7 | Backup folder unavailable |

## JSON output

`--json` prints one JSON value to standard output. Times ending in `Utc` are UTC in ISO 8601; `nextRunLocal` is this PC's local time. Sizes are in bytes.

### `status --json`

```json
{
  "version": "1.0.0",
  "state": "protected",
  "attentionReason": null,
  "xivLauncher": {
    "found": true,
    "path": "C:\\Users\\you\\AppData\\Roaming\\XIVLauncher",
    "layout": "standard",
    "version": "7.0.20",
    "hasConfiguration": true,
    "message": "XIVLauncher found at C:\\Users\\you\\AppData\\Roaming\\XIVLauncher."
  },
  "pluginConfigCount": 67,
  "destination": "C:\\Users\\you\\OneDrive\\XIV Vault",
  "destinationAvailable": true,
  "latestBackup": {
    "file": "xiv-vault-2026-10-11-193319.zip",
    "path": "C:\\Users\\you\\OneDrive\\XIV Vault\\xiv-vault-2026-10-11-193319.zip",
    "createdAtUtc": "2026-10-12T00:33:19Z",
    "kind": "manual",
    "sizeBytes": 1892040,
    "pluginConfigCount": 67,
    "integrity": "verified",
    "onlineOnly": false,
    "problem": null,
    "characterCount": 2,
    "systemSettings": true
  },
  "backupCount": 10,
  "totalBytes": 18920400,
  "retentionCount": 10,
  "schedule": {
    "installed": true,
    "frequency": "weekly",
    "days": ["sunday"],
    "time": "18:30",
    "nextRunLocal": "2026-10-18T18:30:00",
    "lastRun": {
      "atUtc": "2026-10-11T23:30:12Z",
      "result": "success",
      "message": "Successful",
      "backupFile": "xiv-vault-2026-10-11-183012.zip"
    }
  },
  "gameSettings": {
    "found": true,
    "included": true,
    "characterCount": 2,
    "systemSettings": true
  }
}
```

`latestBackup` is null before the first backup, and `schedule.lastRun` before the first scheduled run. `xivLauncher.path`, `layout` and `version` are null when XIVLauncher isn't found, and `pluginConfigCount` when there is no Dalamud configuration.

### `list --json`

An array of backups, newest first, each shaped like `latestBackup` above. For a backup that is only in the cloud and hasn't been read on this PC, `pluginConfigCount`, `characterCount` and `systemSettings` are null.

### `schedule status --json`

The `schedule` object from `status --json`. When no task is installed, `installed` is false and `frequency`, `time` and `nextRunLocal` are null. `days` is empty unless the schedule is weekly, and `time` is null for `logon`.

### `doctor --json`

```json
{
  "version": "1.0.0",
  "generatedAtUtc": "2026-10-12T00:33:21.3681078Z",
  "summary": "17 checks passed",
  "status": "healthy",
  "groups": [
    {
      "area": "dalamud",
      "title": "Dalamud",
      "status": "healthy",
      "checks": [
        { "label": "Configuration file found", "status": "healthy", "detail": "dalamudConfig.json" }
      ]
    }
  ]
}
```

There is one group for each `area`. Paths in `detail` start with `%USERPROFILE%` instead of your user folder.

### `config show --json`

```json
{
  "backupDestination": "C:\\Users\\you\\OneDrive\\XIV Vault",
  "retentionCount": 10,
  "includeDalamudUi": false,
  "includeGameSettings": true,
  "compression": "balanced",
  "xivLauncherPathOverride": null,
  "schedule": {
    "enabled": true,
    "frequency": "weekly",
    "days": ["sunday"],
    "time": "18:30"
  },
  "checkForUpdates": true,
  "getBetaVersions": false
}
```

`backupDestination` is the default folder unless you chose one, and `xivLauncherPathOverride` is null while XIV Vault finds XIVLauncher itself. `schedule` is the schedule you chose, whether or not the task is installed.

`getBetaVersions` is the installed desktop app's opt-in to GitHub prereleases, off by default. Turning it off never downgrades the app. The CLI has no updater; change this preference in the app's **Settings → Updates**.

### Values

| Field | Values |
|---|---|
| `state` | `protected`, `notBackedUp`, `needsAttention`, `noConfiguration`, `notDetected` |
| `kind` | `manual`, `scheduled`, `preRestore`, `unknown` |
| `integrity` | `verified`, `unverified`, `failed` |
| `xivLauncher.layout` | `standard`, `dalamudUserData` |
| `frequency` | `daily`, `weekly`, `logon` |
| `days` | `sunday` to `saturday` |
| `lastRun.result` | `success`, `skipped`, `failed` |
| `compression` | `fast`, `balanced`, `maximum` |
| `doctor` `status` | `healthy`, `warning`, `error` |
| `doctor` `area` | `xivLauncher`, `dalamud`, `gameSettings`, `backupDestination`, `scheduling` |

## Beta-only options

Beta versions of XIV Vault, such as `1.1.0-beta.1`, can also install the plugins listed in the restored Dalamud settings. Two `restore` options turn this on:

- `--install-plugins` installs the plugins this PC doesn't have yet, from Dalamud's official repository. Plugins are installed only along with the Dalamud settings, so a selection without them, such as `--plugin <NAME>` alone, is refused. A plugin whose settings are in the backup but left out of the selection isn't installed.
- `--custom-repository <HOST>` (repeatable, with `--install-plugins`) also installs from a custom repository that the backup's Dalamud settings turn on. Name it by its host, as the restore's review lists it. Naming a host includes every repository at that host.

The review lists missing plugins to look for in the official repository and the custom repositories you choose before anything changes. Afterwards, the restore names each plugin it couldn't install and why, so you can install it from `/xlplugins` in the game. If Dalamud hasn't finished downloading, the restore installs no plugins and says to open XIVLauncher, let it finish, close it and restore again. Plugins that aren't installed don't change the exit code.

`--help` doesn't list either option, and stable versions refuse both with exit code 2. They aren't part of [What stays the same in 1.x](#what-stays-the-same-in-1x) until a stable version lists them.
