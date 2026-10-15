# Releasing

Every release must update cleanly from the one before it, so nothing is published until an installed copy of the previous release has updated itself to the draft.

## 1. Prepare

- [ ] `CHANGELOG.md`: move `[Unreleased]` under the new version and today's date. Anything that changes the command line is listed, and a 1.x release only adds to it (see [cli.md](cli.md#what-stays-the-same-in-1x)).
- [ ] `Directory.Build.props`: set `<Version>`.
- [ ] For a beta, use `x.y.z-beta.N` and write `docs/releases/x.y.z-beta.N.md`. The workflow uses it as the release body. Include experimental behavior, download hosts, privacy details and the tester's steps. Give goatcorp the agreed heads-up before publishing the first plugin-install beta.
- [ ] Commit `chore(release): x.y.z`, push `main`, and wait for CI to pass.

## 2. Build the draft

- [ ] `git tag vx.y.z` and `git push origin vx.y.z`. `release.yml` tests, signs and packs, then makes a draft release.
- [ ] The draft has 7 files: `xiv-vault-setup-win-x64.exe`, both zips, `XivVault-x.y.z-full.nupkg`, `XivVault-x.y.z-delta.nupkg`, `releases.win.json` and `SHA256SUMS`.
- [ ] A `vX.Y.Z-beta.N` tag produces a signed **Pre-release** draft with `latest=false`. It uses the same `win` feed; its delta comes from the latest eligible release, including earlier betas. Confirm the beta notes appear in the draft.

## 3. Update the previous release to the draft

On a PC with the previous release installed. If XIV Vault isn't installed there, install the previous release's Setup first. Close every XIV Vault window, then point the installed copy at the draft:

For the first beta, the previous release is stable 1.0.0. Its local-source override can offer the beta for this rehearsal, although its normal GitHub check never offers prereleases. Testers install the first beta's Setup by hand. For later betas, rehearse from the previous beta with **Get beta versions** on, and from the latest stable version when it has the switch.

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
- [ ] In a version with the beta switch, a newer beta in the draft feed is offered only with **Get beta versions** on. Turning it off clears an offered beta; **Check Now** does not offer it. A beta with the switch off never offers an older stable release. Settings and the sidebar identify the installed beta.
- [ ] Remove `XIV_VAULT_UPDATE_SOURCE` from the test process environment after the rehearsal. The normal source is GitHub.

## 4. Publish

- [ ] Stable: publish the draft as the latest release. Beta: publish it as a **Pre-release**, keeping **Set as the latest release** off. Stable copies and the website's main download must still see the previous stable release.
- [ ] Stable only: `./scripts/make-winget-manifests.ps1 -Version x.y.z`, and update the winget pull requests. Skip winget for every beta.
- [ ] Record the release in `ROADMAP.md`, including the update from the previous release.
