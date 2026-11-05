<#
.SYNOPSIS
    Builds the managed IL2CPP plugin and (optionally) the native NGX bridge.

.DESCRIPTION
    Requires BepInEx 6 (Unity IL2CPP build) installed in the game AND launched once, so that
    BepInEx has generated the interop assemblies in BepInEx\interop.

    If NuGet is unreachable, the script automatically falls back to compiling against the local
    .NET 6 runtime assemblies, which is enough for this plugin.

.EXAMPLE
    .\scripts\build.ps1 -GameDir "D:\Steam\steamapps\common\Warhammer 40000 Battlesector" -SkipNative
#>
[CmdletBinding()]
param(
    [string]$GameDir = $env:BATTLESECTOR_DIR,
    [string]$BepInExCore,
    [string]$InteropDir,
    [string]$DlssSdk = $env:DLSS_SDK,
    [switch]$SkipNative,
    [switch]$ForceOffline
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $GameDir) {
    $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Warhammer 40,000 Battlesector'
}

# The data folder name differs between store builds (e.g. "Warhammer 40K Battlesector_Data"),
# so do not rely on it. GameAssembly.dll is the only thing that must be here.
if (-not (Test-Path (Join-Path $GameDir 'GameAssembly.dll'))) {
    $dataDirs = @(Get-ChildItem -LiteralPath $GameDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '*_Data' })
    $hint = if ($dataDirs.Count -gt 0) {
        "Found data folder(s): " + ($dataDirs.Name -join ', ')
    } else {
        "No *_Data folder was found either."
    }
    throw "GameAssembly.dll not found in '$GameDir'. Point -GameDir at the folder that contains the game executable. $hint"
}

if (-not $BepInExCore) { $BepInExCore = Join-Path $GameDir 'BepInEx\core' }
if (-not (Test-Path (Join-Path $BepInExCore 'BepInEx.Core.dll'))) {
    throw "BepInEx.Core.dll not found in '$BepInExCore'. Install BepInEx 6 (Unity IL2CPP, win-x64) into the game first."
}

if (-not $InteropDir) { $InteropDir = Join-Path $GameDir 'BepInEx\interop' }
if (-not (Test-Path (Join-Path $InteropDir 'UnityEngine.CoreModule.dll'))) {
    throw "Interop assemblies not found in '$InteropDir'. Start the game once with BepInEx installed, quit, and run this again."
}

function Find-Net6Runtime {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.NETCore.App'),
        (Join-Path $GameDir 'dotnet\shared\Microsoft.NETCore.App')
    )

    foreach ($root in $candidates) {
        if (-not (Test-Path $root)) { continue }
        $dir = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like '6.0.*' } |
            Sort-Object Name -Descending |
            Select-Object -First 1
        if ($dir) { return $dir.FullName }
    }

    return $null
}

function Write-OfflineProps([string]$runtimeDir) {
    $escaped = [System.Security.SecurityElement]::Escape($runtimeDir)
    $props = @"
<Project>
  <PropertyGroup>
    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>
    <!-- The runtime folder contains native DLLs too; skip the "bad image" warnings they cause. -->
    <MSBuildWarningsAsMessages>MSBuildWarningsAsMessages;MSB3246</MSBuildWarningsAsMessages>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="$escaped\*.dll" />
  </ItemGroup>
</Project>
"@
    Set-Content -LiteralPath (Join-Path $root 'managed\offline.props') -Value $props -Encoding UTF8
}

Write-Host "Building managed plugin..." -ForegroundColor Cyan
$project = Join-Path $root 'managed\BattlesectorUpscaler.csproj'
$offlineProps = Join-Path $root 'managed\offline.props'

# A leftover offline.props would disable the normal framework references, so always start clean.
if (Test-Path $offlineProps) { Remove-Item $offlineProps -Force }

# Pass paths through the environment instead of -p: arguments. MSBuild reads environment
# variables as properties, and this avoids Windows argument quoting breaking on paths that
# contain spaces (e.g. "Warhammer 40000 Battlesector").
$env:GameDir = $GameDir
$env:BepInExCore = $BepInExCore
$env:InteropDir = $InteropDir

$restoreOk = $false
if (-not $ForceOffline) {
    Write-Host "Restoring packages..." -ForegroundColor DarkGray
    dotnet restore $project -p:TargetFramework=net6.0 --ignore-failed-sources -v:q
    $restoreOk = ($LASTEXITCODE -eq 0)
}

if (-not $restoreOk) {
    $runtime = Find-Net6Runtime
    if (-not $runtime) {
        throw "NuGet restore failed and no .NET 6 runtime was found to build against. Install the .NET 6 runtime or fix NuGet access."
    }

    Write-Warning "NuGet is not reachable. Building against the local .NET 6 runtime: $runtime"
    Write-OfflineProps $runtime

    dotnet restore $project -p:TargetFramework=net6.0 --ignore-failed-sources -v:q
    if ($LASTEXITCODE -ne 0) { throw "Offline restore failed." }
}

dotnet build $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed. If this is a compile error, send the first 'error CS...' line."
}

$dist = Join-Path $root 'dist\BattlesectorUpscaler'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item (Join-Path $root 'managed\bin\Release\BattlesectorUpscaler.dll') $dist -Force
Write-Host "Plugin -> $dist" -ForegroundColor Green

if ($SkipNative) {
    Write-Host "Skipping native build (-SkipNative)." -ForegroundColor Yellow
    return
}

if (-not $DlssSdk) {
    $defaultSdk = 'C:\SDK\DLSS'
    if (Test-Path $defaultSdk) { $DlssSdk = $defaultSdk }
}

if ($DlssSdk) {
    # Accept either the SDK root or a parent that contains the extracted repo one level down.
    $resolved = $null
    if (Test-Path (Join-Path $DlssSdk 'include\nvsdk_ngx.h')) {
        $resolved = $DlssSdk
    } else {
        $child = Get-ChildItem -LiteralPath $DlssSdk -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'include\nvsdk_ngx.h') } |
            Select-Object -First 1
        if ($child) { $resolved = $child.FullName }
    }

    if (-not $resolved) {
        throw ("DLSS SDK at '$DlssSdk' has no include\nvsdk_ngx.h. " +
               "Download https://github.com/NVIDIA/DLSS/archive/refs/heads/main.zip, extract it, " +
               "and copy its 'include' folder into '$DlssSdk' (so '$DlssSdk\include\nvsdk_ngx.h' exists).")
    }

    $DlssSdk = $resolved
    $env:DLSS_SDK = $DlssSdk
}

if (-not $DlssSdk) {
    Write-Warning "DLSS_SDK is not set, so BattlesectorUpscalerNative.dll was not built. The plugin will fall back to plain blitting."
    return
}

Write-Host "Building native bridge..." -ForegroundColor Cyan
$native = Join-Path $root 'native'
$build = Join-Path $native 'build'

# CMake ships with the VS "C++ CMake tools" component but is often not on PATH.
$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) {
    $candidates = @(
        'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
    )
    $cmake = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $cmake) {
    throw "CMake not found. Install it from https://cmake.org/download/ (add to PATH) or install the 'C++ CMake tools for Windows' component in Visual Studio Build Tools."
}

& $cmake -S $native -B $build -G "Visual Studio 17 2022" -A x64 "-DDLSS_SDK=$DlssSdk"
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed." }

& $cmake --build $build --config Release
if ($LASTEXITCODE -ne 0) { throw "cmake build failed." }

Copy-Item (Join-Path $build 'Release\BattlesectorUpscalerNative.dll') $dist -Force
Write-Host "Native -> $dist" -ForegroundColor Green
