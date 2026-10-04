# Architecture

XIV Vault is one engine with two front ends. Everything that touches backups lives in `XIVault.Core`; the CLI and the desktop app only present it.

```
                 ┌──────────────────────────┐
                 │       XIVault.Core       │
                 │  discovery · backup      │
                 │  validation · restore    │
                 │  retention · scheduling  │
                 │  diagnostics · status    │
                 └────────────┬─────────────┘
               ┌──────────────┴──────────────┐
      ┌────────┴────────┐          ┌─────────┴─────────┐
      │   XIVault.Cli   │          │  XIVault.Desktop  │
      │ Spectre.Console │          │ Avalonia · MVVM   │
      └─────────────────┘          └───────────────────┘
```

Both front ends call `services.AddXivaultCore()` and resolve the same services from Microsoft.Extensions.DependencyInjection.

## Projects

| Project | Role |
|---|---|
| `src/XIVault.Core` | The engine. No UI code. |
| `src/XIVault.Cli` | `xivault.exe`: commands, output formatting, exit codes. |
| `src/XIVault.Desktop` | `XIVault.Desktop.exe`: views, view models, theme. Also the headless `--scheduled-backup` entry point. |
| `tests/XIVault.Tests` | Core, CLI and view model tests. All run in temp folders. |
| `tools/XIVault.Screenshots` | Renders every desktop screen to PNG with Avalonia's headless Skia renderer, against a fake XIVLauncher folder. |

## Core services

| Service | Responsibility |
|---|---|
| `IConfigStore` / `ConfigStore` | `%LOCALAPPDATA%\XIVault\config.json`: destination, retention, UI layout option, compression, XIVLauncher override, schedule preferences. |
| `IStateStore` / `StateStore` | `state.json`: cached verification results and the last scheduled run. Losing it is harmless. |
| `IXivLauncherLocator` / `WindowsXivLauncherLocator` | Finds the XIVLauncher folder: explicit override, then known paths (`%AppData%\XIVLauncher`, `XIVLauncherCN`), then layout detection (`dalamudUserData`), then validation. A folder only counts if it holds XIVLauncher or Dalamud files. |
| `PortableStateScanner` | Lists the allowlisted files in a Dalamud data folder. Metadata only. |
| `BackupAllowlist`, `ArchivePaths` | The allowlist, and the rules for safe archive paths. |
| `BackupService` | Writes `*.zip.tmp`, verifies it, renames it, applies retention. |
| `ArchiveValidator` | Decides whether an archive can be trusted: manifest, schema, paths, allowlist, every hash. |
| `BackupCatalog` | Lists archives in a folder from their manifests, verifies them and caches the result, deletes them. |
| `RetentionService` | Keeps the newest N regular backups and the newest 3 pre-restore snapshots, separately. Damaged archives are neither counted nor deleted. |
| `RestoreService` | Preview, safety checks and the restore itself. |
| `GameProcessGuard` | Detects XIVLauncher and FFXIV processes. |
| `WindowsTaskScheduler`, `ScheduleService` | The `XIV Vault Scheduled Backup` task through `schtasks /xml`, kept in step with the saved preferences. |
| `ScheduledBackupRunner` | What the task runs: waits for FFXIV, backs up, records the result. |
| `StatusService` | The protection summary the Overview and `xivault status` show. |
| `DiagnosticsService` | The four health-check groups and the shareable report. |

`OperationLock` is a named mutex per data folder. Backups, restores and retention take it, so the scheduled task and the app never work on the backup folder at the same time.

Platform access goes through small interfaces so tests can replace it: `IAppEnvironment` (folders and user), `IProcessInspector`, `ICommandRunner` (schtasks) and `TimeProvider`. `XIVAULT_DATA_DIR` redirects config, state and logs, which the smoke test uses.

## Backup flow

1. Load config, locate XIVLauncher, scan the allowlisted files.
2. Create the destination folder; failure there is "destination unavailable" (exit 7).
3. Stream each file into `xivault-<time>.zip.tmp`, hashing it on the way, then write `manifest.json`.
4. Flush to disk, re-open the archive and check every hash with `ArchiveValidator`.
5. Rename to `.zip`, record it as verified, run retention.

Any failure deletes the `.tmp`, so nothing half-written looks like a backup.

## Restore flow

1. Validate the archive completely; refuse on any issue (exit 5).
2. Locate the target. A folder where XIVLauncher has run but Dalamud hasn't is fine, which makes a fresh PC a valid target. Refuse if any folder or file the restore would write is a link, since backups don't follow links.
3. Refuse while XIVLauncher or FFXIV runs (exit 6).
4. Take a pre-restore snapshot into the backup folder, including the UI layout. No snapshot, no restore. The archive being restored stays open, read-shared only, until the end.
5. Extract the manifest's files into a private temp folder and re-hash them.
6. Check processes again, then replace files one by one: copy next to the target and rename over it. The original of each replaced file is kept aside.
7. On any failure, put every replaced file back and delete files the restore created. The snapshot is the second line of defense.
8. Only then apply snapshot retention, which never removes the new snapshot or the archive that was restored.

## Desktop

The desktop app follows MVVM with CommunityToolkit.Mvvm. View models take Core services and small UI interfaces (`IDialogService`, `IShellService`, `IClipboardService`, `IFilePicker`, `IUiThread`, `INavigator`), so they are tested without a window. `DesktopSession` holds state that outlives a screen: the latest status and a running backup.

The theme in `Themes/` reproduces the approved mockup's tokens. `Motion.axaml` holds every animation and is only loaded when Windows animations are on. Icons are Lucide paths compiled into `LucideIcons.cs`; fonts are bundled IBM Plex TTFs. Nothing loads from the network.

## Releases

`release.yml` runs on `v*` tags: tests, self-contained single-file publish for win-x64, the smoke test against the published CLI, zips plus `SHA256SUMS`, then a draft GitHub release from a separate job that holds the only write permission.
