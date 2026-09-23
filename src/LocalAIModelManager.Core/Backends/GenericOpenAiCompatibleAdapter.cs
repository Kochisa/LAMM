using LocalAIModelManager.Core.Models;
using LocalAIModelManager.Core.Processes;

namespace LocalAIModelManager.Core.Backends;

/// <summary>
/// Placeholder adapter for a generic OpenAI-compatible local server (vLLM,
/// LM Studio, text-generation-webui, a remote box, ...). It proves the seam is
/// real: only this class would need to grow to support such a backend, while the
/// lifecycle manager, gateway and UI stay untouched.
/// </summary>
public sealed class GenericOpenAiCompatibleAdapter : OpenAiCompatibleBackendAdapter
{
    private readonly Dictionary<string, EngineCapabilities> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public GenericOpenAiCompatibleAdapter(BackendJobObject? job = null)
        : base(job)
    {
    }

    public override string Kind => BackendAdapterKinds.OpenAiCompatible;

    public override string DisplayName => "Generic OpenAI-compatible server";

    public override string DefaultExecutableFileName => "server.exe";

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

        EngineCapabilities capabilities;
        if (string.IsNullOrWhiteSpace(engine.ExecutablePath))
        {
            capabilities = EngineCapabilities.Unavailable(engine.Id, engine.ExecutablePath, "No executable configured.");
        }
        else if (!File.Exists(engine.ExecutablePath))
        {
            capabilities = EngineCapabilities.Unavailable(engine.Id, engine.ExecutablePath, $"Executable not found: {engine.ExecutablePath}");
        }
        else
        {
            // Unknown CLIs are probed with --help, but their flags are treated as
            // free-form: everything detected is offered, nothing is assumed.
            var help = EngineProcessRunner.RunAsync(engine.ExecutablePath, new[] { "--help" }, TimeSpan.FromSeconds(15))
                .GetAwaiter().GetResult();

            capabilities = string.IsNullOrWhiteSpace(help.CombinedOutput)
                ? new EngineCapabilities
                {
                    EngineId = engine.Id,
                    ExecutablePath = engine.ExecutablePath,
                    ExecutableFingerprint = fingerprint,
                    IsAvailable = true,
                    Version = null,
                    Parameters = Array.Empty<ParameterDescriptor>(),
                    SupportedFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    DetectedFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                }
                : LlamaHelpParser.Parse(engine.Id, engine.ExecutablePath, help.CombinedOutput, fingerprint);

            // Whether GPU offload will actually happen is decided by the engine's own
            // view of its compute devices, so ask it directly.
            var devices = ProbeDevices(engine);
            capabilities = capabilities with
            {
                HasGpuDevice = devices.HasGpuDevice,
                AvailableDevices = devices.Devices,
            };
        }

        lock (_gate)
        {
            _cache[cacheKey] = capabilities;
        }

        return capabilities;
    }

    public override LaunchPlan BuildLaunchPlan(LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var capabilities = request.Capabilities ?? Inspect(request.Engine);
        var warnings = new List<string>();
        var arguments = new List<string>
        {
            "--model", request.Model.FilePath,
            "--host", request.BindHost,
            "--port", request.Port.ToString(),
        };

        foreach (var (key, value) in request.Parameters)
        {
            if (ParameterCatalog.ManagedFlags.Contains(key))
            {
                warnings.Add($"Parameter '{key}' is managed by the manager and was ignored.");
                continue;
            }

            if (capabilities.IsAvailable && !capabilities.Supports(key) && !capabilities.DetectedFlags.Contains(key))
            {
                warnings.Add($"Parameter '{key}' is not supported by this engine build and was dropped.");
                continue;
            }

            if (string.IsNullOrEmpty(value))
            {
                arguments.Add(key);
            }
            else
            {
                arguments.Add(key);
                arguments.Add(value);
            }
        }

        foreach (var (key, value) in request.Engine.ExtraArguments)
        {
            arguments.Add(key);
            if (value.Length > 0)
            {
                arguments.Add(value);
            }
        }

        // "Other" in the UI: raw CLI lines the user typed, passed through verbatim.
        var (extraTokens, extraWarnings) = AdditionalArguments.Tokenize(request.AdditionalArguments);
        warnings.AddRange(extraWarnings);
        arguments.AddRange(extraTokens);

        return new LaunchPlan
        {
            ExecutablePath = request.Engine.ExecutablePath,
            WorkingDirectory = string.IsNullOrWhiteSpace(request.Engine.WorkingDirectory)
                ? Path.GetDirectoryName(request.Engine.ExecutablePath) ?? Environment.CurrentDirectory
                : request.Engine.WorkingDirectory!,
            Arguments = arguments,
            BindHost = request.BindHost,
            Port = request.Port,
            HealthPath = request.Engine.HealthPath,
            ModelId = request.Model.Id,
            EngineId = request.Engine.Id,
            Warnings = warnings,
        };
    }
}
