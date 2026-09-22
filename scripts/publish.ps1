# Packages Local AI Model Manager into runnable software.
#
# Produces, under artifacts\:
#   LocalAIModelManager-<version>-win-x64\             runnable folder (needs .NET 10 runtimes)
#   LocalAIModelManager-<version>-win-x64.zip          same, for distribution
#   LocalAIModelManager-<version>-win-x64-portable\    bundled runtimes, no install needed
#   LocalAIModelManager-<version>-win-x64-portable.zip
#
# Usage:
#   .\scripts\publish.ps1                 # framework-dependent package + zip
#   .\scripts\publish.ps1 -Portable       # also build the no-install bundle
#   .\scripts\publish.ps1 -NoZip
#
# NOTE: this file is intentionally ASCII-only. Windows PowerShell 5.1 parses .ps1
# files as ANSI unless they carry a UTF-8 BOM, so any localised text lives in
# docs\packaging\*.txt (read with -Encoding UTF8) instead of in this script.
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [switch]$Portable,
    [switch]$NoZip,
    [switch]$NoBuild
)

. (Join-Path $PSScriptRoot 'env.ps1')

$root = Get-LammRepoRoot
$artifacts = Join-Path $root 'artifacts'
$baseName = "LocalAIModelManager-$Version-win-x64"
$frameworkDir = Join-Path $artifacts $baseName
$portableDir = Join-Path $artifacts "$baseName-portable"

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Copy-Tree {
    param([string]$Source, [string]$Destination, [string]$Filter = '*')
    if (-not (Test-Path $Source)) { return 0 }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $files = Get-ChildItem -Path $Source -Filter $Filter -File -Recurse
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($Source.Length).TrimStart('\')
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item $file.FullName $target -Force
    }
    return $files.Count
}

function Get-DirectorySize {
    param([string]$Directory)
    return (Get-ChildItem $Directory -Recurse -File | Measure-Object -Property Length -Sum).Sum
}

function Copy-RuntimeNotes {
    param([string]$Directory, [bool]$PortableBundle)

    $templateName = if ($PortableBundle) { 'README-FIRST-portable.txt' } else { 'README-FIRST-framework.txt' }
    $template = Join-Path $root "docs\packaging\$templateName"
    if (-not (Test-Path $template)) { throw "packaging note template not found: $template" }

    $target = Join-Path $Directory 'README-FIRST.txt'
    Get-Content $template -Encoding UTF8 | Set-Content $target -Encoding UTF8

    $manifest = @(
        '',
        'Package manifest',
        '---------------',
        "version          : $Version",
        "runtime          : win-x64, $(if ($PortableBundle) { 'self-contained (bundled runtimes)' } else { 'framework-dependent' })",
        "built            : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        ''
    )
    Add-Content $target -Value $manifest -Encoding UTF8
}

function New-Zip {
    param([string]$SourceDirectory, [string]$ZipPath)

    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }

    # Both Compress-Archive and .NET Framework's ZipFile.CreateFromDirectory write
    # entry names with backslashes (invalid ZIP, and unreadable by non-Windows
    # extractors). Building the archive entry by entry lets us emit proper "/".
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $source = (Resolve-Path $SourceDirectory).Path
    $archive = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $source -Recurse -File) {
            $relative = $file.FullName.Substring($source.Length).TrimStart('\') -replace '\\', '/'
            $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            try {
                $input = [System.IO.File]::OpenRead($file.FullName)
                try { $input.CopyTo($entryStream) } finally { $input.Dispose() }
            }
            finally { $entryStream.Dispose() }
        }
    }
    finally { $archive.Dispose() }

    return (Get-Item $ZipPath).Length
}

if (-not $NoBuild) {
    Invoke-LammBuild -Configuration Release -Quiet
}

# --- 1. framework-dependent package -----------------------------------------
Write-Host 'Publishing (framework-dependent)...' -ForegroundColor Cyan
Remove-Item -Recurse -Force $frameworkDir -ErrorAction SilentlyContinue
Invoke-Dotnet @(
    'publish', (Join-Path $root 'src\LocalAIModelManager.App\LocalAIModelManager.App.csproj'),
    '-c', 'Release', '-m:1', '-nodeReuse:false', '--nologo', '-v', 'quiet',
    '-o', $frameworkDir
)

$appExe = Join-Path $frameworkDir 'LocalAIModelManager.exe'
if (-not (Test-Path $appExe)) { throw "publish did not produce $appExe" }

foreach ($required in 'LocalAIModelManager.dll', 'LocalAIModelManager.ControlHelper.exe') {
    if (-not (Test-Path (Join-Path $frameworkDir $required))) {
        throw "publish output is missing $required"
    }
}

# Ship the offline mock engine as an optional demo engine.
$mockSourceDir = Split-Path (Get-LammMockEngineExecutable -Configuration Release) -Parent
$mockTargetDir = Join-Path $frameworkDir 'engines\mock'
$mockCount = Copy-Tree -Source $mockSourceDir -Destination $mockTargetDir -Filter 'LocalAIModelManager.MockEngine.*'
if ($mockCount -gt 0) {
    Rename-Item (Join-Path $mockTargetDir 'LocalAIModelManager.MockEngine.exe') 'llama-server.exe'
    Write-Host "  demo engine: $mockCount file(s) -> engines\mock\llama-server.exe"
}

