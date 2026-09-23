using LocalAIModelManager.Core.Backends;
using LocalAIModelManager.Core.Configuration;
using LocalAIModelManager.Core.Gateway;
using LocalAIModelManager.Core.Lifecycle;
using LocalAIModelManager.Core.Localization;
using LocalAIModelManager.Core.Logging;
using LocalAIModelManager.Core.Models;
using LocalAIModelManager.Core.Processes;
using LocalAIModelManager.Core.Resources;
using LocalAIModelManager.Core.Runtime;

namespace LocalAIModelManager.Core.Runtime;

public sealed record AppRuntimeOptions
{
    /// <summary>Overrides the configuration directory (tests use a temp folder).</summary>
    public string? ConfigDirectory { get; init; }

    public bool StartGateway { get; init; } = true;

    public bool StartResourceMonitor { get; init; } = true;

    public bool StartIdleMonitor { get; init; } = true;

    public bool SeedDefaultEngines { get; init; } = true;

    /// <summary>Forces the gateway host, ignoring settings (test harness).</summary>
    public string? OverrideHost { get; init; }

    public int? OverridePort { get; init; }

    /// <summary>
    /// Registers this executable as the default engine when no engine is configured.
    /// Used to point the whole lifecycle at the offline mock engine.
    /// </summary>
    public string? SeedEngineExecutable { get; init; }

    public string SeedEngineId { get; init; } = "llamacpp-default";

    public string SeedEngineName { get; init; } = "llama.cpp (llama-server)";

    public string SeedEngineAdapterKind { get; init; } = BackendAdapterKinds.LlamaCpp;

    public TimeSpan IdleSweepInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Disposable helper used to deliver CTRL_BREAK to an engine's console without
    /// ever attaching the manager itself. Defaults to the copy that ships next to
    /// the manager executable.
    /// </summary>
    public string? ControlHelperPath { get; init; }

    /// <summary>
    /// Applied to the persisted settings before any service is constructed, so the
    /// gateway and port allocator pick the configured values up. Test harness hook.
    /// </summary>
    public Action<AppSettings>? ConfigureSettings { get; init; }
}

/// <summary>
/// Composition root: builds every core service and owns their lifetime.
/// The UI and the tests both consume this one object - neither of them ever
/// touches a backend adapter or a child process directly.
/// </summary>
public sealed class AppRuntime : IAsyncDisposable
{
    private readonly AppRuntimeOptions _options;
    private bool _started;
    private bool _disposed;

    private AppRuntime(AppRuntimeOptions options, string configDirectory)
    {
        _options = options;
        ConfigDirectory = configDirectory;

        Logs = new InMemoryLogStore();
        Settings = new SettingsService(configDirectory);
        Models = new ModelRegistry(AppPaths.ModelsFile(configDirectory));
        JobObject = new BackendJobObject();
        Adapters = BackendAdapterRegistry.CreateDefault(JobObject);
        Processes = new ProcessInventory();
        Ports = new PortAllocator(
            Settings.Current.Network.InternalPortRangeStart,
            Settings.Current.Network.InternalPortRangeEnd);

        var resourceSettings = Settings.Current.Resources;
        Resources = new SystemResourceMonitor(
            Logs,
            TimeSpan.FromMilliseconds(resourceSettings.PollIntervalMs),
            resourceSettings.GpuIndex,
            resourceSettings.TrackPerProcessVram);

        Lifecycle = new ModelLifecycleManager(
            Settings,
            Models,
            Adapters,
            Processes,
            Ports,
            Resources,
            Logs,
            options.IdleSweepInterval,
            options.ControlHelperPath ?? DefaultControlHelperPath());

        Gateway = new GatewayHost(
            BuildGatewayOptions(),
            Lifecycle,
            Models,
            Logs,
            Logs.Redactor,
            GetStatus);

        ApplySettingsToLogging();

        Settings.Changed += _ =>
        {
            ApplySettingsToLogging();
        };
    }

    public string ConfigDirectory { get; }

    public InMemoryLogStore Logs { get; }

    public SettingsService Settings { get; }

    public ModelRegistry Models { get; }

    public BackendJobObject JobObject { get; }

    public BackendAdapterRegistry Adapters { get; }

