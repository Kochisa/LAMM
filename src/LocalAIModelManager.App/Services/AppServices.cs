using LocalAIModelManager.Core.Backends;
using LocalAIModelManager.Core.Configuration;
using LocalAIModelManager.Core.Lifecycle;
using LocalAIModelManager.Core.Logging;
using LocalAIModelManager.Core.Models;
using LocalAIModelManager.Core.Runtime;

namespace LocalAIModelManager.App.Services;

/// <summary>
/// The UI facing facade over the core. Pages only ever talk to this object, which in
/// turn only talks to the model manager core - the UI never touches an engine
/// process, an adapter or a command line directly.
/// </summary>
public sealed class AppServices
{
    private readonly Dictionary<string, EngineCapabilities> _capabilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _capabilityGate = new();

    public AppServices(AppRuntime runtime, IDialogService dialogs)
    {
        Runtime = runtime;
        Dialogs = dialogs;
    }

    public AppRuntime Runtime { get; }

    public IDialogService Dialogs { get; }

    public SettingsService Settings => Runtime.Settings;

    public ModelRegistry Models => Runtime.Models;

    public InMemoryLogStore Logs => Runtime.Logs;

    public IModelLifecycleManager Lifecycle => Runtime.Lifecycle;

    public Core.Gateway.GatewayHost Gateway => Runtime.Gateway;

    public string ConfigDirectory => Runtime.ConfigDirectory;

    /// <summary>Raised for transient status bar messages.</summary>
    public event Action<string>? Notification;

    /// <summary>
    /// Raised in addition to <see cref="Notification"/> when the message reports a failed
    /// operation (the tray turns those into balloons). The producer records the severity
    /// here instead of the consumer guessing it from the text: a text based check cannot
    /// survive a language switch.
    /// </summary>
    public event Action<string>? FailureNotification;

    /// <summary>Raised when the gateway must be restarted for settings to take effect.</summary>
    public event Action? GatewayRestartRequired;

    public AppSettings Current => Settings.Current;

    public void Notify(string message) => Notification?.Invoke(message);

    /// <summary>Reports a failed operation: status bar message plus a tray balloon.</summary>
    public void NotifyFailure(string message)
    {
        Notification?.Invoke(message);
        FailureNotification?.Invoke(message);
    }

    public void SaveSettings(Action<AppSettings> mutate)
    {
        Settings.Update(mutate);

        // Keep the log buffer in sync with the advanced settings immediately.
        Runtime.Logs.SetCapacity(Settings.Current.Advanced.LogBufferSize);
        if (Enum.TryParse<LogLevel>(Settings.Current.Advanced.LogLevel, ignoreCase: true, out var level))
        {
            Runtime.Logs.MinimumLevel = level;
        }

        Runtime.Logs.Redactor.RegisterSecret(Settings.Current.Api.ApiKey);
    }

    public IReadOnlyList<EngineDefinition> Engines => Settings.Current.Engines.Engines;

    public EngineDefinition? FindEngine(string? engineId) =>
        string.IsNullOrWhiteSpace(engineId)
            ? null
            : Engines.FirstOrDefault(e => string.Equals(e.Id, engineId, StringComparison.OrdinalIgnoreCase));

    public EngineDefinition? SelectedEngine => FindEngine(Settings.Current.Engines.SelectedEngineId);

