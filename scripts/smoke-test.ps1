<#
.SYNOPSIS
  End-to-end check of a built CLI against a throwaway XIVLauncher folder.

.DESCRIPTION
  Creates a fake XIVLauncher folder (portable files plus things XIVault must never touch), then:
  backup -> inspect the ZIP -> change the source -> restore latest -> check what changed and what
  didn't -> check the safety snapshot. Uses XIVAULT_DATA_DIR so the real profile is never used.

.EXAMPLE
  ./scripts/smoke-test.ps1 -Cli artifacts/publish/XIVault.Cli/xivault.exe
  ./scripts/smoke-test.ps1 -Cli src/XIVault.Cli/bin/Release/net10.0/xivault.dll
#>
param(
    [Parameter(Mandatory = $true)][string]$Cli
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Cli = (Resolve-Path $Cli).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ("xivault-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$launcher = Join-Path $root 'XIVLauncher'
$backups = Join-Path $root 'Backups'
$env:XIVAULT_DATA_DIR = Join-Path $root 'data'
$failures = 0

# Runs the CLI, shows its output, and returns only its exit code.
function Invoke-Xivault {
    param([string[]]$Arguments)
    if ($Cli.EndsWith('.dll')) { & dotnet $Cli @Arguments | Out-Host } else { & $Cli @Arguments | Out-Host }
    return $LASTEXITCODE
}

# Runs the CLI and returns its standard output as one string.
function Get-XivaultOutput {
    param([string[]]$Arguments)
    if ($Cli.EndsWith('.dll')) { $text = & dotnet $Cli @Arguments } else { $text = & $Cli @Arguments }
    return ($text | Out-String)
}

function Check {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Host "  ok    $Message"
    } else {
        Write-Host "  FAIL  $Message" -ForegroundColor Red
        $script:failures++
    }
}

function Write-File {
    param([string]$Path, [string]$Text)
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    [IO.File]::WriteAllText($Path, $Text)
}

try {
    Write-Host "Fake XIVLauncher at $launcher"
    Write-File (Join-Path $launcher 'pluginConfigs/Artisan.json') '{ "original": true }'
    Write-File (Join-Path $launcher 'pluginConfigs/AutoRetainer/profiles/main.json') '{ "retainers": 10 }'
    Write-File (Join-Path $launcher 'dalamudConfig.json') '{ "ThirdRepoList": [] }'
    Write-File (Join-Path $launcher 'dalamudVfs.db') 'SQLite format 3 fake'
    Write-File (Join-Path $launcher 'dalamudui.ini') '[Window]'
    Write-File (Join-Path $launcher 'installedPlugins/Artisan/1.0.0/Artisan.dll') 'binary v1'
    Write-File (Join-Path $launcher 'unrelated-file.txt') 'leave me alone'

    Check ((Invoke-Xivault @('config', 'set', 'source', $launcher)) -eq 0) 'config set source'
    Check ((Invoke-Xivault @('config', 'set', 'destination', $backups)) -eq 0) 'config set destination'
    Check ((Invoke-Xivault @('config', 'set', 'include-ui', 'true')) -eq 0) 'config set include-ui'

    Write-Host 'Back up'
    Check ((Invoke-Xivault @('backup', '--quiet')) -eq 0) 'backup exits 0'
    $archive = Get-ChildItem $backups -Filter 'xivault-*.zip' | Select-Object -First 1
    Check ($null -ne $archive) 'archive created'
    Check ((Get-ChildItem $backups -Filter '*.tmp').Count -eq 0) 'no temp files left'

    Write-Host 'Inspect the ZIP'
    $zip = [IO.Compression.ZipFile]::OpenRead($archive.FullName)
    $names = $zip.Entries | ForEach-Object FullName
    $zip.Dispose()
    Check ($names -contains 'manifest.json') 'manifest.json present'
    Check ($names -contains 'payload/pluginConfigs/Artisan.json') 'plugin config present'
    Check ($names -contains 'payload/pluginConfigs/AutoRetainer/profiles/main.json') 'nested plugin config present'
    Check ($names -contains 'payload/dalamudConfig.json') 'dalamudConfig.json present'
    Check ($names -contains 'payload/dalamudVfs.db') 'dalamudVfs.db present'
    Check ($names -contains 'payload/dalamudUI.ini') 'dalamudUI.ini present when enabled'
    Check (-not ($names | Where-Object { $_ -like '*installedPlugins*' })) 'installedPlugins absent'
    Check (-not ($names | Where-Object { $_ -like '*unrelated*' })) 'unrelated file absent'

    Write-Host 'Change the source'
    Write-File (Join-Path $launcher 'pluginConfigs/Artisan.json') '{ "changed": true }'
    Write-File (Join-Path $launcher 'dalamudConfig.json') '{ "changed": true }'
    Write-File (Join-Path $launcher 'installedPlugins/Artisan/1.0.0/Artisan.dll') 'binary v2'

    Write-Host 'Restore latest'
    Check ((Invoke-Xivault @('restore', 'latest', '--yes')) -eq 0) 'restore exits 0'
    Check ((Get-Content -Raw (Join-Path $launcher 'pluginConfigs/Artisan.json')) -eq '{ "original": true }') 'plugin config restored'
    Check ((Get-Content -Raw (Join-Path $launcher 'dalamudConfig.json')) -eq '{ "ThirdRepoList": [] }') 'dalamudConfig.json restored'
    Check ((Get-Content -Raw (Join-Path $launcher 'installedPlugins/Artisan/1.0.0/Artisan.dll')) -eq 'binary v2') 'installedPlugins untouched'
    Check ((Get-Content -Raw (Join-Path $launcher 'unrelated-file.txt')) -eq 'leave me alone') 'unrelated file untouched'

    $snapshot = Get-ChildItem $backups -Filter 'pre-restore-*.zip' | Select-Object -First 1
    Check ($null -ne $snapshot) 'safety snapshot exists'
    if ($snapshot) {
        $zip = [IO.Compression.ZipFile]::OpenRead($snapshot.FullName)
        $entry = $zip.GetEntry('payload/pluginConfigs/Artisan.json')
        $reader = [IO.StreamReader]::new($entry.Open())
        $before = $reader.ReadToEnd()
        $reader.Dispose()
        $zip.Dispose()
        Check ($before -eq '{ "changed": true }') 'snapshot holds the pre-restore state'
    }

    Write-Host 'Status and diagnostics'
    Check (@((Get-XivaultOutput @('list', '--json')) | ConvertFrom-Json).Count -eq 2) 'list shows backup and snapshot'
    Check (((Get-XivaultOutput @('status', '--json')) | ConvertFrom-Json).state -eq 'protected') 'status is protected'
    Check ((Invoke-Xivault @('doctor')) -eq 0) 'doctor finds no problems'
}
finally {
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
    Remove-Item Env:XIVAULT_DATA_DIR -ErrorAction SilentlyContinue
}

if ($failures -gt 0) {
    Write-Host "$failures check(s) failed" -ForegroundColor Red
    exit 1
}

Write-Host 'Smoke test passed' -ForegroundColor Green
