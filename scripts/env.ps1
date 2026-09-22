# Shared environment for Local AI Model Manager build/run scripts.
#
# Notes for restricted/sandboxed hosts:
#   * The .NET CLI writes first-run sentinels to %USERPROFILE%\.dotnet; DOTNET_CLI_HOME
#     redirects that into the repository so the CLI can run without a writable profile.
#   * MSBuild's out-of-process worker nodes communicate over named pipes, which some
#     sandboxes forbid. Every script therefore builds single-node (-m:1) with node
#     reuse disabled. On an unrestricted machine you can drop those switches.

$ErrorActionPreference = 'Stop'

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:MSBUILDDISABLENODEREUSE = '1'

if (-not $env:DOTNET_CLI_HOME) {
    $env:DOTNET_CLI_HOME = Join-Path $env:TEMP 'lamm-dotnet-home'
}

if (-not (Test-Path $env:DOTNET_CLI_HOME)) {
    New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null
}

function Get-LammRepoRoot { $script:RepoRoot }

function Get-LammConfiguration {
    param([string]$Configuration = 'Debug')
    return $Configuration
}

function Get-LammAppExecutable {
    param([string]$Configuration = 'Debug')
    return Join-Path $script:RepoRoot "src\LocalAIModelManager.App\bin\$Configuration\net10.0-windows\LocalAIModelManager.exe"
}

function Get-LammMockEngineExecutable {
    param([string]$Configuration = 'Debug')
    return Join-Path $script:RepoRoot "src\LocalAIModelManager.MockEngine\bin\$Configuration\net10.0-windows\LocalAIModelManager.MockEngine.exe"
}

function Invoke-LammBuild {
    param(
        [string]$Configuration = 'Debug',
        [string]$Target = '',
        [switch]$Quiet
    )

    $arguments = @('build', (Join-Path $script:RepoRoot 'LocalAIModelManager.sln'),
        '-c', $Configuration, '-m:1', '-nodeReuse:false', '--nologo')

    if ($Target) { $arguments += $Target }
    if (-not $Quiet) { $arguments += @('-v', 'minimal') } else { $arguments += @('-v', 'quiet') }

    Push-Location $script:RepoRoot
    try {
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    return $port
}