    public int ModelCountForEngine(string engineId) =>
        Models.All.Count(m => string.Equals(m.EngineId, engineId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Capability probe (cached). This is what makes llama.cpp versions independently
    /// upgradable: the parameter set always comes from the installed binary.
    /// </summary>
    public EngineCapabilities GetCapabilities(string engineId, bool forceRefresh = false)
    {
        var engine = FindEngine(engineId);
        if (engine is null)
        {
            return EngineCapabilities.Unavailable(engineId, null, Loc.T("services.capabilities.notConfigured"));
        }

        lock (_capabilityGate)
        {
            if (!forceRefresh && _capabilities.TryGetValue(engineId, out var cached))
            {
                return cached;
            }
        }

        var capabilities = Runtime.Adapters.Get(engine.AdapterKind).Inspect(engine, forceRefresh);

        lock (_capabilityGate)
        {
            _capabilities[engineId] = capabilities;
        }

        return capabilities;
    }

    public async Task<EngineCapabilities> RefreshCapabilitiesAsync(string engineId)
    {
        var capabilities = await Task.Run(() => GetCapabilities(engineId, forceRefresh: true)).ConfigureAwait(true);
        if (capabilities.IsAvailable)
        {
            Notify(Loc.T(
                "services.capabilities.updated",
                capabilities.Parameters.Count,
                capabilities.Version ?? Loc.T("common.unknown")));
        }
        else
        {
            NotifyFailure(Loc.T("services.capabilities.probeFailed", capabilities.Error));
        }

        return capabilities;
    }

    /// <summary>
    /// Reads the model file's own metadata and combines it with the current VRAM situation
    /// to derive launch parameters. This is what keeps importing a model from turning into
    /// manual tuning of context size, GPU layers and KV cache.
    /// </summary>
    public async Task<Core.Models.AutoTuneResult> AutoTuneAsync(
        string modelFilePath,
        CancellationToken cancellationToken = default)
    {
        var settings = Settings.Current;
        var snapshot = Runtime.Resources.Current;

        if (settings.Resources.MonitorEnabled)
        {
            try
            {
                snapshot = await Runtime.Resources.RefreshAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Runtime.Logs.Warn("resources", Loc.T("log.resources.probeFailed", ex.Message));
            }
        }

        var gpu = snapshot.PrimaryGpu;
        var input = new Core.Models.AutoTuneInput
        {
            ModelFilePath = modelFilePath,
            GpuAvailable = snapshot.GpuAvailable,
            TotalVramBytes = gpu?.TotalBytes,
            FreeVramBytes = gpu?.FreeBytes,
            MinFreeVramMiB = settings.Lifecycle.MinFreeVramMiB,
            MaxVramUsagePercent = settings.Resources.MaxVramUsagePercent,
            TargetContextSize = settings.Resources.AutoTuneContextSize,
            FallbackGpuLayers = settings.ModelParameters.Defaults.TryGetValue("--n-gpu-layers", out var layers) &&
                                int.TryParse(layers, out var parsedLayers) && parsedLayers > 0
                ? parsedLayers
                : 99,
            FallbackContextSize = settings.ModelParameters.Defaults.TryGetValue("--ctx-size", out var ctx) &&
                                  int.TryParse(ctx, out var parsedCtx) && parsedCtx > 0
                ? parsedCtx
                : 8192,
        };

        var result = await Task.Run(() => Core.Models.ModelAutoTuner.Tune(input), cancellationToken).ConfigureAwait(true);

        // The separator between the individual notes stays punctuation, exactly like the
        // other list joins in this code base; only the words go through the catalog.
        var details = string.Join("；", result.Explanation);
        if (result.Warnings.Count > 0)
        {
            details += " | " + Loc.T("models.test.warnings") + string.Join("；", result.Warnings);
        }

        Runtime.Logs.Info("autotune", Loc.T("log.lifecycle.tuned", Path.GetFileName(modelFilePath), details));

        return result;
    }

    public void InvalidateCapabilities(string? engineId = null)
    {
        lock (_capabilityGate)
        {
            if (engineId is null)
            {
                _capabilities.Clear();
            }
            else
            {
                _capabilities.Remove(engineId);
            }
        }
    }

    public void RequestGatewayRestart() => GatewayRestartRequired?.Invoke();

    public RuntimeStatusSnapshot GetStatus() => Runtime.GetStatus();

    public static string DescribeModelState(ModelState state) => Infrastructure.Labels.ModelState(state);
}
