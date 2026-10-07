# Architecture

XIV Vault is one engine with two front ends. Everything that touches backups lives in `XivVault.Core`; the CLI and the desktop app only present it.

```
                 ┌──────────────────────────┐
                 │       XivVault.Core       │
                 │  discovery · backup      │
                 │  validation · restore    │
                 │  retention · scheduling  │
                 │  diagnostics · status    │
                 └────────────┬─────────────┘
               ┌──────────────┴──────────────┐
      ┌────────┴────────┐          ┌─────────┴─────────┐
      │   XivVault.Cli   │          │  XivVault.Desktop  │
      │ Spectre.Console │          │ Avalonia · MVVM   │
      └─────────────────┘          └───────────────────┘
```

Both front ends call `services.AddXivVaultCore()` and resolve the same services from Microsoft.Extensions.DependencyInjection.

## Projects

| Project | Role |
|---|---|
| `src/XivVault.Core` | The engine. No UI code. |
| `src/XivVault.Cli` | `xiv-vault.exe`: commands, output formatting, exit codes. |
| `src/XivVault.Desktop` | `XIV-Vault.exe` (the `XivVault` assembly, renamed when published): views, view models, theme. Also the headless `--scheduled-backup` entry point, Velopack's install and uninstall hooks, and updates. |
| `tests/XivVault.Tests` | Core, CLI and view model tests. All run in temp folders. |
| `tools/XivVault.Screenshots` | Renders every desktop screen to PNG with Avalonia's headless Skia renderer, against a fake XIVLauncher folder. With `--demo`, records the README's GIF: a real backup and restore of the fake profile, slowed so the progress shows, encoded with ffmpeg. |

## Core services

| Service | Responsibility |
|---|---|
| `IConfigStore` / `ConfigStore` | `%LOCALAPPDATA%\XIV Vault\config.json`: destination, retention, UI layout option, compression, XIVLauncher override, schedule preferences. |
| `IStateStore` / `StateStore` | `state.json`: cached verification results and the last scheduled run. Losing it is harmless. |
| `IXivLauncherLocator` / `WindowsXivLauncherLocator` | Finds the XIVLauncher folder: explicit override, then known paths (`%AppData%\XIVLauncher`, `XIVLauncherCN`), then layout detection (`dalamudUserData`), then validation. A folder only counts if it holds XIVLauncher or Dalamud files. |
| `PortableStateScanner` | Lists the allowlisted files in a Dalamud data folder. Metadata only. |
| `BackupAllowlist`, `ArchivePaths` | The allowlist, and the rules for safe archive paths. |
| `BackupService` | Writes `*.zip.tmp`, verifies it, renames it, applies retention. |
| `ArchiveValidator` | Decides whether an archive can be trusted: manifest, schema, paths, allowlist, every hash. |
| `BackupCatalog` | Lists archives in a folder from their manifests, or by name while they are online only; verifies the latest one, or any one on request, and caches the result; downloads and deletes them. |
| `RetentionService` | Keeps the newest N regular backups and the newest 3 pre-restore snapshots, separately. Damaged archives are neither counted nor deleted. Online-only archives are never downloaded: they don't count, and are deleted unopened once N verified backups are newer. |
| `RestoreService` | Preview, safety checks and the restore itself. |
| `GameProcessGuard` | Detects XIVLauncher and FFXIV processes. |
| `WindowsTaskScheduler`, `ScheduleService` | The `XIV Vault Scheduled Backup` task through `schtasks /xml`, kept in step with the saved preferences. |
| `ScheduledBackupRunner` | What the task runs: waits for FFXIV, backs up, records the result. |
| `StatusService` | The protection summary the Overview and `xiv-vault status` show. |
| `DiagnosticsService` | The four health-check groups and the shareable report. |

`OperationLock` is a named mutex per data folder. Backups, restores and retention take it, so the scheduled task and the app never work on the backup folder at the same time.

Platform access goes through small interfaces so tests can replace it: `IAppEnvironment` (folders and user), `IProcessInspector`, `ICommandRunner` (schtasks), `IFileAvailability` (whether a file is online only) and `TimeProvider`. `XIV_VAULT_DATA_DIR` redirects config, state and logs, which the smoke test uses.

## Backup flow

1. Load config, locate XIVLauncher, scan the allowlisted files.
2. Create the destination folder; failure there is "destination unavailable" (exit 7).
3. Stream each file into `xiv-vault-<time>.zip.tmp`, hashing it on the way, then write `manifest.json`.
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

The theme in `Themes/` reproduces the approved mockup's tokens. `Motion.axaml` holds every animation and is only loaded when Windows animations are on. Icons are Lucide paths compiled into `LucideIcons.cs`; fonts are bundled IBM Plex TTFs. Nothing loads from the network; the update check is the app's only network call.

## Installer and updates

The desktop app is packaged with [Velopack](https://velopack.io). An installed copy lives in `%LOCALAPPDATA%\XivVault`: `Update.exe`, a launcher stub, and the app itself in `current\`. Updates replace the contents of `current\`, so `current\XIV-Vault.exe`, which the scheduled task runs, keeps its path. Settings, state and logs stay in `%LOCALAPPDATA%\XIV Vault`.

`Program.Main` runs `VelopackApp` before anything else. It handles Setup's and the uninstaller's hooks and exits:

- The uninstall hook runs `UninstallCleanup`, which removes the scheduled task if it starts this installed copy.
- Downloaded updates are never applied at startup (`SetAutoApplyOnStartup(false)`), so a scheduled run never replaces its own program.

`IAppUpdater` / `VelopackUpdater` reads the feed (`releases.win.json` on the GitHub releases) and downloads and applies updates; portable copies report themselves as not installed. `UpdatesViewModel`, shared by Settings and the sidebar, runs the check when the app opens if `checkForUpdates` is on, and **Update and Restart**. Installing ends every process running from `current\`, so an update waits while `DesktopSession` is busy, another copy of the app runs (`IAppInstances`), or another process holds `OperationLock`. It checks before downloading and again just before restarting.

To try an update without publishing a release, pack two versions into one folder with `dotnet vpk pack` (as `release.yml` does), install the older version's Setup, and start it with `XIV_VAULT_UPDATE_SOURCE` set to that folder.

## Releases

`release.yml` runs on `v*` tags: tests, self-contained single-file publish for win-x64, the smoke test against the published CLI, Setup and the update packages from `vpk` (pinned in `dotnet-tools.json`, with a delta from the previous published release), zips plus `SHA256SUMS`, then a draft GitHub release from a separate job that holds the only write permission. Installed copies only see published releases, so a draft never reaches them.