    public ProcessInventory Processes { get; }

    public PortAllocator Ports { get; }

    public IResourceMonitor Resources { get; }

    public IModelLifecycleManager Lifecycle { get; }

    public GatewayHost Gateway { get; private set; }

    public bool IsStarted => _started;

    /// <summary>
    /// Rebuilds the gateway from the current settings and restarts it. Used after
    /// API settings change, because a listener cannot change host/port in place.
    /// No model is loaded or unloaded by this operation.
    /// </summary>
    public async Task RestartGatewayAsync(CancellationToken cancellationToken = default)
    {
        var wasRunning = Gateway.IsRunning;
        await Gateway.StopAsync(cancellationToken).ConfigureAwait(false);
        await Gateway.DisposeAsync().ConfigureAwait(false);

        Gateway = new GatewayHost(
            BuildGatewayOptions(),
            Lifecycle,
            Models,
            Logs,
            Logs.Redactor,
            GetStatus);

        if (wasRunning || _options.StartGateway)
        {
            await Gateway.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        Logs.Info("runtime", Loc.T("log.runtime.gatewayRestarted", Gateway.BaseUrl));
    }

    public static AppRuntime Create(AppRuntimeOptions? options = null)
    {
        options ??= new AppRuntimeOptions();
        var configDirectory = options.ConfigDirectory ?? AppPaths.ResolveConfigDirectory();
        Directory.CreateDirectory(configDirectory);

        if (options.ConfigureSettings is not null)
        {
            // Persisted first so every service constructed below sees the values.
            new SettingsService(configDirectory).Update(options.ConfigureSettings);
        }

        var runtime = new AppRuntime(options, configDirectory);
        runtime.SeedEnginesIfNeeded();
        runtime.Settings.Current.Normalize();
        return runtime;
    }

    /// <summary>
    /// Starts the manager only: the gateway and the monitors come up, and every
    /// configured model stays in standby. No weights are read until a request arrives.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        var settings = Settings.Current;

        Logs.Info("runtime", Loc.T("log.runtime.starting", ConfigDirectory));
        Logs.Info("runtime", Loc.T("log.runtime.modelsRegistered", Models.Count));

        foreach (var issue in settings.Validate())
        {
            switch (issue.Severity)
            {
                case ValidationSeverity.Error:
                    Logs.Error("config", issue.ToString());
                    break;
                case ValidationSeverity.Warning:
                    Logs.Warn("config", issue.ToString());
                    break;
                default:
                    Logs.Info("config", issue.ToString());
                    break;
            }
        }

        if (!JobObject.IsAvailable)
        {
            Logs.Warn("runtime", Loc.T("log.runtime.jobUnavailable", JobObject.LastError));
        }

        if (settings.Resources.MonitorEnabled && _options.StartResourceMonitor)
        {
            await Resources.StartAsync(cancellationToken).ConfigureAwait(false);
            await Resources.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        if (settings.General.ProbeEnginesOnStartup)
        {
            // Probing only runs --help; it never loads a model.
            _ = Task.Run(() => ProbeAllEngines(forceRefresh: false, CancellationToken.None), CancellationToken.None);
        }

        if (_options.StartGateway)
        {
            await Gateway.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_options.StartIdleMonitor)
        {
            await Lifecycle.StartIdleMonitorAsync(cancellationToken).ConfigureAwait(false);
        }

        Logs.Info("runtime", Loc.T("log.runtime.ready"));
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Logs.Info("runtime", Loc.T("log.runtime.shutdown"));

        await Lifecycle.UnloadAllAsync(cancellationToken).ConfigureAwait(false);
        await Lifecycle.DisposeAsync().ConfigureAwait(false);
        await Gateway.StopAsync(cancellationToken).ConfigureAwait(false);
        await Processes.StopAllAsync(TimeSpan.FromSeconds(Settings.Current.Lifecycle.ShutdownGraceSeconds), cancellationToken)
            .ConfigureAwait(false);
        await Resources.StopAsync().ConfigureAwait(false);

        // Final safety net: kill anything still inside the job object.
        JobObject.TerminateAll();
        _started = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logs.Warn("runtime", Loc.T("log.runtime.shutdownFailed"), ex);
        }

        await Gateway.DisposeAsync().ConfigureAwait(false);
        await Resources.DisposeAsync().ConfigureAwait(false);
        JobObject.Dispose();
    }

