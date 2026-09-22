using System.Text.Json;
using LocalAIModelManager.Core.Backends;
using LocalAIModelManager.Core.Configuration;
using LocalAIModelManager.Core.Gateway;
using LocalAIModelManager.Core.Logging;
using LocalAIModelManager.Core.Models;
using LocalAIModelManager.Core.Processes;
using LocalAIModelManager.Core.Runtime;

namespace LocalAIModelManager.Tests;

internal static class UnitTests
{
    public static void Register()
    {
        TestFramework.Case("settings: defaults are safe (no preload, loopback only, memory logs)", () =>
        {
            var settings = new AppSettings();
            settings.Normalize();

            Assert.False(settings.Lifecycle.PreloadOnStartup, "models must not be preloaded at startup");
            Assert.Equal(ApiSettings.Loopback, settings.Api.Host, "default bind must be loopback");
            Assert.False(settings.Api.AllowLanAccess, "LAN access must be off by default");
            Assert.True(settings.Api.ApiKeyEnabled, "API key must be on by default");
            Assert.True(settings.Api.ApiKey.StartsWith("sk-lamm-", StringComparison.Ordinal), "a key must be generated");
            Assert.Equal(ApiSettings.Loopback, settings.Network.BackendBindHost, "backend ports must stay on loopback");
            Assert.False(settings.Advanced.LogToDisk, "logs must not be written to disk by default");
            Assert.False(settings.Advanced.LogPromptContent, "prompt logging must be off by default");
            Assert.Equal(300, settings.Lifecycle.IdleTimeoutSeconds, "default idle timeout is 5 minutes");
            foreach (var model in new[] { new ModelDefinition { Id = "x", FilePath = "y", EngineId = "e" } })
            {
                model.Normalize();
                Assert.False(model.AutoLoad, "a model entry never opts itself into auto-load");
            }
        });

        TestFramework.Case("settings: gateway options carry the concurrency limit and the loopback exemption", () =>
        {
            var settings = new AppSettings();
            settings.Api.MaxConcurrentRequests = 7;
            settings.Api.RequireApiKeyForLocalhost = false;
            settings.Advanced.MaxRequestBytes = 4096;
            settings.Network.HttpProxy = "http://127.0.0.1:1080";
            settings.Network.NoProxy = "localhost";
            settings.Normalize();

            var options = GatewayOptions.FromSettings(settings);
            Assert.Equal(7, options.MaxConcurrentRequests, "the concurrency limit is carried into the gateway");
            Assert.False(options.RequireApiKeyForLocalhost, "the loopback exemption is carried into the gateway");
            Assert.Equal(4096L, options.MaxRequestBytes, "the request size limit is carried into the gateway");
            Assert.Equal("http://127.0.0.1:1080", settings.Network.HttpProxy, "the engine proxy is preserved");
            Assert.Equal("localhost", settings.Network.NoProxy, "the proxy exclusion list is preserved");
        });

        TestFramework.Case("settings: LAN without an API key cannot be configured accidentally", () =>
        {
            var settings = new AppSettings
            {
                Api = { Host = ApiSettings.AnyAddress, AllowLanAccess = true, ApiKeyEnabled = false },
            };
            settings.Normalize();

            var issues = settings.Validate();
            Assert.True(issues.Any(i => i.Severity == ValidationSeverity.Error), "validation must flag LAN without auth");

            var options = GatewayOptions.FromSettings(settings);
            Assert.Equal(ApiSettings.Loopback, options.Host, "the gateway must fall back to loopback");
            Assert.True(options.ApiKeyEnabled, "authentication must be forced back on");
        });

        TestFramework.Case("settings: explicit opt-in allows LAN without a key but warns", () =>
        {
            var settings = new AppSettings
            {
                Api = { Host = ApiSettings.AnyAddress, AllowLanAccess = true, ApiKeyEnabled = false },
                Advanced = { AllowLanWithoutApiKey = true },
            };
            settings.Normalize();

            var options = GatewayOptions.FromSettings(settings);
            Assert.Equal(ApiSettings.AnyAddress, options.Host, "the explicit opt-in must be honoured");
            Assert.True(
                settings.Validate().Any(i => i.Severity == ValidationSeverity.Warning),
                "an unauthenticated LAN bind must produce a warning");
        });

        TestFramework.Case("settings: LAN disabled always snaps the host back to loopback", () =>
        {
            var settings = new AppSettings { Api = { Host = ApiSettings.AnyAddress, AllowLanAccess = false } };
            settings.Normalize();
            Assert.Equal(ApiSettings.Loopback, GatewayOptions.FromSettings(settings).Host, "LAN off means loopback");
        });

        TestFramework.Case("apikey: generation, masking and constant-time comparison", () =>
        {
            var key = ApiKeyGenerator.Create();
            Assert.True(key.Length > 20, "keys must be long");
            Assert.True(key.StartsWith(ApiKeyGenerator.Prefix, StringComparison.Ordinal), "keys carry the prefix");
            Assert.True(ApiKeyGenerator.FixedTimeEquals(key, key), "identical keys match");
            Assert.False(ApiKeyGenerator.FixedTimeEquals(key, key + "x"), "different keys do not match");
            Assert.False(ApiKeyGenerator.FixedTimeEquals(null, key), "missing keys never match");
            Assert.NotContains(ApiKeyGenerator.Mask(key), key[12..], "masking must hide the body of the key");
            Assert.True(ApiKeyGenerator.Create() != key, "each generated key is unique");
        });

        TestFramework.Case("logging: redactor removes keys, bearer tokens and header values", () =>
        {
            var redactor = new LogRedactor();
            const string secret = "sk-lamm-SUPERSECRETVALUE123456";
            redactor.RegisterSecret(secret);

            var redacted = redactor.Redact($"calling with key={secret} and Authorization: Bearer abcdef123456789");
            Assert.NotContains(redacted, secret, "registered secrets must be removed");
            Assert.NotContains(redacted, "abcdef123456789", "bearer tokens must be removed");
            Assert.Contains(redacted, LogRedactor.Mask, "redaction is visible");

            var json = redactor.Redact("{\"api_key\":\"another-secret-value\"}");
            Assert.NotContains(json, "another-secret-value", "json api_key fields must be removed");

            var body = redactor.DescribeBody(System.Text.Encoding.UTF8.GetBytes("hello prompt"), contentLoggingEnabled: false, maxChars: 100);
            Assert.NotContains(body, "hello", "prompt content must not be logged unless explicitly enabled");
            Assert.Contains(body, "B", "byte count is still recorded");
        });

        TestFramework.Case("logging: ring buffer keeps the newest entries and never touches disk", () =>
        {
            var store = new InMemoryLogStore(capacity: 50, minimumLevel: LogLevel.Trace);
            for (var i = 0; i < 200; i++)
            {
                store.Log(LogLevel.Information, "test", $"entry {i}");
            }

            Assert.Equal(50, store.Count, "the buffer is bounded");
            Assert.Equal(200, store.TotalWritten, "every write is counted");
            Assert.Equal(150, store.DroppedCount, "overwrites are counted");
            Assert.Contains(store.Snapshot().Last().Message, "entry 199", "newest entry survives");

            store.Log(LogLevel.Error, "test", "special-needle");
            Assert.Equal(1, store.Query("special-needle").Count, "search finds entries");
            Assert.Equal(1, store.Query(minimumLevel: LogLevel.Error).Count, "level filter works");

            store.IsPaused = true;
            store.Log(LogLevel.Error, "test", "ignored-while-paused");
            Assert.Equal(0, store.Query("ignored-while-paused").Count, "pause suspends capture");
            store.IsPaused = false;

            store.Log(LogLevel.Information, "test", "secret sk-lamm-abcdefghijklmnop");
            var snapshot = store.Snapshot().Last().Message;
            Assert.Contains(snapshot, "secret", "message text is kept");
            Assert.NotContains(snapshot, "abcdefghijklmnop", "the key pattern is masked even without registration");
        });

        TestFramework.Case("logging: explicit save is the only path that writes a file", async () =>
        {
            using var workspace = TestWorkspace.Create("logs");
            var store = new InMemoryLogStore(capacity: 10, minimumLevel: LogLevel.Trace);
            store.Log(LogLevel.Information, "test", "hello");

            var logFiles = Directory.GetFiles(workspace.Root, "*.log", SearchOption.AllDirectories);
            Assert.Equal(0, logFiles.Length, "logging must not create files by itself");

            var target = Path.Combine(workspace.Root, "exports", "snapshot.log");
            var written = await store.SaveToFileAsync(target);
            Assert.Equal(1, written, "one entry is exported");
            Assert.True(File.Exists(target), "explicit save creates the file");
            Assert.Contains(await File.ReadAllTextAsync(target), "hello", "the export contains the entry");
        });

        TestFramework.Case("llamacpp: help output drives capability detection", () =>
        {
            // Subset of the mock engine help: some catalogued flags present, some absent.
            const string help = """
                usage: llama-server [options]
                build: b6379-mock (1a2b3c4d)
                options:
                  -m, --model <file>       model file to load
                  -c, --ctx-size <n>       size of the prompt context (default: 4096)
                  --cache-type-k <type>    KV cache data type for K
                  --cache-type-v <type>    KV cache data type for V
                  --flash-attn             enable flash attention
                  -ngl, --n-gpu-layers <n> number of layers to store in VRAM
                  -t, --threads <n>        number of CPU threads
                  -b, --batch-size <n>     logical batch size
                  -ub, --ubatch-size <n>   physical batch size
                  --alias <name>           model name reported by the API
                  --no-webui               disable the built-in web UI
                  --swa-full               use a full-size SWA cache
                """;

            var capabilities = LlamaHelpParser.Parse("engine-1", @"C:\llama\llama-server.exe", help);

            Assert.True(capabilities.IsAvailable, "the engine is available");
            Assert.Equal("b6379-mock", capabilities.Version, "version is parsed from the build line");
            Assert.True(capabilities.Supports("--ctx-size"), "ctx-size is detected");
            Assert.True(capabilities.Supports("-c"), "aliases count as support");
            Assert.True(capabilities.Supports("--n-gpu-layers"), "gpu layers are detected");
            Assert.True(capabilities.Supports("--ubatch-size"), "u-batch size is detected");
            Assert.False(capabilities.Supports("--split-mode"), "absent flags must not be offered");
            Assert.False(capabilities.Supports("--mtp"), "MTP must not be assumed to exist");
            Assert.False(capabilities.Supports("--tensor-split"), "tensor split must not be assumed");

            var swaFull = capabilities.Parameters.FirstOrDefault(p => p.Key == "--swa-full");
            Assert.NotNull(swaFull, "flags outside the catalog are surfaced as detected parameters");
            Assert.True(swaFull!.DetectedOnly, "detected-only parameters are marked as such");

            var ctx = capabilities.Find("--ctx-size");
            Assert.NotNull(ctx, "the catalog entry is kept");
            Assert.Equal(ParameterCategories.Context, ctx!.Category, "categories drive the settings UI");
            Assert.Equal(ParameterKind.Integer, ctx.Kind, "integer parameters are typed");
        });

        TestFramework.Case("llamacpp: launch plan filters unsupported flags and keeps the managed ones intact", () =>
        {
            const string help = """
                usage: llama-server [options]
                  -m, --model <file>       model file
                  --host <ip>              host
                  --port <n>               port
                  -c, --ctx-size <n>       context size
                  -ngl, --n-gpu-layers <n> gpu layers
                  --cache-type-k <type>    kv type k
                  --flash-attn             flash attention
                  --alias <name>           alias
                  --no-webui               disable web ui
                """;

            var capabilities = LlamaHelpParser.Parse("engine-1", @"C:\llama\llama-server.exe", help);
            var adapter = new LlamaCppBackendAdapter();

            var model = new ModelDefinition
            {
                Id = "model-a",
                DisplayName = "Model A",
                FilePath = @"C:\models\model-a.gguf",
                EngineId = "engine-1",
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["--ctx-size"] = "4096",
                    ["--n-gpu-layers"] = "99",
                    ["--split-mode"] = "layer",       // unsupported by this build
                    ["--flash-attn"] = "true",        // boolean -> bare flag
                    ["--mlock"] = "false",            // boolean off -> dropped entirely
                    ["--port"] = "9999",              // managed -> ignored
                },
            };

            var engine = new EngineDefinition
            {
                Id = "engine-1",
                Name = "llama.cpp",
                ExecutablePath = @"C:\llama\llama-server.exe",
                AdapterKind = BackendAdapterKinds.LlamaCpp,
            };

            var plan = adapter.BuildLaunchPlan(new LaunchRequest
            {
                Model = model,
                Engine = engine,
                Parameters = model.Parameters,
                Port = 34567,
                BindHost = ApiSettings.Loopback,
                Capabilities = capabilities,
            });

            var args = plan.Arguments.ToList();
            Assert.Equal(@"C:\llama\llama-server.exe", plan.ExecutablePath, "the executable comes from the engine");
            Assert.Equal(34567, plan.Port, "the internal port is honoured");
            Assert.True(args.Contains("--model"), "the model flag is present");
            Assert.True(args.Contains(@"C:\models\model-a.gguf"), "the model path is passed through");
            Assert.True(args.Contains("--host") && args.Contains(ApiSettings.Loopback), "the engine binds to loopback");
            Assert.True(args.Contains("--alias") && args.Contains("model-a"), "the api alias matches the model id");
            Assert.True(args.Contains("--no-webui"), "unsupported-by-nobody flags are passed when advertised");
            Assert.True(args.Contains("--flash-attn"), "boolean true becomes a bare flag");
            Assert.False(args.Contains("--mlock"), "boolean false is dropped");
            Assert.False(args.Contains("--split-mode"), "unsupported flags are not passed");
            Assert.False(args.Contains("9999"), "managed flags cannot be overridden by a model");
            Assert.True(plan.Warnings.Any(w => w.Contains("--split-mode")), "dropped flags are reported");
            Assert.True(plan.Warnings.Any(w => w.Contains("--port")), "managed overrides are reported");
        });

