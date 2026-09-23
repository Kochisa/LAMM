using LocalAIModelManager.Core.Configuration;
using LocalAIModelManager.Core.Models;
using LocalAIModelManager.Core.Processes;

namespace LocalAIModelManager.Core.Backends;

/// <summary>
/// llama.cpp adapter. It drives <c>llama-server</c> exactly as any other
/// OpenAI-compatible engine: the only llama.cpp specific knowledge in the whole
/// code base lives in this class and in <see cref="ParameterCatalog"/>.
/// </summary>
public sealed class LlamaCppBackendAdapter : OpenAiCompatibleBackendAdapter
{
    private static readonly TimeSpan HelpProbeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(8);

    private readonly Dictionary<string, EngineCapabilities> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public LlamaCppBackendAdapter(BackendJobObject? job = null)
        : base(job)
    {
    }

    public override string Kind => BackendAdapterKinds.LlamaCpp;

    public override string DisplayName => "llama.cpp (llama-server)";

    public override string DefaultExecutableFileName => "llama-server.exe";

    /// <summary>Absolute path of the managed flags the adapter supplies itself.</summary>
    public static IReadOnlySet<string> ManagedFlags => ParameterCatalog.ManagedFlags;

    public override EngineCapabilities Inspect(EngineDefinition engine, bool forceRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var cacheKey = engine.Id + "|" + engine.ExecutablePath;
        var fingerprint = EngineProcessRunner.Fingerprint(engine.ExecutablePath);

        if (!forceRefresh)
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(cacheKey, out var cached) &&
                    string.Equals(cached.ExecutableFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return cached;
                }
            }
        }

        var capabilities = InspectCore(engine, fingerprint);

        lock (_gate)
        {
            _cache[cacheKey] = capabilities;
        }

        return capabilities;
    }

    private static EngineCapabilities InspectCore(EngineDefinition engine, string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(engine.ExecutablePath))
        {
            return EngineCapabilities.Unavailable(engine.Id, engine.ExecutablePath, "No executable configured.");
        }

        if (!File.Exists(engine.ExecutablePath))
        {
            return EngineCapabilities.Unavailable(engine.Id, engine.ExecutablePath, $"Executable not found: {engine.ExecutablePath}");
        }

        string versionOutput = string.Empty;
        try
        {
            var version = EngineProcessRunner.RunAsync(engine.ExecutablePath, new[] { "--version" }, VersionProbeTimeout)
                .GetAwaiter().GetResult();
            versionOutput = version.CombinedOutput;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return EngineCapabilities.Unavailable(engine.Id, engine.ExecutablePath, ex.Message);
        }

        ProcessRunResult help;
        try
        {
            help = EngineProcessRunner.RunAsync(engine.ExecutablePath, new[] { "--help" }, HelpProbeTimeout)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return EngineCapabilities.Unavailable(engine.Id, engine.ExecutablePath, ex.Message);
        }

        var combined = help.CombinedOutput;
        if (string.IsNullOrWhiteSpace(combined))
        {
            return EngineCapabilities.Unavailable(
                engine.Id,
                engine.ExecutablePath,
                $"'{Path.GetFileName(engine.ExecutablePath)} --help' produced no output (exit code {help.ExitCode}).");
        }

        var capabilities = LlamaHelpParser.Parse(engine.Id, engine.ExecutablePath, combined, fingerprint);

        if (capabilities.Version is null && !string.IsNullOrWhiteSpace(versionOutput))
        {
            capabilities = capabilities with { Version = LlamaHelpParser.ExtractVersion(versionOutput) };
        }

        // Whether GPU offload actually happens is decided by the engine's own view of
        // its compute devices. A CUDA build with missing runtime DLLs reports none and
        // silently falls back to the CPU, which is exactly what the user must be told.
        var devices = ProbeDevices(engine);
        capabilities = capabilities with
        {
            HasGpuDevice = devices.HasGpuDevice,
            AvailableDevices = devices.Devices,
        };

        return capabilities;
    }

    public override LaunchPlan BuildLaunchPlan(LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = request.Model;
        var engine = request.Engine;
        var capabilities = request.Capabilities ?? Inspect(engine);

        var warnings = new List<string>();
        var arguments = new List<string>
        {
            "--model", model.FilePath,
            "--host", request.BindHost,
            "--port", request.Port.ToString(),
        };

        // The alias keeps the engine's own /v1/models honest about which id it serves.
        if (capabilities.Supports("--alias"))
        {
            arguments.Add("--alias");
            arguments.Add(model.Id);
        }

        if (capabilities.Supports("--no-webui"))
        {
            arguments.Add("--no-webui");
        }

        var orderedKeys = ParameterCatalog.Known
            .Select(k => k.Key)
            .Concat(request.Parameters.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var key in orderedKeys)
        {
            if (!request.Parameters.TryGetValue(key, out var value))
            {
                continue;
            }

            if (ParameterCatalog.ManagedFlags.Contains(key))
            {
                warnings.Add($"Parameter '{key}' is managed by the manager and was ignored.");
                continue;
            }

            if (!IsSupported(capabilities, key))
            {
                warnings.Add($"Parameter '{key}' is not supported by this engine build and was dropped.");
                continue;
            }

            if (!ParameterRenderer.TryRender(key, value, capabilities, out var rendered))
            {
                continue;
            }

            arguments.AddRange(rendered);
        }

        foreach (var (key, value) in engine.ExtraArguments)
        {
            if (!IsSupported(capabilities, key))
            {
                warnings.Add($"Engine extra argument '{key}' is not supported by this build and was still passed through.");
            }

            if (value.Length == 0)
            {
                arguments.Add(key);
            }
            else
            {
                arguments.Add(key);
                arguments.Add(value);
            }
        }

        // "Other" in the UI: raw CLI lines the user typed. Passed through verbatim and
        // last, so they win over anything the catalog produced.
        var (extraTokens, extraWarnings) = AdditionalArguments.Tokenize(request.AdditionalArguments);
        warnings.AddRange(extraWarnings);
        arguments.AddRange(extraTokens);

        var workingDirectory = string.IsNullOrWhiteSpace(engine.WorkingDirectory)
            ? Path.GetDirectoryName(engine.ExecutablePath) ?? Environment.CurrentDirectory
            : engine.WorkingDirectory!;

        return new LaunchPlan
        {
            ExecutablePath = engine.ExecutablePath,
            WorkingDirectory = workingDirectory,
            Arguments = arguments,
            BindHost = request.BindHost,
            Port = request.Port,
            HealthPath = engine.HealthPath,
            ModelId = model.Id,
            EngineId = engine.Id,
            Warnings = warnings,
        };
    }

    public override IReadOnlyList<string> ValidateLaunch(LaunchPlan plan, EngineCapabilities capabilities)
    {
        var problems = new List<string>(base.ValidateLaunch(plan, capabilities));

        var modelIndex = plan.Arguments.ToList().FindIndex(a => string.Equals(a, "--model", StringComparison.OrdinalIgnoreCase));
        if (modelIndex < 0 || modelIndex + 1 >= plan.Arguments.Count)
        {
            problems.Add("The launch plan does not reference a model file.");
        }
        else
        {
            var modelPath = plan.Arguments[modelIndex + 1];
            if (!File.Exists(modelPath))
            {
                problems.Add($"Model file not found: {modelPath}");
            }
        }

        return problems;
    }

    private static bool IsSupported(EngineCapabilities capabilities, string flag) =>
        !capabilities.IsAvailable || capabilities.Supports(flag) || capabilities.DetectedFlags.Contains(flag);
}
