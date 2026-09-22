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

    /// <summary>Raised when the gateway must be restarted for settings to take effect.</summary>
    public event Action? GatewayRestartRequired;

    public AppSettings Current => Settings.Current;

    public void Notify(string message) => Notification?.Invoke(message);

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
            return EngineCapabilities.Unavailable(engineId, null, "引擎未配置");
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
        Notify(capabilities.IsAvailable
            ? $"引擎能力已更新：{capabilities.Parameters.Count} 个可用参数（版本 {capabilities.Version ?? "未知"}）"
            : $"引擎探测失败：{capabilities.Error}");
        return capabilities;
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
