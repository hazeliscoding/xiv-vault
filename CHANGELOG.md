# Changelog

All notable changes to XIV Vault are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.0] - 2026-10-04

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
- Self-contained Windows x64 builds: `xiv-vault-desktop-win-x64.zip` and `xiv-vault-cli-win-x64.zip`.

[Unreleased]: https://github.com/hazeliscoding/xiv-vault/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/hazeliscoding/xiv-vault/releases/tag/v0.1.0