    public RuntimeStatusSnapshot GetStatus()
    {
        var settings = Settings.Current;
        var statuses = Lifecycle.GetStatuses();
        var engines = BuildEngineStatuses(settings);

        return new RuntimeStatusSnapshot
        {
            Gateway = new GatewayStatusInfo
            {
                IsRunning = Gateway.IsRunning,
                Host = Gateway.Options.Host,
                Port = Gateway.Options.Port,
                BaseUrl = Gateway.BaseUrl,
                StartedAtUtc = Gateway.StartedAtUtc,
                TotalRequests = Gateway.TotalRequests,
                ActiveRequests = Gateway.ActiveRequests,
                ApiKeyEnabled = Gateway.Options.ApiKeyEnabled,
                LanAccessEnabled = Gateway.Options.AllowLanAccess,
                LastError = Gateway.LastError,
            },
            Models = statuses,
            Processes = Processes.Snapshot(),
            ExitedProcesses = Processes.History,
            Engines = engines,
            Resources = Resources.Current,
            ValidationIssues = BuildValidationIssues(settings, engines),
            LoadedModelCount = statuses.Count(s => s.IsLoaded),
            StandbyModelCount = statuses.Count(s => s.State == ModelState.Standby),
            LogEntryCount = Logs.Count,
            ConfigDirectory = ConfigDirectory,
        };
    }

    /// <summary>
    /// Settings validation plus runtime findings. The GPU finding matters most: an
    /// engine that cannot see a GPU while the parameters ask for offload means every
    /// model silently runs on the CPU.
    /// </summary>
    private static IReadOnlyList<ValidationIssue> BuildValidationIssues(
        AppSettings settings,
        IReadOnlyList<EngineStatusInfo> engines)
    {
        var issues = new List<ValidationIssue>(settings.Validate());
        var gpuOffloadRequested = settings.ModelParameters.Defaults.TryGetValue("--n-gpu-layers", out var layers) &&
                                  int.TryParse(layers, out var parsed) &&
                                  parsed > 0;

        foreach (var engine in engines.Where(e => e.HasGpuDevice is false))
        {
            var detail = gpuOffloadRequested
                ? Loc.T("validation.engineNoGpuWithOffload")
                : Loc.T("validation.engineNoGpuCpuOnly");

            issues.Add(new ValidationIssue(
                gpuOffloadRequested ? ValidationSeverity.Warning : ValidationSeverity.Info,
                "Inference Engine",
                Loc.T("validation.engineNoGpu", engine.Name, detail)));
        }

        return issues;
    }

