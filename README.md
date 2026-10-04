<p align="center"><img src="docs/brand/lockup-dark.svg" alt="XIVault" height="48"></p>

**Move to a new PC without losing your Dalamud setup.** XIVault backs up the portable part of your XIVLauncher / Dalamud configuration (plugin settings, Dalamud settings, the plugin database) into a plain ZIP in a folder you choose, such as OneDrive, Dropbox, a NAS or an external drive. On a fresh PC you install XIVLauncher, point XIVault at that folder and restore.

XIVault is a Windows desktop app and a command-line tool built on the same engine. It is in early development; see [ROADMAP.md](ROADMAP.md).

## Command line

```powershell
xivault backup                      # back up now
xivault backup --quiet              # for scripts: print nothing unless it fails
xivault list                        # backups in the backup folder
xivault status --json               # is the setup protected?
xivault doctor                      # check XIVLauncher, Dalamud, the folder and scheduling
xivault schedule weekly --day Sunday
xivault restore latest              # always takes a safety backup first
```

Exit codes are stable, so scripts can rely on them:

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

## License

[Apache-2.0](LICENSE). XIVault is not affiliated with Square Enix, XIVLauncher or Dalamud.
