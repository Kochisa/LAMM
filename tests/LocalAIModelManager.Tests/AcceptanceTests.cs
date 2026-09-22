using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LocalAIModelManager.Core.Backends;
using LocalAIModelManager.Core.Configuration;
using LocalAIModelManager.Core.Lifecycle;
using LocalAIModelManager.Core.Models;
using LocalAIModelManager.Core.Runtime;

namespace LocalAIModelManager.Tests;

/// <summary>
/// End-to-end verification against real Kestrel listeners and real child processes.
/// The mock engine stands in for llama-server so the acceptance criteria can be
/// demonstrated on a machine with no llama.cpp build and no model weights.
/// </summary>
internal static class AcceptanceTests
{
    private const string ApiKey = "sk-lamm-test-key-000000000000";

    public static void Register()
    {
        TestFramework.Case("ACCEPTANCE: start -> standby -> load on demand -> stream -> idle unload -> reload", AcceptanceFlowAsync);
        TestFramework.Case("lifecycle: multiple models stay loaded, the loaded-model limit evicts the LRU", LruEvictionAsync);
        TestFramework.Case("api: authentication can be enabled, disabled and rejects wrong keys", ApiKeyEnforcementAsync);
        TestFramework.Case("engines: capability probe follows the installed build (version switching)", EngineVersionSwitchingAsync);
        TestFramework.Case("lifecycle: a failing load surfaces as 502 and a Failed state", LoadFailureAsync);
        TestFramework.Case("process: graceful stop first, forced kill on timeout, and no orphans", ProcessShutdownAsync);
        TestFramework.Case("logs: nothing is written to disk unless the user saves a snapshot", LogsStayInMemoryAsync);
        TestFramework.Case("api: /v1/models lists every registered model, loaded or not", ModelListingAsync);
    }

    // ---------------------------------------------------------------- acceptance

    private static async Task AcceptanceFlowAsync()
    {
        using var workspace = TestWorkspace.Create("acceptance");
        await using var runtime = workspace.CreateRuntime(s =>
        {
            s.Lifecycle.IdleTimeoutSeconds = 3;
            s.Lifecycle.MaxLoadedModels = 2;
        });

        runtime.Models.Add(workspace.CreateModel("model-a", parameters: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["--mock-load-ms"] = "600",
            ["--ctx-size"] = "2048",
            ["--n-gpu-layers"] = "99",
        }));
        runtime.Models.Add(workspace.CreateModel("model-b"));

        // ---- 1. starting the manager loads nothing --------------------------
        await runtime.StartAsync();

        var status = runtime.GetStatus();
        Assert.True(status.Gateway.IsRunning, "the gateway is running after startup");
        Assert.Equal(0, status.LoadedModelCount, "no model may be loaded at startup");
        Assert.Equal(2, status.StandbyModelCount, "every model starts in standby");
        Assert.Equal(0, runtime.Processes.LiveCount, "no engine process may exist at startup");
        Assert.True(status.Models.All(m => m.State == ModelState.Standby), "all models report Standby");
        Assert.True(status.Models.All(m => m.Pid is null), "no model has a pid at startup");

        var baseUrl = $"http://127.0.0.1:{workspace.GatewayPort}";
        using var http = TestWorkspace.CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        // ---- 2. catalog is visible without loading anything ------------------
        using (var catalog = await http.GetAsync(baseUrl + "/v1/models"))
        {
            Assert.Equal(HttpStatusCode.OK, catalog.StatusCode, "/v1/models responds");
            var body = await catalog.Content.ReadAsStringAsync();
            Assert.Contains(body, "model-a", "registered model a is listed");
            Assert.Contains(body, "model-b", "registered model b is listed");
            Assert.Contains(body, "\"loaded\":false", "nothing is reported as loaded");
        }

        Assert.Equal(0, runtime.Processes.LiveCount, "listing models must not load anything");

        // ---- 3. authentication is enforced ----------------------------------
        using (var anonymous = TestWorkspace.CreateHttpClient())
        using (var noKey = await anonymous.PostAsync(baseUrl + "/v1/chat/completions", ChatBody("model-a", "nope", stream: false)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode, "a missing API key is rejected");
            Assert.Equal(0, runtime.Processes.LiveCount, "a rejected request must not start an engine");
        }

