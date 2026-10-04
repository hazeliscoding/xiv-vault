# Troubleshooting

Start with **Diagnostics** in the app, or `xiv-vault doctor`. It checks XIVLauncher, Dalamud, the backup folder and scheduling, and says what to do next. **Copy Diagnostic Report** (or `xiv-vault doctor --report`) gives you text to attach to an issue. It holds paths, versions and results, never configuration contents, and your Windows user folder is shown as `%USERPROFILE%`.

## "XIVLauncher not found"

XIV Vault looks in `%AppData%\XIVLauncher` (and `XIVLauncherCN`). A folder only counts if XIVLauncher or Dalamud files are in it.

- Start XIVLauncher once, so it creates its folder.
- If you use a custom location, set it in **Settings → XIVLauncher → Override path**, or `xiv-vault config set source D:\Games\XIVLauncher`. Point it at the folder that holds `launcherConfigV3.json` or `pluginConfigs`.
- `xiv-vault config set source auto` goes back to detection.

## "There is no Dalamud configuration to back up yet"

XIVLauncher is installed but Dalamud has never saved settings. On a new PC, that's expected: restore a backup instead. Otherwise start the game once with Dalamud enabled.

## "The backup folder is not available" (exit code 7)

The drive is disconnected, the network share is offline, or the folder can't be created. Reconnect it, or choose another folder in **Settings**. A restore needs the backup folder too, because the safety snapshot is written there first.

## "XIVLauncher is running" / "FFXIV is running" (exit code 6)

Restores need both closed, because Dalamud rewrites its files while the game runs. Close the game and XIVLauncher, then run the checks again. Scheduled backups wait for the game to close by themselves.

## "The latest backup did not pass verification"

A file in the archive doesn't match its recorded hash: the file was damaged after it was written, often by an incomplete sync. XIV Vault keeps damaged archives for you to inspect and never counts them toward the number of backups it keeps. Back up again, and if it keeps happening, check the drive or the sync client. `xiv-vault list --verify` re-checks every archive.

## A restore is blocked by the integrity check

The archive is damaged or isn't a XIV Vault backup, so XIV Vault won't restore it. Choose an older backup. If the backup came from a newer XIV Vault, update XIV Vault first.

## "Another XIV Vault backup or restore is still running"

Only one backup or restore runs at a time, including scheduled ones. XIV Vault waits up to five minutes for the other one to finish before showing this. Try again in a moment.

## "… is a link to another location"

Something inside the XIVLauncher folder, often `pluginConfigs`, is a junction or symbolic link. XIV Vault doesn't follow links, so those settings aren't in your backups and a restore won't write through them. **Diagnostics** shows this as a suggestion. Replace the link with a normal folder, then back up again.

## Automatic backups don't run

- **Schedule** shows the next run and the last result. **Diagnostics → Scheduling** flags a missing task or a task that points to a program that no longer exists (for example after moving XIV Vault). Turning automatic backups off and on again recreates the task.
- `schtasks /Query /TN "XIV Vault Scheduled Backup" /V /FO LIST` shows the task as Windows sees it.
- The task runs only while you are signed in. A run that was missed because the PC was off happens at the next start.

## A restore stopped part-way

Every file the restore had changed was put back, so your configuration is as it was. The message says so. If it reports that some files could not be put back, restore the newest **Pre-Restore** backup from **Backups**: it holds your configuration from just before the restore.

## Plugins are missing after a restore

XIV Vault restores plugin settings, not plugins. Open XIVLauncher and start the game: Dalamud downloads your installed plugins again, and they pick up their restored settings. Plugins from custom repositories need those repositories, which are part of the restored Dalamud settings.

## Logs

`%LOCALAPPDATA%\XIV Vault\logs\xiv-vault-YYYYMMDD.log`, kept for 14 days. Add `--verbose` to a CLI command to see the same lines in the terminal.

## Files XIV Vault keeps

| File | Purpose |
|---|---|
| `%LOCALAPPDATA%\XIV Vault\config.json` | Settings |
| `%LOCALAPPDATA%\XIV Vault\state.json` | Verification cache and the last scheduled result; safe to delete |
| `%LOCALAPPDATA%\XIV Vault\logs\` | Logs |

Set `XIV_VAULT_DATA_DIR` to use a different folder for all three, for example to test without touching your real settings.
