# Roadmap

XIV Vault is a Windows-first backup and restore utility for the portable XIVLauncher / Dalamud configuration, with a CLI and an Avalonia desktop app on one shared engine. This file tracks what gets built, in what order, and the decisions already made.

## Decisions (2026-10-04)

- **Stack:** .NET 10 (LTS), C#, Avalonia 12 with CommunityToolkit.Mvvm, Spectre.Console.Cli, System.Text.Json source generation, xUnit v3 on Microsoft.Testing.Platform. Microsoft.Extensions DI and logging abstractions are the only other dependencies.
- **One engine.** All behavior lives in `XivVault.Core`. The CLI and the desktop app only present it, and both wire it up with `AddXivVaultCore()`.
- **License:** Apache-2.0.
- **Backup format:** a normal ZIP with `manifest.json` and a `payload/` folder. The manifest is versioned (`schemaVersion: 1`), lists every file with its SHA-256 and is the only source of metadata. The file name is not trusted.
- **Allowlist:** `pluginConfigs/`, `dalamudConfig.json`, `dalamudVfs.db`, and `dalamudUI.ini` when the user opts in. Temporary files and `logs`/`cache` folders inside `pluginConfigs/` are skipped, and links are not followed. Nothing else is ever read.
- **Backups are atomic.** The archive is written as `*.zip.tmp`, closed, re-read and hash-checked, then renamed. Retention runs only after that rename.
- **Restore never deletes.** A plugin config that exists now but not in the backup stays. Each file is replaced through a temp file and a rename. If anything fails mid-restore, the files already replaced are put back from a local copy and the restore reports failure.
- **Safety snapshots** are `pre-restore-*.zip` files in the backup destination, shown in Backups as Pre-Restore. They always include `dalamudUI.ini` when it exists, because a restore may replace it. The newest 3 are kept, separately from the regular retention count.
- **Restore targets need XIVLauncher, not Dalamud config.** A fresh PC that has only run XIVLauncher is a valid restore target. Backups need at least one portable item to exist.
- **Scheduling** uses Windows Task Scheduler (`schtasks /create /xml`) with one task, `XIV Vault Scheduled Backup`, running as the current user with least privilege. Turning automatic backups off removes the task and keeps the preferences. XIV Vault computes the next run itself and the scheduled run records its own result, so nothing depends on the locale of `schtasks` output.
- **The task runs the app that created it.** The desktop app registers itself with `--scheduled-backup` and runs without a window. The CLI registers `xiv-vault backup --scheduled`.
- **Scheduled backups wait for FFXIV to close** (checking every minute, for up to 6 hours) instead of copying a database the game has open. Manual backups run at once.
- **Integrity state** is cached per archive (path, size, modified time) in `%LOCALAPPDATA%\XIV Vault\state.json`. Archives that were never verified on this PC are verified in the background when listed.
- **Default destination:** `%OneDrive%\XIV Vault` when OneDrive is set up, otherwise `Documents\XIV Vault`.
- **UI** implements the Claude Design "XIV Vault" mockup, built on the Quorum design system with XIV Vault's overrides: IBM Plex Sans and Mono, accent `#6C9EFF`, healthy `#3CD5DE`. Dark only, like the mockup. Fonts are bundled TTFs (OFL) and icons are Lucide paths (ISC) compiled into the app, so nothing loads from a CDN.
- **The mockup's sample values are not used.** Version is 0.1.0, not the mockup's 1.4.0, and every number on screen comes from `XivVault.Core`.
- **Compression** follows the mockup: Fast, Balanced (default) and Maximum map to the .NET `CompressionLevel` values.
- **Name:** the product is **XIV Vault**, and every name follows it, since nothing had been released yet. `xiv-vault` for the repository, the command, archives, release zips and logs; `XivVault` for projects, namespaces and assemblies, which keeps resource URIs free of spaces; `XIV Vault` for folders: `%LOCALAPPDATA%\XIV Vault` and the default `OneDrive\XIV Vault` backup folder. The manifest field is `xivVaultVersion` and the override variable is `XIV_VAULT_DATA_DIR`.
- **Brand:** the diamond from the mockup's title bar. The wordmark, "XIV Vault", is IBM Plex Sans SemiBold converted to paths by `scripts/make-lockup.mjs`. Assets are in `docs/brand/`, and `scripts/make-icons.cs` builds `xiv-vault.ico` from `app-icon.svg`.