        // ---- 4. an external request loads the model on demand ----------------
        var loadWatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        string responseBody;
        using (response = await http.PostAsync(baseUrl + "/v1/chat/completions", ChatBody("model-a", "hello from the acceptance test", stream: false)))
        {
            responseBody = await response.Content.ReadAsStringAsync();
            loadWatch.Stop();
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode, $"the completion succeeds: {responseBody}");
        Assert.Contains(responseBody, "[mock:model-a]", "the request reached the engine for model A");
        Assert.Contains(responseBody, "hello from the acceptance test", "the prompt was forwarded verbatim");
        Assert.Contains(responseBody, "ctx=2048", "the per-model parameter reached the engine command line");
        Assert.Contains(responseBody, "usage", "an OpenAI style usage block is returned");
        Assert.True(loadWatch.ElapsedMilliseconds >= 400, $"the request had to wait for the model to load (took {loadWatch.ElapsedMilliseconds}ms)");

        status = runtime.GetStatus();
        Assert.Equal(1, status.LoadedModelCount, "exactly one model is loaded now");
        var modelA = status.Models.Single(m => m.ModelId == "model-a");
        Assert.Equal(ModelState.Ready, modelA.State, "model A is ready");
        Assert.True(modelA.Pid is > 0, "model A has a pid");
        Assert.True(modelA.Port is >= 34300 and <= 34400, $"the internal port comes from the configured range ({modelA.Port})");
        var loadedPid = modelA.Pid!.Value;
        Assert.Equal(1, runtime.Processes.LiveCount, "one engine process is tracked");

        // ---- 5. the internal engine port is bound to loopback only -----------
        using (var internalClient = TestWorkspace.CreateHttpClient())
        {
            var probe = await internalClient.GetAsync($"http://127.0.0.1:{modelA.Port}/mock/state");
            Assert.Equal(HttpStatusCode.OK, probe.StatusCode, "the engine answers on loopback");
            var state = await probe.Content.ReadAsStringAsync();
            Assert.Contains(state, "--n-gpu-layers", "parameters reach the engine process");
            Assert.Contains(state, "99", "parameter values reach the engine process");
        }