        TestFramework.Case("models: registry validates, persists and never deletes files", () =>
        {
            using var workspace = TestWorkspace.Create("registry");
            var registry = new ModelRegistry(AppPaths.ModelsFile(workspace.ConfigDirectory));

            var modelFile = workspace.CreateModelFile("alpha");
            var model = new ModelDefinition
            {
                Id = "alpha",
                DisplayName = "Alpha",
                FilePath = modelFile,
                EngineId = "llamacpp-default",
            };

            registry.Add(model);
            Assert.Equal(1, registry.Count, "the model is registered");
            Assert.NotNull(registry.Get("ALPHA"), "lookups are case insensitive");
            Assert.True(registry.Get("alpha")!.AutoLoad == false, "auto-load stays off");

            var duplicate = new ModelDefinition { Id = "alpha", FilePath = modelFile, EngineId = "e" };
            var duplicateErrors = registry.Validate(duplicate);
            Assert.True(duplicateErrors.Any(e => e.Contains("already registered")), "duplicate ids are rejected");

            var missing = new ModelDefinition { Id = "beta", FilePath = Path.Combine(workspace.Root, "nope.gguf"), EngineId = "e" };
            Assert.True(registry.Validate(missing).Count > 0, "missing files are rejected");

            registry.Rename("alpha", "Alpha Renamed", "alpha-2");
            Assert.Equal("alpha-2", registry.Get("alpha-2")!.Id, "renaming changes the id");
            Assert.Equal("Alpha Renamed", registry.Get("alpha-2")!.DisplayName, "renaming changes the display name");

            Assert.True(registry.Remove("alpha-2"), "removal succeeds");
            Assert.Equal(0, registry.Count, "the catalog is empty again");
            Assert.True(File.Exists(modelFile), "removing a model entry must NOT delete the weights file");

            var reloaded = new ModelRegistry(AppPaths.ModelsFile(workspace.ConfigDirectory));
            Assert.Equal(0, reloaded.Count, "the removal is persisted");
        });

