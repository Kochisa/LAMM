# Starts the desktop application.
#
# Usage:
#   .\scripts\run.ps1
#   .\scripts\run.ps1 -Minimized
#   .\scripts\run.ps1 -ConfigDirectory C:\temp\lamm-config
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Minimized,
    [switch]$NoBuild,
    [string]$ConfigDirectory = ''
)

. (Join-Path $PSScriptRoot 'env.ps1')

if (-not $NoBuild) {
    Invoke-LammBuild -Configuration $Configuration -Quiet
}

$exe = Get-LammAppExecutable -Configuration $Configuration
if (-not (Test-Path $exe)) {
    throw "Application not found: $exe (run scripts/build.ps1 first)"
}

if ($ConfigDirectory) {
    $env:LAMM_CONFIG_DIR = $ConfigDirectory
}

$arguments = @()
if ($Minimized) { $arguments += '--minimized' }

Write-Host "Starting $exe $($arguments -join ' ')"
Write-Host 'The manager starts with every model in standby; nothing is loaded until a request arrives.'

Start-Process -FilePath $exe -ArgumentList $arguments
