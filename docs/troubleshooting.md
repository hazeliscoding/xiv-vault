# Troubleshooting

Start with **Diagnostics** in the app, or `xivault doctor`. It checks XIVLauncher, Dalamud, the backup folder and scheduling, and says what to do next. **Copy Diagnostic Report** (or `xivault doctor --report`) gives you text to attach to an issue. It holds paths, versions and results, never configuration contents, and your Windows user folder is shown as `%USERPROFILE%`.

## "XIVLauncher not found"

XIVault looks in `%AppData%\XIVLauncher` (and `XIVLauncherCN`). A folder only counts if XIVLauncher or Dalamud files are in it.

- Start XIVLauncher once, so it creates its folder.
- If you use a custom location, set it in **Settings → XIVLauncher → Override path**, or `xivault config set source D:\Games\XIVLauncher`. Point it at the folder that holds `launcherConfigV3.json` or `pluginConfigs`.
- `xivault config set source auto` goes back to detection.

## "There is no Dalamud configuration to back up yet"

XIVLauncher is installed but Dalamud has never saved settings. On a new PC, that's expected: restore a backup instead. Otherwise start the game once with Dalamud enabled.

## "The backup folder is not available" (exit code 7)

The drive is disconnected, the network share is offline, or the folder can't be created. Reconnect it, or choose another folder in **Settings**. A restore needs the backup folder too, because the safety snapshot is written there first.

## "XIVLauncher is running" / "FFXIV is running" (exit code 6)

Restores need both closed, because Dalamud rewrites its files while the game runs. Close the game and XIVLauncher, then run the checks again. Scheduled backups wait for the game to close by themselves.

## "The latest backup did not pass verification"

A file in the archive doesn't match its recorded hash: the file was damaged after it was written, often by an incomplete sync. XIVault keeps damaged archives for you to inspect and never counts them toward the number of backups it keeps. Back up again, and if it keeps happening, check the drive or the sync client. `xivault list --verify` re-checks every archive.

## A restore is blocked by the integrity check

The archive is damaged or isn't an XIVault backup, so XIVault won't restore it. Choose an older backup. If the backup came from a newer XIVault, update XIVault first.

## "Another XIVault backup or restore is still running"

Only one backup or restore runs at a time, including scheduled ones. Wait for the other to finish (a scheduled backup may be waiting for FFXIV to close) and try again.

## "… is a link to another location"

Something inside the XIVLauncher folder, often `pluginConfigs`, is a junction or symbolic link. XIVault doesn't follow links, so those settings aren't in your backups and a restore won't write through them. **Diagnostics** shows this as a suggestion. Replace the link with a normal folder, then back up again.

## Automatic backups don't run

- **Schedule** shows the next run and the last result. **Diagnostics → Scheduling** flags a missing task or a task that points to a program that no longer exists (for example after moving XIVault). Turning automatic backups off and on again recreates the task.
- `schtasks /Query /TN "XIVault Scheduled Backup" /V /FO LIST` shows the task as Windows sees it.
- The task runs only while you are signed in. A run that was missed because the PC was off happens at the next start.

## A restore stopped part-way

Every file the restore had changed was put back, so your configuration is as it was. The message says so. If it reports that some files could not be put back, restore the newest **Pre-Restore** backup from **Backups**: it holds your configuration from just before the restore.

## Plugins are missing after a restore

XIVault restores plugin settings, not plugins. Open XIVLauncher and start the game: Dalamud downloads your installed plugins again, and they pick up their restored settings. Plugins from custom repositories need those repositories, which are part of the restored Dalamud settings.

## Logs

`%LOCALAPPDATA%\XIVault\logs\xivault-YYYYMMDD.log`, kept for 14 days. Add `--verbose` to a CLI command to see the same lines in the terminal.

## Files XIVault keeps

| File | Purpose |
|---|---|
| `%LOCALAPPDATA%\XIVault\config.json` | Settings |
| `%LOCALAPPDATA%\XIVault\state.json` | Verification cache and the last scheduled result; safe to delete |
| `%LOCALAPPDATA%\XIVault\logs\` | Logs |

Set `XIVAULT_DATA_DIR` to use a different folder for all three, for example to test without touching your real settings.