        TestFramework.Case("models: corrupt configuration is quarantined instead of crashing", () =>
        {
            using var workspace = TestWorkspace.Create("corrupt");
            var path = AppPaths.ModelsFile(workspace.ConfigDirectory);
            File.WriteAllText(path, "{ this is not json ");

            var registry = new ModelRegistry(path);
            Assert.Equal(0, registry.Count, "a corrupt catalog yields an empty one");
            Assert.True(
                Directory.GetFiles(workspace.ConfigDirectory, "*.corrupt-*").Length == 1,
                "the corrupt file is preserved for inspection");
        });

        TestFramework.Case("ports: allocation is unique and released correctly", () =>
        {
            var allocator = new PortAllocator(35900, 35910);
            var ports = new List<int>();
            for (var i = 0; i < 5; i++)
            {
                var port = allocator.ReserveNext();
                Assert.True(port >= 35900 && port <= 35910, "ports come from the configured range");
                ports.Add(port);
            }

            Assert.Equal(ports.Count, ports.Distinct().Count(), "every reservation is unique");
            Assert.Equal(5, allocator.ReservedCount, "reservations are tracked");

            allocator.Release(ports[0]);
            Assert.False(allocator.IsReserved(ports[0]), "released ports are free again");

            var restored = allocator.ReserveNext();
            Assert.Equal(ports[0], restored, "the freed port is handed out again");
        });