Copy-RuntimeNotes -Directory $frameworkDir -PortableBundle $false
Write-Host ("  -> {0} ({1:N1} MiB)" -f $frameworkDir, ((Get-DirectorySize $frameworkDir) / 1MB))

# --- 2. optional no-install bundle -------------------------------------------
# A hand-written "includedFrameworks" runtimeconfig does NOT work: whether an
# apphost is self-contained is decided at apphost build time by the SDK, and that
# requires the runtime packs (unavailable offline here). The supported way to ship
# a bundle that needs no installed .NET is a PRIVATE .NET INSTALLATION next to the
# app, activated by pointing DOTNET_ROOT at it (see Start.cmd).
if ($Portable) {
    Write-Host 'Building no-install bundle (private .NET runtime)...' -ForegroundColor Cyan
    Remove-Item -Recurse -Force $portableDir -ErrorAction SilentlyContinue
    Copy-Item $frameworkDir $portableDir -Recurse -Force

    $dotnetExe = (Get-Command dotnet).Source
    $dotnetRoot = Split-Path $dotnetExe -Parent
    $privateRoot = Join-Path $portableDir 'dotnet'
    New-Item -ItemType Directory -Force -Path $privateRoot | Out-Null

    Copy-Item $dotnetExe (Join-Path $privateRoot 'dotnet.exe') -Force

    # hostfxr is what an apphost resolves first; it is not part of a shared framework.
    $hostFxrVersion = Get-ChildItem (Join-Path $dotnetRoot 'host\fxr') -Directory |
        Where-Object { $_.Name -like '10.*' } |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1
    if (-not $hostFxrVersion) { throw "no 10.x hostfxr found under $dotnetRoot" }
    Copy-Tree -Source $hostFxrVersion.FullName -Destination (Join-Path $privateRoot "host\fxr\$($hostFxrVersion.Name)") | Out-Null
    Write-Host ("  bundled host\fxr {0}" -f $hostFxrVersion.Name)

    $frameworks = @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App', 'Microsoft.AspNetCore.App')
    $resolved = @{}

    foreach ($framework in $frameworks) {
        $candidate = Get-ChildItem (Join-Path $dotnetRoot "shared\$framework") -Directory |
            Where-Object { $_.Name -like '10.*' } |
            Sort-Object { [version]$_.Name } -Descending |
            Select-Object -First 1

        if (-not $candidate) { throw "no 10.x $framework found under $dotnetRoot" }
        $resolved[$framework] = $candidate.Name

        $copied = Copy-Tree -Source $candidate.FullName -Destination (Join-Path $privateRoot "shared\$framework\$($candidate.Name)")
        Write-Host ("  bundled {0} {1} ({2} files)" -f $framework, $candidate.Name, $copied)
    }

    foreach ($required in 'dotnet.exe', 'host\fxr', 'shared\Microsoft.NETCore.App', 'shared\Microsoft.WindowsDesktop.App', 'shared\Microsoft.AspNetCore.App') {
        if (-not (Test-Path (Join-Path $privateRoot $required))) {
            throw "no-install bundle is incomplete: $required was not bundled"
        }
    }

    # Launcher: point the apphost at the private runtime and detach.
    $launcher = @(
        '@echo off',
        'rem Launch Local AI Model Manager using the private .NET runtime in .\dotnet',
        'setlocal',
        'set "DOTNET_ROOT=%~dp0dotnet"',
        'set "DOTNET_MULTILEVEL_LOOKUP=0"',
        'start "" "%~dp0LocalAIModelManager.exe" %*',
        'endlocal'
    )
    $launcher | Set-Content (Join-Path $portableDir 'Start.cmd') -Encoding ASCII

    Copy-RuntimeNotes -Directory $portableDir -PortableBundle $true
    Write-Host ("  -> {0} ({1:N1} MiB)" -f $portableDir, ((Get-DirectorySize $portableDir) / 1MB))
}

# --- 3. zip ------------------------------------------------------------------
if (-not $NoZip) {
    Write-Host 'Compressing...' -ForegroundColor Cyan

    $zip = Join-Path $artifacts "$baseName.zip"
    Write-Host ("  -> {0} ({1:N1} MiB)" -f $zip, ((New-Zip -SourceDirectory $frameworkDir -ZipPath $zip) / 1MB))

    if ($Portable) {
        $portableZip = Join-Path $artifacts "$baseName-portable.zip"
        Write-Host ("  -> {0} ({1:N1} MiB)" -f $portableZip, ((New-Zip -SourceDirectory $portableDir -ZipPath $portableZip) / 1MB))
    }
}

Write-Host ''
Write-Host 'Publish succeeded.' -ForegroundColor Green
Write-Host "  Runnable exe : $appExe"
if ($Portable) {
    Write-Host "  No-install   : $(Join-Path $portableDir 'LocalAIModelManager.exe')"
}
Write-Host ''
Write-Host 'Verify the packaged build end to end with:'
Write-Host "  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\app-smoke.ps1 -NoBuild -AppExecutable `"$appExe`""
