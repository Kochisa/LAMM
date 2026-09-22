# Runs the offline unit + acceptance harness (real child processes, real Kestrel).
#
# Usage:
#   .\scripts\acceptance.ps1                 # everything
#   .\scripts\acceptance.ps1 -Filter ACCEPTANCE
#   .\scripts\acceptance.ps1 -NoBuild
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$Filter = '',
    [switch]$NoBuild
)

. (Join-Path $PSScriptRoot 'env.ps1')

if (-not $NoBuild) {
    Invoke-LammBuild -Configuration $Configuration -Quiet
}

$exe = Get-LammTestExecutable -Configuration $Configuration
if (-not (Test-Path $exe)) {
    throw "Test harness not found: $exe (run scripts/build.ps1 first)"
}

$arguments = @()
if ($Filter) { $arguments += $Filter }

& $exe @arguments
$exitCode = $LASTEXITCODE

Write-Host ''
if ($exitCode -eq 0) {
    Write-Host 'Acceptance harness passed.' -ForegroundColor Green
} else {
    Write-Host "Acceptance harness reported failures (exit code $exitCode)." -ForegroundColor Red
}

exit $exitCode