- **Screenshots** come from `tools/XivVault.Screenshots`, which renders the real views with Avalonia's headless Skia renderer against a fake XIVLauncher folder and a fake Task Scheduler. It is also how the UI was checked against the mockup.
- **Fresh-PC states** that the mockup doesn't cover follow its patterns: "Setting up a new PC?" (XIVLauncher present, no Dalamud settings) leads to Restore, and "XIVLauncher not found" leads to Settings. The restore wizard also accepts a backup file picked from anywhere.
- **Restore steps:** the review screen compares the backup with this PC and warns when plugins changed after the backup. The live restore runs inside step 3, as in the mockup, with stages in the order the engine runs them: verify, snapshot, plugin configs, Dalamud settings.

## Decisions (2026-10-04, after review)

An independent review of the restore and validation code found failure modes; these rules close them.

- **Links are refused.** Backups don't follow junctions or symlinks inside the XIVLauncher folder, so a restore refuses to write through one, and Diagnostics warns when `pluginConfigs` is a link.
- **The archive being restored is untouchable.** It stays open, read-shared only, for the whole restore. Snapshot retention runs after the restore and never removes the new snapshot or that archive.
- **Retention counts only verified backups.** Before deleting anything it verifies archives this PC hasn't checked, so a backup damaged after it was written never takes the place of a good one.
- **One operation at a time.** A named mutex per data folder serializes backups, restores and retention between the app, the CLI and the scheduled task.
- **Manifests must describe their files.** `contents` and `statistics` have to match the file list; paths that backups skip (`logs`, `cache`, temp files) are refused; archives are capped at 2 GB and 100,000 files, with sizes bounded before they are summed.

## Decisions (2026-10-05, installer and updates)

- **Installer:** the desktop app also ships as `xiv-vault-setup-win-x64.exe`, built with Velopack (MIT) by `release.yml`. It installs per user into `%LOCALAPPDATA%\XivVault` with a Start menu shortcut and no administrator prompt. Settings, state and logs stay in `%LOCALAPPDATA%\XIV Vault`, so uninstalling keeps them. The desktop zip stays as a portable copy that doesn't update. The CLI stays a zip; winget remains the way to update it.
- **The update check is on by default**, with a switch in Settings. It reads the public release list from GitHub and sends nothing about the user or their backups. It is the only network call XIV Vault makes, and the README's safety model says so. Portable copies never check.
- **Updates install only when the user chooses**, with Update and Restart in Settings; the sidebar shows when one is available. Installing ends every copy running from the install folder, so an update waits while a backup or restore runs anywhere, or while another copy (such as a scheduled backup) is open. Downloaded updates are never applied at startup, so a scheduled run never replaces its own program.
- **Uninstalling removes the scheduled task** when it starts the installed copy, and keeps the schedule preferences.
- **Desktop executable:** `XIV-Vault.exe`. The assembly stays `XivVault`, because .NET compares assembly names without case and the tests load the app and the CLI (`xiv-vault`) together; publishing renames the single-file exe. The CLI stays `xiv-vault.exe`. Windows treats the two as the same file name, so they ship in separate zips and the README says to keep them in separate folders. "Another copy is running" matches processes by path, so a CLI command never holds up an update.
- **Release assets:** Setup, this version's full (and, from the second release on, delta) update package and `releases.win.json` join the two zips. `SHA256SUMS` covers Setup and the zips.

## Decisions (2026-10-05, road to 1.0)

- **v0.1.0 is published.** From here on, every release must update cleanly from the one before it.
- **1.0 means safe to trust and stable to build on:** proven on real setups other than the author's, compatibility promises written down (0.1.0 backups always restore; the CLI, its JSON output and exit codes stay stable), and updates proven across releases.
- **Restoring only some plugins is part of 1.0.** It removes the biggest limitation of a restore, which today returns every plugin in the backup to its old settings.
- **Synced folders are the main case to harden.** Listing backups opens every archive, and verification reads each one in full, so on a new PC with OneDrive Files On-Demand, opening XIV Vault downloads every backup.
- **Code signing waits until after 1.0**, once there is an established userbase. Until then SmartScreen asks users to confirm Setup and the zips, and the README says so.

## M0: Bootstrap

- [x] Solution with `XivVault.Core`, `XivVault.Cli`, `XivVault.Desktop` and `XivVault.Tests`.
- [x] Brand assets and app icon.
- [x] CI on `windows-latest`: restore, build, test, `dotnet format --verify-no-changes`.

**Done when:** CI is green on a pull request.

## M1: Core engine

- [x] Configuration (`%LOCALAPPDATA%\XIV Vault\config.json`) and app state.
- [x] `IXivLauncherLocator` / `WindowsXivLauncherLocator`: override, known paths, layout detection (`dalamudUserData`), validation.
- [x] Backup: allowlist, ZIP + manifest, SHA-256, atomic finalize, retention.
- [x] Archive validation: schema, hashes, traversal, absolute paths, unknown files.
- [x] Restore: process check, safety snapshot, temp extraction, allowlisted apply, rollback on failure.

