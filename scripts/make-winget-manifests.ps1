<#
  Writes the winget manifests for a published release into artifacts/winget, in the folder layout
  microsoft/winget-pkgs uses, and validates them:

    HazelGranados.XIVVault      the app, installed with Setup
    HazelGranados.XIVVault.CLI  the command line, from the portable zip

  Hashes come from the release's SHA256SUMS, so nothing is downloaded but that file.

    ./scripts/make-winget-manifests.ps1 -Version 0.1.0
#>
param(
    [Parameter(Mandatory)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repo = 'hazeliscoding/xiv-vault'
$tag = "v$Version"
$schema = '1.12.0'

$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/tags/$tag"
if ($release.draft) { throw "$tag is still a draft. Winget needs a published release." }
$date = ([datetime]$release.published_at).ToUniversalTime().ToString('yyyy-MM-dd')

$sumsFile = New-TemporaryFile
Invoke-WebRequest "https://github.com/$repo/releases/download/$tag/SHA256SUMS" -OutFile $sumsFile
$sums = @{}
foreach ($line in Get-Content $sumsFile) {
    $hash, $name = $line -split '\s+', 2
    $sums[$name] = $hash.ToUpperInvariant()
}
Remove-Item $sumsFile

$download = "https://github.com/$repo/releases/download/$tag"
$out = Join-Path $PSScriptRoot "../artifacts/winget/manifests/h/HazelGranados/XIVVault"

function Write-Manifest($folder, $file, $text) {
    New-Item -ItemType Directory -Force $folder | Out-Null
    # winget-pkgs expects UTF-8 without a BOM.
    [IO.File]::WriteAllText((Join-Path $folder $file), $text.Replace("`r`n", "`n") + "`n")
}

function Write-Package($id, $folder, $installer, $locale) {
    Write-Manifest $folder "$id.yaml" @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json

PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@
    Write-Manifest $folder "$id.installer.yaml" @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json

PackageIdentifier: $id
PackageVersion: $Version
InstallerLocale: en-US
ReleaseDate: $date
$installer
ManifestType: installer
ManifestVersion: $schema
"@
    Write-Manifest $folder "$id.locale.en-US.yaml" @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json

PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Hazel Granados
PublisherUrl: https://github.com/hazeliscoding
PublisherSupportUrl: https://github.com/$repo/issues
Author: Hazel Granados
PackageUrl: https://github.com/$repo
License: Apache-2.0
LicenseUrl: https://github.com/$repo/blob/main/LICENSE
Copyright: Copyright 2026 Hazel Granados
$locale
Tags:
- backup
- dalamud
- ff14
- ffxiv
- final-fantasy-xiv
- restore
- xivlauncher
ReleaseNotesUrl: https://github.com/$repo/releases/tag/$tag
ManifestType: defaultLocale
ManifestVersion: $schema
"@
}

$description = @"
Description: |-
  Move to a new PC without losing your Dalamud setup. XIV Vault backs up the portable part of your
  XIVLauncher / Dalamud configuration (plugin settings, Dalamud settings, the plugin database) into a
  plain ZIP in a folder you choose, such as OneDrive, Dropbox, a NAS or an external drive. Every backup
  is hash-verified, and every restore takes a safety backup first.
"@

# The app updates itself from GitHub releases. Running Setup over it closes every running copy,
# which could interrupt a backup or restore, so `winget upgrade --all` leaves it alone.
Write-Package 'HazelGranados.XIVVault' (Join-Path $out $Version) @"
InstallerType: exe
Scope: user
InstallModes:
- silent
InstallerSwitches:
  Silent: --silent
  SilentWithProgress: --silent
  InstallLocation: --installto "<INSTALLPATH>"
  Log: --log "<LOGPATH>"
UpgradeBehavior: install
RequireExplicitUpgrade: true
ProductCode: XivVault
AppsAndFeaturesEntries:
- DisplayName: XIV Vault
  Publisher: Hazel Granados
  ProductCode: XivVault
InstallationMetadata:
  DefaultInstallLocation: '%LocalAppData%\XivVault'
Installers:
- Architecture: x64
  InstallerUrl: $download/xiv-vault-setup-win-x64.exe
  InstallerSha256: $($sums['xiv-vault-setup-win-x64.exe'])
"@ @"
PackageName: XIV Vault
Moniker: xiv-vault
ShortDescription: Back up and restore your XIVLauncher and Dalamud plugin settings.
$description
"@

Write-Package 'HazelGranados.XIVVault.CLI' (Join-Path $out "CLI/$Version") @"
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: xiv-vault.exe
  PortableCommandAlias: xiv-vault
Commands:
- xiv-vault
Installers:
- Architecture: x64
  InstallerUrl: $download/xiv-vault-cli-win-x64.zip
  InstallerSha256: $($sums['xiv-vault-cli-win-x64.zip'])
"@ @"
PackageName: XIV Vault CLI
Moniker: xiv-vault-cli
ShortDescription: Command line for XIV Vault, which backs up and restores your XIVLauncher and Dalamud plugin settings.
$description
"@

foreach ($folder in (Join-Path $out $Version), (Join-Path $out "CLI/$Version")) {
    $resolved = (Resolve-Path $folder).Path
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        winget validate --manifest $resolved
        if ($LASTEXITCODE -ne 0) { throw "winget validate failed for $resolved" }
    }
    Write-Host "Wrote $resolved"
}
