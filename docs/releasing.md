# Releasing

Every release must update cleanly from the one before it, so nothing is published until an installed copy of the previous release has updated itself to the draft.

## 1. Prepare

- [ ] `CHANGELOG.md`: move `[Unreleased]` under the new version and today's date. Anything that changes the command line is listed, and a 1.x release only adds to it (see [cli.md](cli.md#what-stays-the-same-in-1x)).
- [ ] `Directory.Build.props`: set `<Version>`.
- [ ] Commit `chore(release): x.y.z`, push `main`, and wait for CI to pass.

## 2. Build the draft

- [ ] `git tag vx.y.z` and `git push origin vx.y.z`. `release.yml` tests, signs and packs, then makes a draft release.
- [ ] The draft has 7 files: `xiv-vault-setup-win-x64.exe`, both zips, `XivVault-x.y.z-full.nupkg`, `XivVault-x.y.z-delta.nupkg`, `releases.win.json` and `SHA256SUMS`.

## 3. Update the previous release to the draft

On a PC with the previous release installed. If XIV Vault isn't installed there, install the previous release's Setup first. Close every XIV Vault window, then point the installed copy at the draft:

```powershell
gh release download vx.y.z -p "*.nupkg" -p releases.win.json -D "$env:TEMP\xiv-vault-draft"
$env:XIV_VAULT_UPDATE_SOURCE = "$env:TEMP\xiv-vault-draft"
& "$env:LOCALAPPDATA\XivVault\current\XIV-Vault.exe"
```

- [ ] **Settings** offers the new version. **Update and Restart** installs it, and XIV Vault opens again showing the new version.
- [ ] Settings, the backup list and the schedule are as they were.
- [ ] **Diagnostics** has no new warnings, and Scheduling is healthy.
- [ ] **Back Up Now** makes a verified backup.
- [ ] `schtasks /run /tn "XIV Vault Scheduled Backup"` runs without a window, and **Schedule** then shows the last run as successful.

## 4. Publish

- [ ] Publish the draft as the latest release. Installed copies see it from now on.
- [ ] `./scripts/make-winget-manifests.ps1 -Version x.y.z`, and update the winget pull requests.
- [ ] Record the release in `ROADMAP.md`, including the update from the previous release.
