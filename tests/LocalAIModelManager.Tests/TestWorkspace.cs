using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LocalAIModelManager.Core.Backends;
using LocalAIModelManager.Core.Configuration;
using LocalAIModelManager.Core.Models;
using LocalAIModelManager.Core.Runtime;

namespace LocalAIModelManager.Tests;

/// <summary>Throwaway sandbox: its own config directory, model files and ports.</summary>
internal sealed class TestWorkspace : IDisposable
{
    private TestWorkspace(string root, string name)
    {
        Root = root;
        Name = name;
        ConfigDirectory = Path.Combine(root, "config");
        ModelsDirectory = Path.Combine(root, "models");
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(ModelsDirectory);
    }

    public string Name { get; }

    public string Root { get; }

    public string ConfigDirectory { get; }

    public string ModelsDirectory { get; }

    private const string MockExecutableName = "LocalAIModelManager.MockEngine.exe";

    private static readonly Lazy<string> MockEnginePathLazy = new(ResolveMockEnginePath);

    public static string MockEnginePath => MockEnginePathLazy.Value;

    /// <summary>
    /// Locates the mock engine, preferring the copy next to the test binary and
    /// falling back to the source tree output (handy when running the test project
    /// on its own instead of through the solution build).
    /// </summary>
    private static string ResolveMockEnginePath()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("LAMM_MOCK_ENGINE");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment!;
        }

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "mockengine", MockExecutableName),
            Path.Combine(AppContext.BaseDirectory, MockExecutableName),
        };

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var sourceOutput = Path.Combine(
                directory.FullName,
                "src", "LocalAIModelManager.MockEngine", "bin", "Debug", "net10.0-windows", MockExecutableName);
            candidates.Add(sourceOutput);

            var sourceOutputRelease = Path.Combine(
                directory.FullName,
                "src", "LocalAIModelManager.MockEngine", "bin", "Release", "net10.0-windows", MockExecutableName);
            candidates.Add(sourceOutputRelease);

            directory = directory.Parent;
        }

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    public static TestWorkspace Create(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "lamm-tests", $"{name}-{Guid.NewGuid():N}"[..(name.Length + 9)]);
        Directory.CreateDirectory(root);
        return new TestWorkspace(root, name);
    }

    public static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static HttpClient CreateHttpClient(TimeSpan? timeout = null) =>
        LoopbackHttp.Create(timeout ?? TimeSpan.FromSeconds(60));

    /// <summary>
    /// Copies the complete mock engine (apphost, managed dll and runtimeconfig) into
    /// a directory, simulating an installed inference engine build.
    /// </summary>
    public static string CopyMockEngineTo(string directory, string executableName = "llama-server.exe")
    {
        Directory.CreateDirectory(directory);
        var sourceDirectory = Path.GetDirectoryName(MockEnginePath)!;

        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("LocalAIModelManager.MockEngine", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(file, Path.Combine(directory, name), overwrite: true);
            }
        }

        var target = Path.Combine(directory, executableName);
        File.Copy(MockEnginePath, target, overwrite: true);
        return target;
    }

    /// <summary>Creates a real file on disk; the registry refuses paths that do not exist.</summary>
    public string CreateModelFile(string name, int sizeBytes = 8192)
    {
        var path = Path.Combine(ModelsDirectory, name + ".gguf");
        var payload = new byte[sizeBytes];
        Encoding.ASCII.GetBytes("GGUF-mock-weights").CopyTo(payload, 0);
        File.WriteAllBytes(path, payload);
        return path;
    }

    public ModelDefinition CreateModel(string id, string engineId = "llamacpp-default", Dictionary<string, string>? parameters = null)
    {
        var model = new ModelDefinition
        {
            Id = id,
            DisplayName = id.ToUpperInvariant(),
            FilePath = CreateModelFile(id),
            EngineId = engineId,
            Parameters = parameters ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };

        model.Normalize();
        return model;
    }

    /// <summary>Builds a runtime wired to the offline mock engine.</summary>
    public AppRuntime CreateRuntime(
        Action<AppSettings>? configure = null,
        int gatewayPort = 0,
        string engineId = "llamacpp-default",
        bool startGateway = true,
        TimeSpan? idleSweep = null,
        string engineAdapterKind = BackendAdapterKinds.LlamaCpp)
    {
        var port = gatewayPort == 0 ? FreeTcpPort() : gatewayPort;
        GatewayPort = port;

        return AppRuntime.Create(new AppRuntimeOptions
        {
            ConfigDirectory = ConfigDirectory,
            StartGateway = startGateway,
            StartResourceMonitor = false,
            StartIdleMonitor = true,
            SeedDefaultEngines = false,
            SeedEngineExecutable = MockEnginePath,
            SeedEngineId = engineId,
            SeedEngineName = "Mock llama-server",
            SeedEngineAdapterKind = engineAdapterKind,
            OverrideHost = "127.0.0.1",
            OverridePort = port,
            IdleSweepInterval = idleSweep ?? TimeSpan.FromMilliseconds(300),
            ConfigureSettings = settings =>
            {
                settings.Api.Port = port;
                settings.Api.Host = ApiSettings.Loopback;
                settings.Api.ApiKeyEnabled = true;
                settings.Api.ApiKey = "sk-lamm-test-key-000000000000";
                settings.Api.RequestTimeoutSeconds = 60;
                settings.Network.InternalPortRangeStart = 34300;
                settings.Network.InternalPortRangeEnd = 34400;
                settings.General.ProbeEnginesOnStartup = false;
                settings.Resources.MonitorEnabled = false;
                settings.Lifecycle.IdleTimeoutSeconds = 300;
                settings.Lifecycle.MaxLoadedModels = 2;
                settings.Lifecycle.ShutdownGraceSeconds = 2;
                settings.Advanced.LogLevel = "Debug";
                configure?.Invoke(settings);
            },
        });
    }

    public int GatewayPort { get; private set; }

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonDocument.Parse(text);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
