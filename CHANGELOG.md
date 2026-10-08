# Changelog

All notable changes to XIV Vault are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.3.0] - 2026-10-08

### Added

- Restore only some plugins. The restore review lists the backup's plugins with a filter and Select all / Select none, and Dalamud settings are their own choice. Everything is chosen to start with, so a full restore is still one click. Plugins you leave out, and Dalamud settings if you leave them out, stay exactly as they are. However little is chosen, the whole backup is still verified and the safety backup still holds everything a full restore would replace.
- `xiv-vault restore --plugin <name>` (repeatable) and `--dalamud-settings` choose what to restore from the command line. A plugin the backup doesn't hold stops the restore with exit code 2.

## [0.2.0] - 2026-10-07

### Changed

- Backups kept online only, as OneDrive Files On-Demand does, are listed by name, date and size without being downloaded, so opening XIV Vault on a new PC no longer fetches every backup. Google Drive for desktop doesn't mark such files, so there XIV Vault still reads each backup's manifest.
- XIV Vault verifies the latest backup and the one being restored, not every backup this PC hasn't checked, which read each one in full. `xiv-vault list --verify` still checks them all.
- Restoring a backup that is still in the cloud shows its size and downloads it first, with progress and Cancel in the app. `xiv-vault restore` says the same before it downloads.
- Retention never downloads a backup to decide whether to keep it. An online-only backup this PC hasn't verified doesn't count toward the number kept, and is deleted unopened once enough newer verified backups exist.
- `xiv-vault list --json` has a new `onlineOnly` field, and `pluginConfigCount` is `null` when the backup's manifest hasn't been read.

## [0.1.2] - 2026-10-06

### Fixed

- After a backup, the restore wizard still preselected the backup that was newest when the app opened. It now follows the newest backup until you choose one.

## [0.1.1] - 2026-10-05

### Fixed

- Diagnostics showed the XIVLauncher folder joined to the folder XIV Vault runs from, such as `%USERPROFILE%\AppData\Local\XivVault\current\%AppData%\XIVLauncher`, when that folder was inside the user profile, as it is for the installed app.
- XIVLauncher 7, which installs with Velopack, was reported as "XIVLauncher app not found", so Open XIVLauncher was unavailable.
- The restore review warned that plugins would "return to their earlier settings" when their files had only been saved again without changes, which plugins often do when the game closes. It now compares contents with the backup.

## [0.1.0] - 2026-10-05

First release.

### Added

- Backups of the portable XIVLauncher / Dalamud configuration: `pluginConfigs`, `dalamudConfig.json`, `dalamudVfs.db` and, optionally, `dalamudUI.ini`. Plain ZIP archives with a versioned manifest and a SHA-256 for every file, written atomically and verified before they count.
- Restores with safety checks (integrity, XIVLauncher and FFXIV closed, folders available), an automatic pre-restore safety backup, extraction to a temp folder, and rollback if anything fails part-way.
- Retention: the newest 10 backups by default, and the newest 3 safety backups kept separately.
- XIVLauncher detection, including `dalamudUserData` layouts and a manual folder override.
- Automatic backups through Windows Task Scheduler: daily, weekly or at Windows login. Scheduled runs wait for FFXIV to close.
- Diagnostics for XIVLauncher, Dalamud, the backup folder and scheduling, with a shareable report that holds no configuration contents.
- Desktop app (Avalonia): Overview, Backups, Restore wizard, Schedule, Diagnostics and Settings.
- Command line: `backup`, `restore`, `list`, `status`, `doctor`, `schedule`, `config` and `version`, with JSON output and stable exit codes.
- Installer: `xiv-vault-setup-win-x64.exe` installs the desktop app for the current user and keeps it up to date from GitHub releases. Updates install only when chosen in Settings, wait for running backups, restores and scheduled backups, and the check can be turned off. Uninstalling removes the scheduled backup task.
- Self-contained Windows x64 builds: `xiv-vault-setup-win-x64.exe`, plus portable `xiv-vault-desktop-win-x64.zip` and `xiv-vault-cli-win-x64.zip`.

[Unreleased]: https://github.com/hazeliscoding/xiv-vault/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/hazeliscoding/xiv-vault/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/hazeliscoding/xiv-vault/compare/v0.1.2...v0.2.0
[0.1.2]: https://github.com/hazeliscoding/xiv-vault/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/hazeliscoding/xiv-vault/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/hazeliscoding/xiv-vault/releases/tag/v0.1.0
