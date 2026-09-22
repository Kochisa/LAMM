# Builds the whole solution (core, app, mock engine, tests).
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

. (Join-Path $PSScriptRoot 'env.ps1')

Invoke-LammBuild -Configuration $Configuration

Write-Host ''
Write-Host 'Build succeeded.' -ForegroundColor Green
Write-Host "  App        : $(Get-LammAppExecutable -Configuration $Configuration)"
Write-Host "  Tests      : $(Get-LammTestExecutable -Configuration $Configuration)"
Write-Host "  Mock engine: $(Get-LammMockEngineExecutable -Configuration $Configuration)"