**Done when:** tests back up a fake XIVLauncher folder, restore it, and the traversal, checksum, running-process and partial-failure cases fail safely.

## M2: CLI

- [x] `backup`, `restore`, `list`, `status`, `doctor`, `schedule`, `config`, `version`, with `--json`, `--quiet` and `--verbose` where they apply.
- [x] Stable exit codes, documented in the README.

**Done when:** `xiv-vault backup`, `list`, `status`, `doctor` and `restore latest` work against a fake XIVLauncher folder.

## M3: Scheduling and diagnostics

- [x] Task Scheduler integration: daily, weekly, at Windows login.
- [x] Scheduled run: waits for FFXIV, records its result.
- [x] Diagnostics service and a report that holds paths, versions and results only.

**Done when:** `xiv-vault schedule weekly --day Sunday` creates the task, `schedule status` reads it back, and `doctor` reports all four groups.

## M4: Desktop

- [x] Shell: window chrome, navigation, theme tokens, fonts, icons, shared components.
- [x] Overview, Backups, Restore wizard, Schedule, Diagnostics, Settings on real Core data.
- [x] Polish: keyboard and focus, screen-reader names, reduced motion, empty and error states, confirmations.

**Done when:** from a clean state the app detects XIVLauncher, backs up, shows the backup in history, schedules weekly backups and restores a previous backup through the wizard.

## M5: Release v0.1.0

- [x] Self-contained win-x64 publish for both apps: `xiv-vault-cli-win-x64.zip`, `xiv-vault-desktop-win-x64.zip`.
- [x] Release workflow on `v*` tags.
- [x] README with screenshots, `docs/architecture.md`, `docs/backup-format.md`, `docs/troubleshooting.md`.
- [x] End-to-end smoke test against a fake XIVLauncher folder.

**Done when:** pushing `v0.1.0` publishes both zips to a draft GitHub release.

## M6: Installer and updates

- [x] Setup and update packages built with `vpk` in the release workflow, with a delta from the previous release.
- [x] Update check when the app opens, Update and Restart, and the switch in Settings; a notice in the sidebar.
- [x] Updates wait for backups, restores and other running copies; uninstalling removes the scheduled task.

**Done when:** Setup installs XIV Vault, an installed copy updates itself to a newer version, and uninstalling removes the scheduled task.

## M7: First users (0.1.x)

- [ ] Back up and restore on the author's own setup with the published release; fix what turns up.
- [ ] Release 0.1.1: the first update installed copies take from GitHub, and the first delta package.
- [ ] Issue templates: a bug report that asks for the Diagnostics report, and a feature request.
- [ ] Winget manifests for Setup and the CLI.

**Done when:** a 0.1.0 install updates itself to 0.1.1 from GitHub, and `winget install` works for both.

## M8: Fresh PC and synced folders (0.2)

- [ ] List backups without downloading them: names and sizes from the folder, manifests only for files already on this PC.
- [ ] Verify only the latest backup and the one being restored, not every archive in the folder.
- [ ] A restore whose backup is still in the cloud shows the download and its size before anything changes.
- [ ] XIVLauncher layouts and folders that users report.

**Done when:** on a new PC with OneDrive Files On-Demand, opening XIV Vault downloads nothing until a restore is chosen.

## M9: Restore only some plugins (0.3)

- [ ] Choose plugins in the review step of the restore wizard; Dalamud settings are their own choice.
- [ ] The same choice on the command line: `xiv-vault restore <backup> --plugin <name>`.
- [ ] The safety backup still holds everything a full restore would replace.

**Done when:** restoring one plugin's settings leaves every other plugin and Dalamud's settings as they were.

## M10: 1.0

- [ ] Real 0.1.0 archives kept as test files, which every later version must validate and restore.
- [ ] The stable parts written down: backup format, CLI commands and options, JSON output, exit codes, and what a major version may change.
- [ ] A release checklist that installs the previous release and updates it before a draft is published.
- [ ] Every screen checked with Narrator.

**Done when:** every item above is checked, the last two releases updated cleanly from the one before, and no data-loss issue is open.

## Later

- Code signing, after 1.0 once there is an established userbase.
- Light theme.

## Not planned

- Copying plugin binaries, or installing plugins.
- Backing up the game itself.
- Cloud APIs (Google Drive, Dropbox, OneDrive, S3). A folder synced by their own clients is enough.
- Accounts, telemetry, a web dashboard.
- A Windows service or tray daemon.
- Plugin-specific backup adapters, or a Dalamud plugin version of XIV Vault.
