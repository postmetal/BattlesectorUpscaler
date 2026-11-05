<#
.SYNOPSIS
    Copies the built mod into the game's BepInEx plugins folder and zips dist/.

.NOTES
    nvngx_dlss.dll is NOT part of this repository. Put a copy into dist\BattlesectorUpscaler\
    before packaging (it ships with NVIDIA's DLSS SDK or with any DLSS-enabled game).
#>
[CmdletBinding()]
param(
    [string]$GameDir = $env:BATTLESECTOR_DIR,
    [switch]$NoInstall
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist\BattlesectorUpscaler'

if (-not (Test-Path (Join-Path $dist 'BattlesectorUpscaler.dll'))) {
    throw "Build output not found in '$dist'. Run scripts\build.ps1 first."
}

$dlss = Join-Path $dist 'nvngx_dlss.dll'
if (-not (Test-Path $dlss)) {
    Write-Warning "nvngx_dlss.dll is missing from dist. DLSS will fail to start; the mod will still blit."
}

if (-not $NoInstall) {
    if (-not $GameDir) {
        $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Warhammer 40,000 Battlesector'
    }

    $plugins = Join-Path $GameDir 'BepInEx\plugins\BattlesectorUpscaler'
    New-Item -ItemType Directory -Force -Path $plugins | Out-Null
    Copy-Item (Join-Path $dist '*') $plugins -Recurse -Force
    Write-Host "Installed to $plugins" -ForegroundColor Green
}

$version = '0.1.0'
$zip = Join-Path $root "dist\BattlesectorUpscaler-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip
Write-Host "Package -> $zip" -ForegroundColor Green
