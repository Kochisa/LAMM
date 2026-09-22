# End-to-end smoke test of the SHIPPED desktop application.
#
# It starts the real LocalAIModelManager.exe with a throwaway configuration
# directory and verifies the acceptance story through the public HTTP API:
#
#   start manager -> nothing loaded -> request model A -> engine starts ->
#   model loads -> response streams -> idle timeout -> unload + VRAM release ->
#   next request reloads -> exit leaves no orphan processes.
#
# The offline mock engine stands in for llama-server so this runs on any machine.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoBuild,
    [switch]$KeepSandbox,
    # Point the smoke test at an already packaged build, e.g. artifacts\...\LocalAIModelManager.exe
    [string]$AppExecutable = '',
    [string]$MockEngineExecutable = ''
)

. (Join-Path $PSScriptRoot 'env.ps1')

$failures = New-Object System.Collections.Generic.List[string]
$checks = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    $script:checks++
    if ($Condition) {
        Write-Host "  PASS  $Message" -ForegroundColor Green
    } else {
        Write-Host "  FAIL  $Message" -ForegroundColor Red
        $script:failures.Add($Message)
    }
}

function Get-LlamaServerProcesses {
    @(Get-Process -Name 'llama-server' -ErrorAction SilentlyContinue)
}

if (-not $NoBuild) {
    Invoke-LammBuild -Configuration $Configuration -Quiet
}

$appExe = if ($AppExecutable) { $AppExecutable } else { Get-LammAppExecutable -Configuration $Configuration }
$mockExe = if ($MockEngineExecutable) { $MockEngineExecutable } else { Get-LammMockEngineExecutable -Configuration $Configuration }

if (-not (Test-Path $appExe)) { throw "Application not found: $appExe" }
if (-not (Test-Path $mockExe)) { throw "Mock engine not found: $mockExe" }

Write-Host "Application under test: $appExe"

$sandbox = Join-Path $env:TEMP ("lamm-app-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$engineDir = Join-Path $sandbox 'engines\b6379'
$modelsDir = Join-Path $sandbox 'models'
New-Item -ItemType Directory -Force -Path $engineDir, $modelsDir | Out-Null

# --- fixture: an "installed" engine build and a model file -------------------
$engineExe = Join-Path $engineDir 'llama-server.exe'
Copy-Item $mockExe $engineExe -Force
foreach ($file in Get-ChildItem (Split-Path $mockExe -Parent)) {
    if ($file.Name -like 'LocalAIModelManager.MockEngine*' -or $file.Extension -in '.json', '.dll') {
        Copy-Item $file.FullName (Join-Path $engineDir $file.Name) -Force
    }
}
Set-Content -Path (Join-Path $engineDir 'mock-engine.version') -Value 'b6379-smoke' -NoNewline

$modelFile = Join-Path $modelsDir 'model-a.gguf'
[System.IO.File]::WriteAllBytes($modelFile, [byte[]](1..4096 | ForEach-Object { $_ % 251 }))

$gatewayPort = Get-FreeTcpPort
$apiKey = 'sk-lamm-app-smoke-key-1234'
$baseUrl = "http://127.0.0.1:$gatewayPort"

$settings = [ordered]@{
    schemaVersion = 1
    general = [ordered]@{
        startWithWindows = $false
        startMinimized = $true
        closeToTray = $false
        minimizeToTray = $false
        confirmExit = $false
        theme = 'Dark'
        probeEnginesOnStartup = $true
    }
    api = [ordered]@{
        host = '127.0.0.1'
        port = $gatewayPort
        allowLanAccess = $false
        apiKeyEnabled = $true
        apiKey = $apiKey
        maxConcurrentRequests = 64
        requestTimeoutSeconds = 120
        enableStatusEndpoint = $true
    }
    engines = [ordered]@{
        selectedEngineId = 'mock'
        engines = @(
            [ordered]@{
                id = 'mock'
                name = 'llama.cpp (mock)'
                adapterKind = 'llamacpp'
                executablePath = $engineExe
                startupTimeoutSeconds = 60
                shutdownGraceSeconds = 2
            }
        )
    }
    lifecycle = [ordered]@{
        idleTimeoutSeconds = 4
        idleUnloadEnabled = $true
        maxLoadedModels = 2
        vramEvictionEnabled = $false
        minFreeVramMiB = 0
        shutdownGraceSeconds = 2
        preloadOnStartup = $false
    }
    resources = [ordered]@{ monitorEnabled = $false; pollIntervalMs = 2000 }
    network = [ordered]@{ internalPortRangeStart = 36100; internalPortRangeEnd = 36200 }
    advanced = [ordered]@{ logLevel = 'Debug'; logBufferSize = 2000; logToDisk = $false; logPromptContent = $false }
}

$models = [ordered]@{
    schemaVersion = 1
    models = @(
        [ordered]@{
            id = 'model-a'
            displayName = 'Model A'
            filePath = $modelFile
            engineId = 'mock'
            enabled = $true
            autoLoad = $false
            parameters = [ordered]@{ '--mock-load-ms' = '400'; '--ctx-size' = '2048' }
        }
    )
}

$settings | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $sandbox 'settings.json') -Encoding UTF8
$models | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $sandbox 'models.json') -Encoding UTF8

