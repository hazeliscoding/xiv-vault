# Roadmap

XIVault is a Windows-first backup and restore utility for the portable XIVLauncher / Dalamud configuration, with a CLI and an Avalonia desktop app on one shared engine. This file tracks what gets built, in what order, and the decisions already made.

## Decisions (2026-10-04)

- **Stack:** .NET 10 (LTS), C#, Avalonia 12 with CommunityToolkit.Mvvm, Spectre.Console.Cli, System.Text.Json source generation, xUnit v3 on Microsoft.Testing.Platform. Microsoft.Extensions DI and logging abstractions are the only other dependencies.
- **One engine.** All behavior lives in `XIVault.Core`. The CLI and the desktop app only present it, and both wire it up with `AddXivaultCore()`.
- **License:** Apache-2.0.
- **Backup format:** a normal ZIP with `manifest.json` and a `payload/` folder. The manifest is versioned (`schemaVersion: 1`), lists every file with its SHA-256 and is the only source of metadata. The file name is not trusted.
- **Allowlist:** `pluginConfigs/`, `dalamudConfig.json`, `dalamudVfs.db`, and `dalamudUI.ini` when the user opts in. Temporary files and `logs`/`cache` folders inside `pluginConfigs/` are skipped, and links are not followed. Nothing else is ever read.
- **Backups are atomic.** The archive is written as `*.zip.tmp`, closed, re-read and hash-checked, then renamed. Retention runs only after that rename.
- **Restore never deletes.** A plugin config that exists now but not in the backup stays. Each file is replaced through a temp file and a rename. If anything fails mid-restore, the files already replaced are put back from a local copy and the restore reports failure.
- **Safety snapshots** are `pre-restore-*.zip` files in the backup destination, shown in Backups as Pre-Restore. They always include `dalamudUI.ini` when it exists, because a restore may replace it. The newest 3 are kept, separately from the regular retention count.
- **Restore targets need XIVLauncher, not Dalamud config.** A fresh PC that has only run XIVLauncher is a valid restore target. Backups need at least one portable item to exist.
- **Scheduling** uses Windows Task Scheduler (`schtasks /create /xml`) with one task, `XIVault Scheduled Backup`, running as the current user with least privilege. Turning automatic backups off removes the task and keeps the preferences. XIVault computes the next run itself and the scheduled run records its own result, so nothing depends on the locale of `schtasks` output.
- **The task runs the app that created it.** The desktop app registers itself with `--scheduled-backup` and runs without a window. The CLI registers `xivault backup --scheduled`.
- **Scheduled backups wait for FFXIV to close** (checking every minute, for up to 6 hours) instead of copying a database the game has open. Manual backups run at once.
- **Integrity state** is cached per archive (path, size, modified time) in `%LOCALAPPDATA%\XIVault\state.json`. Archives that were never verified on this PC are verified in the background when listed.
- **Default destination:** `%OneDrive%\XIVault` when OneDrive is set up, otherwise `Documents\XIVault`.
- **UI** implements the Claude Design "XIVault" mockup, built on the Quorum design system with XIVault's overrides: IBM Plex Sans and Mono, accent `#6C9EFF`, healthy `#3CD5DE`. Dark only, like the mockup. Fonts are bundled TTFs (OFL) and icons are Lucide paths (ISC) compiled into the app, so nothing loads from a CDN.
- **The mockup's sample values are not used.** Version is 0.1.0, not the mockup's 1.4.0, and every number on screen comes from `XIVault.Core`.
- **Compression** follows the mockup: Fast, Balanced (default) and Maximum map to the .NET `CompressionLevel` values.
- **Brand:** the diamond from the mockup's title bar. The wordmark is IBM Plex Sans SemiBold converted to paths. Assets are in `docs/brand/`, and `scripts/make-icons.cs` builds `xivault.ico` from `app-icon.svg`.

