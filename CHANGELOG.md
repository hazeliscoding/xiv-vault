# Changelog

All notable changes to XIV Vault are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- The sidebar said Dalamud reinstalls plugins on a new PC, which it doesn't. Install them again from Dalamud's plugin installer (`/xlplugins`), and each one picks up its restored settings.

## [1.0.0] - 2026-10-11

XIV Vault 1.0 is safe to trust and stable to build on: every backup any version made restores in every later version, and the command line keeps its commands, options, JSON fields and exit codes through 1.x ([docs/cli.md](docs/cli.md)).

### Added

- A website, [hazeliscoding.github.io/xiv-vault](https://hazeliscoding.github.io/xiv-vault/), with a user guide for every task. It loads nothing from other sites and has no analytics.
- A Windows notification when a scheduled backup fails, from the app's task and the CLI's alike. It says only that the backup failed, never a path or the error, since it can appear on a stream. Turn it off under XIV Vault in Windows' notification settings.
- `docs/cli.md`, a reference for every command, option, exit code and JSON field, with what stays the same from 1.0 on.

### Changed

- Small text, field borders and the dash beside something not chosen are easier to read: they now meet WCAG AA contrast.
- Tab reaches the pages in the sidebar before the links at its bottom.
- `schedule status --json` writes days in lowercase (`"sunday"`), as `config show --json` and every other JSON value do.

### Fixed

- Secondary buttons, such as Restore Backup and Choose Folder, show the focus ring.
- A confirmation keeps the keyboard and screen readers inside it, starts on the safe choice, and gives focus back when it closes.
- Focus no longer drops out of the window when Back Up Now runs or the restore wizard moves on a step, so the next Tab doesn't start again at the top.
- Screen readers hear which page is open, the name of every list, and a backup starting and finishing.
- In a narrow window, Schedule no longer cuts off Saturday, Sunday and its hints: the next backup moves under the settings. Backups keeps Reveal and Delete in view by leaving out the plugin count, then the size, which Inspect still shows.

## [0.4.0] - 2026-10-10

### Added

- Game settings. Backups also hold the game's own settings from `Documents\My Games\FINAL FANTASY XIV - A Realm Reborn`: each character's HUD layout, hotbars, keybinds, macros and gear sets, the shared macros, appearance saves, and system settings (`FFXIV.cfg`). Chat logs, screenshots and old copies stay out. A restore puts each character's settings back in that character's own folder, works on a PC where the game has never started, and refuses while the game runs.
- Character settings and system settings are choices of their own in the restore review, both chosen to start with. Leave out system settings when the new PC has a different monitor. `xiv-vault restore` takes `--character-settings` and `--system-settings`.
- A switch in Settings, and `xiv-vault config set include-game`, to leave game settings out of backups. Safety backups hold them either way.
- Diagnostics and `xiv-vault doctor` have a Game settings group. `status`, `list` and Backups count characters.

### Changed

- Setup, the update packages and both executables are code-signed, with Hazel Granados as the publisher. Windows SmartScreen may still warn about Setup until the download builds a reputation.
- Backups use format version 2. Version 1 backups still restore; XIV Vault 0.3 and earlier can't restore version 2 backups.
- `xiv-vault doctor --json` has a `gameSettings` group, `status --json` a `gameSettings` object, and `list --json` has `characterCount` and `systemSettings` fields.
- A character's folder is named by a content ID that identifies the character. Logs, error messages and the diagnostic report never show it.

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

[Unreleased]: https://github.com/hazeliscoding/xiv-vault/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/hazeliscoding/xiv-vault/compare/v0.4.0...v1.0.0
[0.4.0]: https://github.com/hazeliscoding/xiv-vault/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/hazeliscoding/xiv-vault/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/hazeliscoding/xiv-vault/compare/v0.1.2...v0.2.0
[0.1.2]: https://github.com/hazeliscoding/xiv-vault/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/hazeliscoding/xiv-vault/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/hazeliscoding/xiv-vault/releases/tag/v0.1.0