$headers = @{ Authorization = "Bearer $apiKey" }
$app = $null

try {
    Write-Host ''
    Write-Host "Sandbox: $sandbox"
    Write-Host "Gateway: $baseUrl"
    Write-Host ''

    # --- 1. start the manager ------------------------------------------------
    $env:LAMM_CONFIG_DIR = $sandbox
    $app = Start-Process -FilePath $appExe -ArgumentList '--minimized' -PassThru -WindowStyle Minimized

    $healthy = $false
    for ($i = 0; $i -lt 120; $i++) {
        Start-Sleep -Milliseconds 500
        try {
            $response = Invoke-WebRequest -Uri "$baseUrl/health" -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -eq 200) { $healthy = $true; break }
        } catch {
            if ($app.HasExited) { break }
        }
    }

    Assert-True $healthy 'the gateway becomes reachable shortly after startup'
    if (-not $healthy) {
        throw "The application did not start listening on $baseUrl (exit code $($app.ExitCode))."
    }

    Write-Host ''
    Write-Host '1) startup: manager only, nothing loaded' -ForegroundColor Cyan

    $engineProcesses = Get-LlamaServerProcesses
    Assert-True ($engineProcesses.Count -eq 0) 'no engine process exists after startup'

    $modelsResponse = Invoke-RestMethod -Uri "$baseUrl/v1/models" -Headers $headers
    $modelA = $modelsResponse.data | Where-Object { $_.id -eq 'model-a' }
    Assert-True ($null -ne $modelA) '/v1/models lists the registered model'
    Assert-True ($modelA.lamm.loaded -eq $false) 'the model is reported as NOT loaded at startup'
    Assert-True ($modelA.lamm.state -eq 'standby') 'the model state is standby at startup'

    $status = Invoke-RestMethod -Uri "$baseUrl/v1/internal/status" -Headers $headers
    Assert-True ($status.loadedModelCount -eq 0) 'the runtime snapshot reports 0 loaded models'

    Write-Host ''
    Write-Host '2) API key enforcement' -ForegroundColor Cyan
    $unauthorized = $false
    try {
        Invoke-WebRequest -Uri "$baseUrl/v1/models" -UseBasicParsing -TimeoutSec 5 | Out-Null
    } catch {
        $unauthorized = $_.Exception.Response.StatusCode.value__ -eq 401
    }
    Assert-True $unauthorized 'a request without the API key is rejected with 401'

    Write-Host ''
    Write-Host '3) on-demand load and serve' -ForegroundColor Cyan
    $body = @{
        model    = 'model-a'
        messages = @(@{ role = 'user'; content = 'hello from the app smoke test' })
        stream   = $false
    } | ConvertTo-Json -Depth 6

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $completion = Invoke-RestMethod -Uri "$baseUrl/v1/chat/completions" -Method Post -Headers $headers -Body $body -ContentType 'application/json'
    $stopwatch.Stop()
    $content = $completion.choices[0].message.content

    Assert-True ($content.Contains('[mock:model-a]')) 'the completion came back from the auto-started engine'
    Assert-True ($content.Contains('hello from the app smoke test')) 'the prompt was forwarded verbatim'
    Assert-True ($content.Contains('ctx=2048')) 'the per-model parameter reached the engine command line'
    Assert-True ($stopwatch.ElapsedMilliseconds -ge 350) "the request waited for the model to load ($($stopwatch.ElapsedMilliseconds) ms)"

    $engineProcesses = Get-LlamaServerProcesses
    Assert-True ($engineProcesses.Count -eq 1) 'exactly one engine child process is now running'

    $status = Invoke-RestMethod -Uri "$baseUrl/v1/internal/status" -Headers $headers
    $loadedModel = $status.models | Where-Object { $_.modelId -eq 'model-a' }
    Assert-True ($status.loadedModelCount -eq 1) 'the runtime snapshot reports 1 loaded model'
    Assert-True ($loadedModel.state -eq 'Ready') 'the model state is Ready'
    Assert-True ($loadedModel.pid -gt 0) 'the loaded model exposes its engine PID'
    Assert-True ($loadedModel.port -ge 36100 -and $loadedModel.port -le 36200) "the internal port comes from the configured range ($($loadedModel.port))"

    Write-Host ''
    Write-Host '4) streaming (SSE)' -ForegroundColor Cyan
    $streamBody = @{
        model    = 'model-a'
        messages = @(@{ role = 'user'; content = 'stream this' })
        stream   = $true
    } | ConvertTo-Json -Depth 6

    $streamResponse = Invoke-WebRequest -Uri "$baseUrl/v1/chat/completions" -Method Post -Headers $headers `
        -Body $streamBody -ContentType 'application/json' -UseBasicParsing
    $text = [string]$streamResponse.Content
    Assert-True ([string]$streamResponse.Headers['Content-Type'] -like '*text/event-stream*') 'the streaming response uses text/event-stream'
    Assert-True ($text.Contains('chat.completion.chunk')) 'the stream contains OpenAI chunk objects'
    Assert-True ($text.Contains('[DONE]')) 'the stream terminates with [DONE]'

    Write-Host ''
    Write-Host '5) idle timeout unloads and releases VRAM' -ForegroundColor Cyan
    $unloaded = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 1
        $status = Invoke-RestMethod -Uri "$baseUrl/v1/internal/status" -Headers $headers
        if ($status.loadedModelCount -eq 0) { $unloaded = $true; break }
    }

    Assert-True $unloaded 'the idle timeout unloaded the model'
    $engineProcesses = Get-LlamaServerProcesses
    Assert-True ($engineProcesses.Count -eq 0) 'the engine process was terminated (VRAM released)'
    $status = Invoke-RestMethod -Uri "$baseUrl/v1/internal/status" -Headers $headers
    $standby = $status.models | Where-Object { $_.modelId -eq 'model-a' }
    Assert-True ($standby.state -eq 'Standby') 'the model is back in standby'

    Write-Host ''
    Write-Host '6) the next request reloads the model' -ForegroundColor Cyan
    $completion2 = Invoke-RestMethod -Uri "$baseUrl/v1/chat/completions" -Method Post -Headers $headers -Body $body -ContentType 'application/json'
    Assert-True ($completion2.choices[0].message.content.Contains('[mock:model-a]')) 'the model was reloaded on demand'
    $engineProcesses = Get-LlamaServerProcesses
    Assert-True ($engineProcesses.Count -eq 1) 'a fresh engine process is running again'

    Write-Host ''
    Write-Host '7) logs stay in memory' -ForegroundColor Cyan
    $logFiles = @(Get-ChildItem -Path $sandbox -Recurse -Include '*.log', '*.txt' -ErrorAction SilentlyContinue)
    Assert-True ($logFiles.Count -eq 0) 'no log file was written to disk'
}
catch {
    Write-Host ''
    Write-Host "Smoke test aborted: $($_.Exception.Message)" -ForegroundColor Red
    $failures.Add("aborted: $($_.Exception.Message)")
}
finally {
    Write-Host ''
    Write-Host '8) shutdown leaves no orphan processes' -ForegroundColor Cyan

    if ($app -and -not $app.HasExited) {
        $app.CloseMainWindow() | Out-Null
        if (-not $app.WaitForExit(15000)) {
            $app.Kill()
            $app.WaitForExit(10000)
        }
    }

    Start-Sleep -Seconds 2
    $orphans = Get-LlamaServerProcesses
    Assert-True ($orphans.Count -eq 0) 'no engine process survived the application exit'

    if ($orphans.Count -gt 0) {
        $orphans | Stop-Process -Force -ErrorAction SilentlyContinue
    }

    Remove-Item Env:\LAMM_CONFIG_DIR -ErrorAction SilentlyContinue

    if (-not $KeepSandbox) {
        Remove-Item -Recurse -Force $sandbox -ErrorAction SilentlyContinue
    } else {
        Write-Host "Sandbox kept at $sandbox"
    }

    Write-Host ''
    if ($failures.Count -eq 0) {
        Write-Host "Application smoke test PASSED ($checks checks)." -ForegroundColor Green
        exit 0
    }

    Write-Host "Application smoke test FAILED ($($failures.Count)/$checks checks)." -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
    exit 1
}