- **Screenshots** come from `tools/XIVault.Screenshots`, which renders the real views with Avalonia's headless Skia renderer against a fake XIVLauncher folder and a fake Task Scheduler. It is also how the UI was checked against the mockup.
- **Fresh-PC states** that the mockup doesn't cover follow its patterns: "Setting up a new PC?" (XIVLauncher present, no Dalamud settings) leads to Restore, and "XIVLauncher not found" leads to Settings. The restore wizard also accepts a backup file picked from anywhere.
- **Restore steps:** the review screen compares the backup with this PC and warns when plugins changed after the backup. The live restore runs inside step 3, as in the mockup, with stages in the order the engine runs them: verify, snapshot, plugin configs, Dalamud settings.

## M0: Bootstrap

- [x] Solution with `XIVault.Core`, `XIVault.Cli`, `XIVault.Desktop` and `XIVault.Tests`.
- [x] Brand assets and app icon.
- [x] CI on `windows-latest`: restore, build, test, `dotnet format --verify-no-changes`.

**Done when:** CI is green on a pull request.

## M1: Core engine

- [x] Configuration (`%LOCALAPPDATA%\XIVault\config.json`) and app state.
- [x] `IXivLauncherLocator` / `WindowsXivLauncherLocator`: override, known paths, layout detection (`dalamudUserData`), validation.
- [x] Backup: allowlist, ZIP + manifest, SHA-256, atomic finalize, retention.
- [x] Archive validation: schema, hashes, traversal, absolute paths, unknown files.
- [x] Restore: process check, safety snapshot, temp extraction, allowlisted apply, rollback on failure.

**Done when:** tests back up a fake XIVLauncher folder, restore it, and the traversal, checksum, running-process and partial-failure cases fail safely.

## M2: CLI

- [x] `backup`, `restore`, `list`, `status`, `doctor`, `schedule`, `config`, `version`, with `--json`, `--quiet` and `--verbose` where they apply.
- [x] Stable exit codes, documented in the README.

**Done when:** `xivault backup`, `list`, `status`, `doctor` and `restore latest` work against a fake XIVLauncher folder.

## M3: Scheduling and diagnostics

- [x] Task Scheduler integration: daily, weekly, at Windows login.
- [x] Scheduled run: waits for FFXIV, records its result.
- [x] Diagnostics service and a report that holds paths, versions and results only.

**Done when:** `xivault schedule weekly --day Sunday` creates the task, `schedule status` reads it back, and `doctor` reports all four groups.

## M4: Desktop

- [x] Shell: window chrome, navigation, theme tokens, fonts, icons, shared components.
- [x] Overview, Backups, Restore wizard, Schedule, Diagnostics, Settings on real Core data.
- [x] Polish: keyboard and focus, screen-reader names, reduced motion, empty and error states, confirmations.

**Done when:** from a clean state the app detects XIVLauncher, backs up, shows the backup in history, schedules weekly backups and restores a previous backup through the wizard.

## M5: Release v0.1.0

- [ ] Self-contained win-x64 publish for both apps: `xivault-cli-win-x64.zip`, `XIVault.Desktop-win-x64.zip`.
- [ ] Release workflow on `v*` tags.
- [ ] README with screenshots, `docs/architecture.md`, `docs/backup-format.md`, `docs/troubleshooting.md`.
- [ ] End-to-end smoke test against a fake XIVLauncher folder.

**Done when:** pushing `v0.1.0` publishes both zips to a draft GitHub release.

## Later

- Winget manifest.
- Code signing.
- Restoring a chosen subset of plugin configs.
- Light theme.

## Not planned

- Copying plugin binaries, or installing plugins.
- Backing up the game itself.
- Cloud APIs (Google Drive, Dropbox, OneDrive, S3). A folder synced by their own clients is enough.
- Accounts, telemetry, a web dashboard.
- A Windows service or tray daemon.
- Plugin-specific backup adapters, or a Dalamud plugin version of XIVault.