        // ---- 6. streaming (SSE) ---------------------------------------------
        var chunks = new List<string>();
        var streamedContent = string.Empty;
        using (var streamRequest = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/chat/completions")
               {
                   Content = ChatBody("model-a", "stream this please", stream: true),
               })
        using (var streamResponse = await http.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode, "the streaming request succeeds");
            Assert.Equal("text/event-stream", streamResponse.Content.Headers.ContentType?.MediaType, "the content type is SSE");
            var stream = await ReadSseAsync(streamResponse);
            chunks = stream.Events;
            streamedContent = stream.Content;
        }

        Assert.True(chunks.Count >= 4, $"the stream produced several chunks ({chunks.Count})");
        Assert.True(chunks.Any(c => c.Contains("[DONE]")), "the stream terminates with [DONE]");
        Assert.Contains(streamedContent, "stream this please", "streamed deltas carry the answer");
        Assert.Contains(streamedContent, "[mock:model-a]", "streamed deltas come from the right engine");
        Assert.True(chunks[0].Contains("chat.completion.chunk"), "chunks use the OpenAI chunk object");

        // ---- 7. idle timeout unloads and releases the process ---------------
        await Assert.EventuallyAsync(
            () => runtime.GetStatus().Models.All(m => m.State == ModelState.Standby),
            TimeSpan.FromSeconds(25),
            "the idle timeout unloads model A");

        Assert.Equal(0, runtime.Processes.LiveCount, "no engine process is left running");
        Assert.True(ProcessIsGone(loadedPid), "the engine process for model A was terminated");
        status = runtime.GetStatus();
        Assert.Equal(0, status.LoadedModelCount, "nothing is loaded after the idle timeout");
        var unloaded = status.Models.Single(m => m.ModelId == "model-a");
        Assert.Equal(BackendStopMode.Graceful, unloaded.LastStopMode, "the engine was stopped gracefully");
        Assert.Equal(0, status.Processes.Count, "the process inventory is empty");
        Assert.True(runtime.Ports.ReservedCount == 0, "the internal port was released");
        Assert.True(status.ExitedProcesses.Count >= 1, "the exited process is recorded in the history");

        // ---- 8. the next request reloads the model --------------------------
        using (var reload = await http.PostAsync(baseUrl + "/v1/chat/completions", ChatBody("model-a", "reload me", stream: false)))
        {
            Assert.Equal(HttpStatusCode.OK, reload.StatusCode, "the model is reloaded on demand");
            var reloadBody = await reload.Content.ReadAsStringAsync();
            Assert.Contains(reloadBody, "[mock:model-a]", "the reloaded engine serves the request");
        }

        var reloaded = runtime.GetStatus().Models.Single(m => m.ModelId == "model-a");
        Assert.Equal(ModelState.Ready, reloaded.State, "model A is ready again");
        Assert.True(reloaded.Pid is > 0, "a new engine process exists");

        // ---- 9. the status endpoint describes the runtime -------------------
        using (var internalStatus = await http.GetAsync(baseUrl + "/v1/internal/status"))
        {
            Assert.Equal(HttpStatusCode.OK, internalStatus.StatusCode, "/v1/internal/status responds");
            var json = await internalStatus.Content.ReadAsStringAsync();
            Assert.Contains(json, "\"loadedModelCount\":1", "the snapshot reports the loaded model");
            Assert.Contains(json, "gateway", "the snapshot reports the gateway");
        }

        // ---- 10. no model file was ever touched, no log file was written -----
        Assert.True(File.Exists(Path.Combine(workspace.ModelsDirectory, "model-a.gguf")), "the model file still exists");
        var strayLogs = Directory.GetFiles(workspace.ConfigDirectory, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(0, strayLogs.Count, "logging never writes files by default");

        // ---- 11. shutdown leaves nothing behind -----------------------------
        var lastPid = reloaded.Pid!.Value;
        await runtime.StopAsync();
        Assert.True(ProcessIsGone(lastPid), "shutdown terminated the engine process");
        Assert.Equal(0, runtime.Processes.LiveCount, "the inventory is empty after shutdown");
    }

    private static async Task LruEvictionAsync()
    {
        using var workspace = TestWorkspace.Create("lru");
        await using var runtime = workspace.CreateRuntime(s =>
        {
            s.Lifecycle.MaxLoadedModels = 1;
            s.Lifecycle.IdleTimeoutSeconds = 600;
            s.Lifecycle.MinFreeVramMiB = 0;
        });

        runtime.Models.Add(workspace.CreateModel("first"));
        runtime.Models.Add(workspace.CreateModel("second"));

        await runtime.StartAsync();

        var baseUrl = $"http://127.0.0.1:{workspace.GatewayPort}";
        using var http = TestWorkspace.CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        await SendAsync(http, baseUrl, "first");
        var afterFirst = runtime.GetStatus();
        Assert.Equal(1, afterFirst.LoadedModelCount, "the first model is loaded");
        var firstPid = afterFirst.Models.Single(m => m.ModelId == "first").Pid!.Value;

        // A second model must evict the idle, least recently used one.
        await SendAsync(http, baseUrl, "second");
        var afterSecond = runtime.GetStatus();
        Assert.Equal(1, afterSecond.LoadedModelCount, "only one model may be loaded");
        Assert.Equal(ModelState.Ready, afterSecond.Models.Single(m => m.ModelId == "second").State, "the second model is ready");
        Assert.Equal(ModelState.Standby, afterSecond.Models.Single(m => m.ModelId == "first").State, "the first model was evicted");
        Assert.Equal(1, runtime.Processes.LiveCount, "exactly one engine process remains");
        Assert.True(ProcessIsGone(firstPid), "the evicted engine process was terminated");
        Assert.Equal(
            BackendStopMode.Graceful,
            afterSecond.Models.Single(m => m.ModelId == "first").LastStopMode,
            "eviction uses the graceful path");

        // And back again: the evicted model reloads on demand.
        await SendAsync(http, baseUrl, "first");
        var afterReturn = runtime.GetStatus();
        Assert.Equal(ModelState.Ready, afterReturn.Models.Single(m => m.ModelId == "first").State, "the first model reloaded");
        Assert.Equal(ModelState.Standby, afterReturn.Models.Single(m => m.ModelId == "second").State, "the second model was evicted");
        Assert.Equal(1, runtime.Processes.LiveCount, "still exactly one engine process");
    }

    private static async Task ApiKeyEnforcementAsync()
    {
        using var workspace = TestWorkspace.Create("apikey");
        await using var runtime = workspace.CreateRuntime(s =>
        {
            s.Api.ApiKeyEnabled = true;
            s.Api.ApiKey = "sk-lamm-explicit-test-key-1234";
        });

        runtime.Models.Add(workspace.CreateModel("secured"));
        await runtime.StartAsync();

        var baseUrl = $"http://127.0.0.1:{workspace.GatewayPort}";

        using (var noKey = TestWorkspace.CreateHttpClient())
        using (var response = await noKey.GetAsync(baseUrl + "/v1/models"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode, "no key means 401");
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains(body, "invalid_api_key", "the error body uses the OpenAI error shape");
        }

        using (var wrongKey = TestWorkspace.CreateHttpClient())
        {
            wrongKey.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-lamm-wrong-key");
            using var response = await wrongKey.GetAsync(baseUrl + "/v1/models");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode, "a wrong key means 401");
        }

        using (var rightKey = TestWorkspace.CreateHttpClient())
        {
            rightKey.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-lamm-explicit-test-key-1234");
            using var response = await rightKey.GetAsync(baseUrl + "/v1/models");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode, "the configured key is accepted");
        }

        // Health stays reachable without a key so supervisors can probe the gateway.
        using (var health = TestWorkspace.CreateHttpClient())
        using (var response = await health.GetAsync(baseUrl + "/health"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode, "/health is unauthenticated");
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains(body, "local-ai-model-manager", "health identifies the service");
            Assert.NotContains(body, "sk-lamm-explicit-test-key-1234", "health must never leak the key");
        }

        // And the API key can be switched off entirely (separate sandbox).
        using var openWorkspace = TestWorkspace.Create("apikey-open");
        await using var openRuntime = openWorkspace.CreateRuntime(s => s.Api.ApiKeyEnabled = false);
        openRuntime.Models.Add(openWorkspace.CreateModel("open"));
        await openRuntime.StartAsync();

        using var openClient = TestWorkspace.CreateHttpClient();
        using var openResponse = await openClient.PostAsync(
            $"http://127.0.0.1:{openRuntime.Gateway.Options.Port}/v1/chat/completions",
            ChatBody("open", "no key needed", stream: false));
        Assert.Equal(HttpStatusCode.OK, openResponse.StatusCode, "authentication can be disabled");
    }

    private static async Task EngineVersionSwitchingAsync()
    {
        using var workspace = TestWorkspace.Create("engines");

        // Build two "installed llama.cpp builds": same binary, different sidecars.
        var v1Dir = Path.Combine(workspace.Root, "engines", "b6379");
        var v2Dir = Path.Combine(workspace.Root, "engines", "b7000");

        var v1Exe = TestWorkspace.CopyMockEngineTo(v1Dir);
        var v2Exe = TestWorkspace.CopyMockEngineTo(v2Dir);

        File.WriteAllText(Path.Combine(v1Dir, "mock-engine.version"), "b6379-old");
        File.WriteAllText(Path.Combine(v2Dir, "mock-engine.version"), "b7000-new");
        // The newer build advertises flags the older one does not have.
        File.WriteAllText(Path.Combine(v2Dir, "mock-engine.flags"),
            "--mtp                        enable multi-token prediction (new in this build)\n" +
            "--split-mode <mode>          how to split the model across GPUs");

        await using var runtime = workspace.CreateRuntime(s =>
        {
            s.Engines.Engines.Clear();
            s.Engines.Engines.Add(new EngineDefinition
            {
                Id = "llamacpp-old",
                Name = "llama.cpp b6379",
                AdapterKind = BackendAdapterKinds.LlamaCpp,
                ExecutablePath = v1Exe,
            });
            s.Engines.Engines.Add(new EngineDefinition
            {
                Id = "llamacpp-new",
                Name = "llama.cpp b7000",
                AdapterKind = BackendAdapterKinds.LlamaCpp,
                ExecutablePath = v2Exe,
            });
            s.Engines.SelectedEngineId = "llamacpp-new";
        });

        var oldCaps = await runtime.InspectEngineAsync("llamacpp-old", forceRefresh: true);
        var newCaps = await runtime.InspectEngineAsync("llamacpp-new", forceRefresh: true);

        Assert.Equal("b6379-old", oldCaps.Version, "the old build reports its own version");
        Assert.Equal("b7000-new", newCaps.Version, "the new build reports its own version");
        Assert.False(oldCaps.Supports("--mtp"), "the old build does not have --mtp");
        Assert.False(oldCaps.Supports("--split-mode"), "the old build does not have --split-mode");
        Assert.True(newCaps.Supports("--mtp"), "the new build advertises --mtp");
        Assert.True(newCaps.Supports("--split-mode"), "the new build advertises --split-mode");
        Assert.True(
            newCaps.Parameters.Count > oldCaps.Parameters.Count,
            "the newer build exposes more parameters than the older one");

        // A model pinned to the old engine silently drops the unsupported flags ...
        var model = workspace.CreateModel("switcher", engineId: "llamacpp-old", parameters: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["--ctx-size"] = "1024",
            ["--mtp"] = "true",
            ["--split-mode"] = "layer",
        });
        runtime.Models.Add(model);

        var test = await runtime.Lifecycle.TestAsync("switcher", CancellationToken.None);
        Assert.True(
            test.Warnings.Any(w => w.Contains("--mtp")),
            "the test action reports that --mtp is not advertised by the installed build");
        Assert.False(test.LaunchArguments.Contains("--mtp"), "unsupported parameters never reach the command line");

        // ... and the very same model supports them after switching the engine.
        var switched = runtime.Models.Get("switcher")!;
        switched.EngineId = "llamacpp-new";
        runtime.Models.Update(switched);

        var switchedTest = await runtime.Lifecycle.TestAsync("switcher", CancellationToken.None);
        Assert.True(switchedTest.LaunchArguments.Contains("--mtp"), "after the engine switch the flag is passed");
        Assert.True(switchedTest.LaunchArguments.Contains("--split-mode"), "after the engine switch the GPU split flag is passed");
        Assert.False(
            switchedTest.Warnings.Any(w => w.Contains("is not advertised")),
            "no capability warnings remain after the switch");

        // The engine's own help output is what drives this - nothing is hard-coded.
        var detected = newCaps.Parameters.FirstOrDefault(p => p.Key == "--mtp");
        Assert.NotNull(detected, "--mtp appears in the catalogued parameter list for the new build");
        Assert.False(detected!.DetectedOnly, "--mtp is a known catalog entry, just an unsupported one on the old build");
    }

    private static async Task LoadFailureAsync()
    {
        using var workspace = TestWorkspace.Create("failure");
        await using var runtime = workspace.CreateRuntime();

        runtime.Models.Add(workspace.CreateModel("broken", parameters: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["--mock-load-ms"] = "100",
            ["--mock-fail-load"] = "true",
        }));

        await runtime.StartAsync();

        var exception = await Assert.ThrowsAsync<ModelUnavailableException>(
            () => runtime.Lifecycle.AcquireAsync("broken", CancellationToken.None));
        Assert.Contains(exception.Message, "broken", "the error names the model");

        var status = runtime.GetStatus().Models.Single(m => m.ModelId == "broken");
        Assert.Equal(ModelState.Failed, status.State, "the model is marked Failed");
        Assert.NotNull(status.LastError, "the failure reason is recorded");
        Assert.Equal(0, runtime.Processes.LiveCount, "no engine process is left behind");
        Assert.True(runtime.Ports.ReservedCount == 0, "the internal port was released after the failure");

        var baseUrl = $"http://127.0.0.1:{workspace.GatewayPort}";
        using var http = TestWorkspace.CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        using var response = await http.PostAsync(baseUrl + "/v1/chat/completions", ChatBody("broken", "will fail", stream: false));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode, "a failed load is reported as 502");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(body, "model_load_failed", "the error code identifies a load failure");

        // An unknown model is a 404, not a 502.
        using var unknown = await http.PostAsync(baseUrl + "/v1/chat/completions", ChatBody("does-not-exist", "hi", stream: false));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode, "unknown models are 404");
    }

    private static async Task ProcessShutdownAsync()
    {
        using var workspace = TestWorkspace.Create("shutdown");
        await using var runtime = workspace.CreateRuntime(s => s.Lifecycle.ShutdownGraceSeconds = 2);

        runtime.Models.Add(workspace.CreateModel("polite", parameters: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["--mock-exit-on-stdin-eof"] = "true",
        }));
        runtime.Models.Add(workspace.CreateModel("stubborn", parameters: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // An engine with no console cannot be signalled gracefully; the supervisor
            // must fall through to the forced kill after the grace period.
            ["--mock-no-console"] = "true",
            ["--mock-load-ms"] = "50",
        }));

        await runtime.StartAsync();

        await runtime.Lifecycle.StartAsync("polite", CancellationToken.None);
        var politePid = runtime.GetStatus().Models.Single(m => m.ModelId == "polite").Pid!.Value;
        await runtime.Lifecycle.StopAsync("polite", CancellationToken.None);

        var politeStatus = runtime.GetStatus().Models.Single(m => m.ModelId == "polite");
        Assert.Equal(ModelState.Standby, politeStatus.State, "the polite engine stopped");
        Assert.Equal(BackendStopMode.Graceful, politeStatus.LastStopMode, "the graceful path was used");
        Assert.True(ProcessIsGone(politePid), "the graceful stop really terminated the process");

        await runtime.Lifecycle.StartAsync("stubborn", CancellationToken.None);
        var stubbornPid = runtime.GetStatus().Models.Single(m => m.ModelId == "stubborn").Pid!.Value;

        var stopWatch = Stopwatch.StartNew();
        await runtime.Lifecycle.StopAsync("stubborn", CancellationToken.None);
        stopWatch.Stop();

        var stubbornStatus = runtime.GetStatus().Models.Single(m => m.ModelId == "stubborn");
        Assert.Equal(ModelState.Standby, stubbornStatus.State, "the stubborn engine stopped too");
        Assert.Equal(
            BackendStopMode.ForcedKill,
            stubbornStatus.LastStopMode,
            "the forced kill path was used. Engine output was:" + Environment.NewLine + EngineTail(runtime));
        Assert.True(ProcessIsGone(stubbornPid), "no orphan process survives the forced kill");
        Assert.True(stopWatch.Elapsed.TotalSeconds >= 1.5, $"the grace period was honoured ({stopWatch.Elapsed.TotalSeconds:F1}s)");

        // Restart must work and produce a fresh process.
        await runtime.Lifecycle.StartAsync("polite", CancellationToken.None);
        var restarted = runtime.GetStatus().Models.Single(m => m.ModelId == "polite");
        Assert.Equal(ModelState.Ready, restarted.State, "restart brings the model back");
        Assert.True(restarted.Pid != politePid, "restart creates a new process");

        // A hard dispose must not leave anything running either.
        var livePid = restarted.Pid!.Value;
        await runtime.DisposeAsync();
        Assert.True(ProcessIsGone(livePid), "disposing the runtime kills the engine");
        Assert.Equal(0, runtime.Processes.LiveCount, "the inventory is empty after dispose");
    }

    private static async Task LogsStayInMemoryAsync()
    {
        using var workspace = TestWorkspace.Create("logs-e2e");
        await using var runtime = workspace.CreateRuntime();
        runtime.Models.Add(workspace.CreateModel("logged"));
        await runtime.StartAsync();

        var baseUrl = $"http://127.0.0.1:{workspace.GatewayPort}";
        using var http = TestWorkspace.CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        await SendAsync(http, baseUrl, "logged", prompt: "SUPERSECRETPROMPTTEXT");

        Assert.True(runtime.Logs.Count > 0, "the in-memory log buffer captured activity");

        var allMessages = string.Join('\n', runtime.Logs.Snapshot().Select(e => e.Message));
        Assert.NotContains(allMessages, ApiKey, "the API key never reaches the log buffer");
        Assert.NotContains(allMessages, "Bearer ", "authorization headers never reach the log buffer");
        Assert.NotContains(allMessages, "SUPERSECRETPROMPTTEXT", "prompt content is not logged by default");

        var files = Directory.GetFiles(workspace.ConfigDirectory, "*", SearchOption.AllDirectories);
        Assert.Equal(0, files.Count(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)), "no log file exists");

        var target = Path.Combine(workspace.Root, "saved.log");
        await runtime.Logs.SaveToFileAsync(target);
        Assert.True(File.Exists(target), "an explicit save writes the snapshot");
        Assert.Contains(await File.ReadAllTextAsync(target), "gateway", "the snapshot contains gateway activity");

        runtime.Logs.Clear();
        Assert.Equal(0, runtime.Logs.Count, "the buffer can be cleared");
    }

    private static async Task ModelListingAsync()
    {
        using var workspace = TestWorkspace.Create("listing");
        await using var runtime = workspace.CreateRuntime();

        runtime.Models.Add(workspace.CreateModel("listed-one"));
        runtime.Models.Add(workspace.CreateModel("listed-two"));
        var disabled = workspace.CreateModel("listed-disabled");
        disabled.Enabled = false;
        runtime.Models.Add(disabled);

        await runtime.StartAsync();

        using var http = TestWorkspace.CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        var baseUrl = $"http://127.0.0.1:{workspace.GatewayPort}";

        using (var response = await http.GetAsync(baseUrl + "/v1/models"))
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains(body, "listed-one", "model one is listed");
            Assert.Contains(body, "listed-two", "model two is listed");
            Assert.Contains(body, "listed-disabled", "disabled models are still listed");
            Assert.Contains(body, "\"state\":\"standby\"", "state is reported for each model");
            Assert.Contains(body, "\"enabled\":false", "the disabled flag is reported");
        }

        using (var single = await http.GetAsync(baseUrl + "/v1/models/listed-one"))
        {
            Assert.Equal(HttpStatusCode.OK, single.StatusCode, "a single model can be fetched");
        }

        using (var missing = await http.GetAsync(baseUrl + "/v1/models/not-registered"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode, "unknown ids are 404");
        }

        using (var disabledCall = await http.PostAsync(baseUrl + "/v1/chat/completions", ChatBody("listed-disabled", "hi", stream: false)))
        {
            Assert.Equal(HttpStatusCode.BadGateway, disabledCall.StatusCode, "a disabled model cannot be served");
        }

        Assert.Equal(0, runtime.Processes.LiveCount, "nothing was loaded by listing models");
    }

    // ------------------------------------------------------------------ helpers

    private static async Task SendAsync(HttpClient http, string baseUrl, string modelId, string prompt = "ping")
    {
        using var response = await http.PostAsync(baseUrl + "/v1/chat/completions", ChatBody(modelId, prompt, stream: false));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode, $"expected a completion for '{modelId}': {body}");
        Assert.Contains(body, $"[mock:{modelId}]", $"the response came from the engine for '{modelId}'");
    }

    private static StringContent ChatBody(string model, string prompt, bool stream) =>
        new(
            JsonSerializer.Serialize(new
            {
                model,
                messages = new[] { new { role = "user", content = prompt } },
                stream,
                temperature = 0.2,
            }),
            Encoding.UTF8,
            "application/json");

    /// <summary>Reads an SSE response, returning both the raw events and the concatenated deltas.</summary>
    private static async Task<(List<string> Events, string Content)> ReadSseAsync(HttpResponseMessage response)
    {
        var events = new List<string>();
        var content = new StringBuilder();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            events.Add(line);
            var payload = line[6..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array &&
                    choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var deltaContent) &&
                    deltaContent.ValueKind == JsonValueKind.String)
                {
                    content.Append(deltaContent.GetString());
                }
            }
            catch (JsonException)
            {
                // Not a chunk we understand; the raw event is still recorded.
            }
        }

        return (events, content.ToString());
    }

    /// <summary>Last engine stdout/stderr lines, used as diagnostics when a stop assertion fails.</summary>
    private static string EngineTail(Core.Runtime.AppRuntime runtime)
    {
        var lines = runtime.Logs.Snapshot()
            .Where(e => e.Category.StartsWith("engine:", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Message)
            .TakeLast(20)
            .ToList();

        return lines.Count == 0 ? "(no engine output captured)" : string.Join(Environment.NewLine, lines);
    }

    private static bool ProcessIsGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