        TestFramework.Case("engine discovery: finds a llama-server style executable on disk", () =>
        {
            using var workspace = TestWorkspace.Create("discovery");
            var enginesDir = Path.Combine(workspace.ConfigDirectory, "engines", "b6379");
            var fake = TestWorkspace.CopyMockEngineTo(enginesDir);

            var discovered = EngineDiscovery.Discover(workspace.ConfigDirectory, additionalRoot: null);
            var match = discovered.FirstOrDefault(d => string.Equals(d.Path, fake, StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(match, "the executable is discovered");
            Assert.Equal(BackendAdapterKinds.LlamaCpp, match!.AdapterKind, "llama-server maps to the llama.cpp adapter");

            var definition = EngineDiscovery.ToEngineDefinition(match);
            Assert.True(definition.ExecutableExists(), "the discovered engine is usable");
            Assert.True(definition.Id.Contains("b6379", StringComparison.OrdinalIgnoreCase), "the id reflects the build folder");
        });

        TestFramework.Case("startup: the Run key entry never preloads models and is written when permitted", () =>
        {
            // The startup command line is environment independent and must never
            // contain a switch that loads models at boot.
            var minimized = StartupRegistration.BuildStartupArguments(startMinimized: true);
            var normal = StartupRegistration.BuildStartupArguments(startMinimized: false);
            Assert.Contains(minimized, "--startup", "the startup switch is recorded");
            Assert.Contains(minimized, "--minimized", "the minimized switch is recorded");
            Assert.Equal("--startup", normal, "start minimized is optional");
            Assert.NotContains(minimized, "--load", "the startup command must never preload models");
            Assert.NotContains(minimized, "model", "the startup command must not reference any model");

            const string testKey = @"Software\LocalAIModelManager\Tests\Run";
            const string valueName = "LocalAIModelManagerTest";

            Assert.False(StartupRegistration.IsEnabled(valueName, testKey), "nothing is registered initially");

            if (!StartupRegistration.IsWritable(testKey))
            {
                // Documented environment limitation: the Run key cannot be written
                // here. The non-throwing path must still report the problem cleanly.
                var ok = StartupRegistration.TrySetEnabled(
                    true, @"C:\app\LocalAIModelManager.exe", minimized, out var error, valueName, testKey);
                Assert.False(ok, "the write is reported as failed rather than throwing");
                Assert.NotNull(error, "a human readable error is produced for the UI");

                throw new TestSkippedException(
                    "HKEY_CURRENT_USER writes are denied in this sandbox, so the Run key write path could not be exercised");
            }

            try
            {
                StartupRegistration.SetEnabled(true, @"C:\Path With Spaces\LocalAIModelManager.exe", minimized, valueName, testKey);
                Assert.True(StartupRegistration.IsEnabled(valueName, testKey), "the entry is registered");

                var command = StartupRegistration.GetCommand(valueName, testKey);
                Assert.NotNull(command, "the command is readable");
                Assert.Contains(command!, "\"C:\\Path With Spaces\\LocalAIModelManager.exe\"", "paths with spaces are quoted");
                Assert.Contains(command!, "--startup", "the startup switch is recorded");
                Assert.Contains(command!, "--minimized", "the minimized switch is recorded");
                Assert.NotContains(command!, "--load", "the startup command must never preload models");

                StartupRegistration.SetEnabled(false, "x", string.Empty, valueName, testKey);
                Assert.False(StartupRegistration.IsEnabled(valueName, testKey), "the entry is removed again");
            }
            finally
            {
                StartupRegistration.SetEnabled(false, "x", string.Empty, valueName, testKey);
            }
        });

        TestFramework.Case("settings: unknown keys survive a round trip and defaults fill the gaps", () =>
        {
            using var workspace = TestWorkspace.Create("settings-store");
            var path = AppPaths.SettingsFile(workspace.ConfigDirectory);

            File.WriteAllText(path, """
                {
                  "api": { "port": 18080, "host": "127.0.0.1", "apiKey": "sk-lamm-custom" },
                  "lifecycle": { "idleTimeoutSeconds": 42, "maxLoadedModels": 3 },
                  "unknownSection": { "value": 1 }
                }
                """);

            var service = new SettingsService(workspace.ConfigDirectory);
            Assert.Equal(18080, service.Current.Api.Port, "existing values are preserved");
            Assert.Equal(42, service.Current.Lifecycle.IdleTimeoutSeconds, "lifecycle values are preserved");
            Assert.Equal(3, service.Current.Lifecycle.MaxLoadedModels, "numeric values are preserved");
            Assert.Equal("sk-lamm-custom", service.Current.Api.ApiKey, "custom keys are preserved");
            Assert.Equal(ApiSettings.Loopback, service.Current.Api.Host, "missing host falls back to loopback");
            Assert.Equal(2000, service.Current.Advanced.LogBufferSize, "missing sections get defaults");

            service.Update(s => s.Api.Port = 19090);
            var reloaded = new SettingsService(workspace.ConfigDirectory);
            Assert.Equal(19090, reloaded.Current.Api.Port, "updates are persisted");

            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            Assert.True(document.RootElement.TryGetProperty("api", out _), "settings are written as json");
        });
    }
}