    /// <summary>Re-probes an engine and caches the result (never loads a model).</summary>
    public async Task<EngineCapabilities> InspectEngineAsync(string engineId, bool forceRefresh, CancellationToken cancellationToken = default)
    {
        var engine = Settings.Current.Engines.Engines
            .FirstOrDefault(e => string.Equals(e.Id, engineId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Engine '{engineId}' is not configured.");

        var capabilities = await Task.Run(() => Adapters.Get(engine.AdapterKind).Inspect(engine, forceRefresh), cancellationToken)
            .ConfigureAwait(false);

        Settings.Update(settings =>
        {
            var target = settings.Engines.Engines.FirstOrDefault(e => string.Equals(e.Id, engineId, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                target.DetectedVersion = capabilities.Version;
                target.LastProbeError = capabilities.IsAvailable ? null : capabilities.Error;
                target.LastProbedAtUtc = DateTimeOffset.UtcNow;
            }
        });

        return capabilities;
    }

    public void ProbeAllEngines(bool forceRefresh, CancellationToken cancellationToken)
    {
        foreach (var engine in Settings.Current.Engines.Engines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var capabilities = Adapters.Get(engine.AdapterKind).Inspect(engine, forceRefresh);
                Logs.Info("engine",
                    capabilities.IsAvailable
                        ? Loc.T("log.engine.probeOk", engine.Id, capabilities.Version ?? "unknown", capabilities.Parameters.Count)
                        : Loc.T("log.engine.probeFailed", engine.Id, capabilities.Error));
            }
            catch (Exception ex)
            {
                Logs.Warn("engine", Loc.T("log.engine.probeThrew", engine.Id), ex);
            }
        }
    }

    private IReadOnlyList<EngineStatusInfo> BuildEngineStatuses(AppSettings settings)
    {
        var result = new List<EngineStatusInfo>();
        foreach (var engine in settings.Engines.Engines)
        {
            var adapter = Adapters.TryGet(engine.AdapterKind);
            EngineCapabilities? capabilities = null;
            if (adapter is not null && engine.ExecutableExists())
            {
                try
                {
                    capabilities = adapter.Inspect(engine);
                }
                catch (Exception ex)
                {
                    Logs.Debug("engine", Loc.T("log.engine.capabilityReadFailed", engine.Id, ex.Message));
                }
            }

            result.Add(new EngineStatusInfo
            {
                Id = engine.Id,
                Name = engine.Name,
                AdapterKind = engine.AdapterKind,
                ExecutablePath = engine.ExecutablePath,
                ExecutableExists = engine.ExecutableExists(),
                IsAvailable = capabilities?.IsAvailable ?? false,
                Version = capabilities?.Version ?? engine.DetectedVersion,
                Error = capabilities?.Error ?? engine.LastProbeError,
                LastProbedAtUtc = capabilities?.ProbedAtUtc ?? engine.LastProbedAtUtc,
                SupportedParameterCount = capabilities?.Parameters.Count ?? 0,
                ModelCount = Models.All.Count(m => string.Equals(m.EngineId, engine.Id, StringComparison.OrdinalIgnoreCase)),
                IsSelected = string.Equals(settings.Engines.SelectedEngineId, engine.Id, StringComparison.OrdinalIgnoreCase),
                AvailableDevices = capabilities?.AvailableDevices ?? Array.Empty<string>(),
                HasGpuDevice = capabilities?.HasGpuDevice,
            });
        }

        return result;
    }

    private GatewayOptions BuildGatewayOptions()
    {
        var settings = Settings.Current;
        var options = GatewayOptions.FromSettings(settings);

        if (_options.OverrideHost is not null || _options.OverridePort is not null)
        {
            options = options with
            {
                Host = _options.OverrideHost ?? options.Host,
                Port = _options.OverridePort ?? options.Port,
            };
        }

        return options;
    }

    private void ApplySettingsToLogging()
    {
        var advanced = Settings.Current.Advanced;
        Logs.SetCapacity(advanced.LogBufferSize);
        Logs.MinimumLevel = Enum.TryParse<LogLevel>(advanced.LogLevel, ignoreCase: true, out var level)
            ? level
            : LogLevel.Information;

        Logs.Redactor.RegisterSecret(Settings.Current.Api.ApiKey);
    }

    private static string DefaultControlHelperPath() =>
        Path.Combine(AppContext.BaseDirectory, "LocalAIModelManager.ControlHelper.exe");

    private void SeedEnginesIfNeeded()
    {
        var settings = Settings.Current;
        if (settings.Engines.Engines.Count > 0)
        {
            return;
        }

        var seeded = new List<EngineDefinition>();

        if (!string.IsNullOrWhiteSpace(_options.SeedEngineExecutable))
        {
            seeded.Add(new EngineDefinition
            {
                Id = _options.SeedEngineId,
                Name = _options.SeedEngineName,
                AdapterKind = _options.SeedEngineAdapterKind,
                ExecutablePath = _options.SeedEngineExecutable!,
                Tags = { "seeded" },
            });
        }
        else if (_options.SeedDefaultEngines)
        {
            foreach (var discovered in EngineDiscovery.Discover(ConfigDirectory))
            {
                seeded.Add(EngineDiscovery.ToEngineDefinition(discovered));
            }
        }

        if (seeded.Count == 0)
        {
            return;
        }

        Settings.Update(settings =>
        {
            settings.Engines.Engines = seeded;
            settings.Engines.SelectedEngineId = seeded[0].Id;
        });

        Logs.Info("engine", Loc.T("log.engine.registered", seeded.Count, string.Join(", ", seeded.Select(s => s.Id))));
    }
}
