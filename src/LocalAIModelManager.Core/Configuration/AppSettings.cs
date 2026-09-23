using LocalAIModelManager.Core.Localization;
using LocalAIModelManager.Core.Models;

namespace LocalAIModelManager.Core.Configuration;

public enum ValidationSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record ValidationIssue(ValidationSeverity Severity, string Section, string Message)
{
    public override string ToString() => $"[{Severity}] {Section}: {Message}";
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; }

    public bool StartMinimized { get; set; }

    /// <summary>Closing the window hides it to the notification area instead of exiting.</summary>
    public bool CloseToTray { get; set; } = true;

    public bool MinimizeToTray { get; set; } = true;

    public bool ConfirmExit { get; set; } = true;

    /// <summary>"Dark" or "Light".</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Interface language: "zh-CN", "en-US", "ja-JP" or "fr-FR".</summary>
    public string Language { get; set; } = Localizer.DefaultLanguage;

    /// <summary>Probe every engine with <c>--help</c> when the manager starts.</summary>
    public bool ProbeEnginesOnStartup { get; set; } = true;

    public void Normalize()
    {
        Theme = string.Equals(Theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        Language = Localizer.Normalize(Language);
    }
}

public sealed class ApiSettings
{
    public const string Loopback = "127.0.0.1";
    public const string AnyAddress = "0.0.0.0";

    /// <summary>Bind address of the public OpenAI-compatible gateway.</summary>
    public string Host { get; set; } = Loopback;

    public int Port { get; set; } = 8080;

    /// <summary>LAN exposure is opt-in and never enabled by default.</summary>
    public bool AllowLanAccess { get; set; }

    public bool ApiKeyEnabled { get; set; } = true;

    public string ApiKey { get; set; } = string.Empty;

    public bool RequireApiKeyForLocalhost { get; set; } = true;

    public int MaxConcurrentRequests { get; set; } = 64;

    public int RequestTimeoutSeconds { get; set; } = 600;

    /// <summary>Expose <c>/v1/internal/status</c> (always behind the API key when enabled).</summary>
    public bool EnableStatusEndpoint { get; set; } = true;

    public void Normalize()
    {
        Host = string.IsNullOrWhiteSpace(Host) ? Loopback : Host.Trim();
        if (Port is < 1 or > 65535)
        {
            Port = 8080;
        }

        MaxConcurrentRequests = Math.Clamp(MaxConcurrentRequests, 1, 1024);
        RequestTimeoutSeconds = Math.Clamp(RequestTimeoutSeconds, 5, 86_400);
        ApiKey = (ApiKey ?? string.Empty).Trim();
    }

    public bool IsLoopbackOnly =>
        Host is Loopback or "localhost" or "::1";
}

public sealed class EngineSettings
{
    public string SelectedEngineId { get; set; } = string.Empty;

    public List<EngineDefinition> Engines { get; set; } = new();

    public void Normalize()
    {
        Engines ??= new List<EngineDefinition>();
        foreach (var engine in Engines)
        {
            engine.Normalize();
        }

        SelectedEngineId = (SelectedEngineId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(SelectedEngineId) && Engines.Count > 0)
        {
            SelectedEngineId = Engines[0].Id;
        }
    }
}

public sealed class ModelParameterSettings
{
    /// <summary>
    /// Version of the schema migrations already applied to this section. Bumping it is how
    /// a one-off repair reaches existing configuration files.
    /// </summary>
    public int DefaultsVersion { get; set; }

    /// <summary>Current schema version of this section.</summary>
    public const int CurrentDefaultsVersion = 3;

    /// <summary>
    /// Global parameter defaults, applied to every model unless that model overrides them.
    /// EMPTY BY DEFAULT ON PURPOSE: nothing is inferred or added for the user. A parameter
    /// only reaches llama-server when a human configured it here or on the model itself,
    /// so VRAM usage is decided by the model and the engine's own defaults - not by
    /// arbitrary values baked into this application.
    /// </summary>
    public Dictionary<string, string> Defaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Free-form extra flags, one CLI line each (for example <c>--some-option value</c>).
    /// Order is preserved and lines are appended verbatim to the engine command line.
    /// </summary>
    public List<string> AdditionalArguments { get; set; } = new();

    /// <summary>Legacy flag/value form, kept so existing configuration files still load.</summary>
    public Dictionary<string, string> ExtraArguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Normalize()
    {
        Defaults ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ExtraArguments ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AdditionalArguments ??= new List<string>();

        if (DefaultsVersion < CurrentDefaultsVersion)
        {
            // Version 1 and 2 seeded GPU offload / context size automatically. Those were
            // never the user's choices, so they are removed once; from now on the section
            // is only ever filled by explicit user input.
            RemoveIf(value: "99", key: "--n-gpu-layers");
            RemoveIf(value: "8192", key: "--ctx-size");

            DefaultsVersion = CurrentDefaultsVersion;
        }

        Defaults = DictionaryNormalizer.Normalize(Defaults);
        ExtraArguments = DictionaryNormalizer.Normalize(ExtraArguments);
        AdditionalArguments = NormalizeLines(AdditionalArguments);
    }

    private void RemoveIf(string key, string value)
    {
        if (Defaults.TryGetValue(key, out var existing) &&
            string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
        {
            Defaults.Remove(key);
        }
    }

    internal static List<string> NormalizeLines(IEnumerable<string>? lines) =>
        (lines ?? Enumerable.Empty<string>())
            .Select(l => (l ?? string.Empty).Trim())
            .Where(l => l.Length > 0)
            .ToList();
}

public sealed class LifecycleSettings
{
    /// <summary>Idle time after which a loaded model is unloaded again. Default 5 minutes.</summary>
    public int IdleTimeoutSeconds { get; set; } = 300;

    public bool IdleUnloadEnabled { get; set; } = true;

    /// <summary>How many models may be resident at the same time (hard limit).</summary>
    public int MaxLoadedModels { get; set; } = 2;

    /// <summary>Unload the least-recently-used idle model when VRAM runs low.</summary>
    public bool VramEvictionEnabled { get; set; } = true;

    /// <summary>Free VRAM threshold that triggers LRU eviction.</summary>
    public int MinFreeVramMiB { get; set; } = 1024;

    public int ShutdownGraceSeconds { get; set; } = 10;

    /// <summary>
    /// Always false. Loading models at boot is explicitly out of scope: a Windows
    /// reboot must leave every model in standby.
    /// </summary>
    public bool PreloadOnStartup { get; set; }

    public void Normalize()
    {
        IdleTimeoutSeconds = IdleTimeoutSeconds < 1 ? 300 : IdleTimeoutSeconds;
        MaxLoadedModels = Math.Clamp(MaxLoadedModels, 1, 64);
        MinFreeVramMiB = Math.Clamp(MinFreeVramMiB, 0, 1_000_000);
        ShutdownGraceSeconds = Math.Clamp(ShutdownGraceSeconds, 1, 600);
        PreloadOnStartup = false;
    }
}

public sealed class ResourceSettings
{
    public bool MonitorEnabled { get; set; } = true;

    public int PollIntervalMs { get; set; } = 2000;

    public int GpuIndex { get; set; }

    /// <summary>Attribute VRAM usage to individual backend child processes via nvidia-smi.</summary>
    public bool TrackPerProcessVram { get; set; } = true;

    /// <summary>
    /// Ceiling the automatic parameter tuning will spend on a single model, as a
    /// percentage of total VRAM. It is a safety limit, not a goal: the tuner aims at
    /// <see cref="AutoTuneContextSize"/> and only consults this when that does not fit.
    /// </summary>
    public int MaxVramUsagePercent { get; set; } = 70;

    /// <summary>
    /// Context length the automatic tuning aims for. 8192 is a deliberately modest
    /// default - llama.cpp reserves the entire KV cache up front, so a generous value
    /// here is what makes a small model occupy several GiB of VRAM.
    /// </summary>
    public int AutoTuneContextSize { get; set; } = 8192;

    public void Normalize()
    {
        PollIntervalMs = Math.Clamp(PollIntervalMs, 250, 60_000);
        GpuIndex = Math.Max(0, GpuIndex);
        MaxVramUsagePercent = Math.Clamp(MaxVramUsagePercent, 20, 100);
        AutoTuneContextSize = Math.Clamp(AutoTuneContextSize, 512, 1_048_576);
    }
}

public sealed class NetworkSettings
{
    /// <summary>
    /// Bind host for every backend child process. Hard-coded to loopback: internal
    /// engine ports are never exposed to the network, even when LAN access is on.
    /// </summary>
    public string BackendBindHost { get; set; } = ApiSettings.Loopback;

    public int InternalPortRangeStart { get; set; } = 32800;

    public int InternalPortRangeEnd { get; set; } = 32900;

    public string HttpProxy { get; set; } = string.Empty;

    public string NoProxy { get; set; } = string.Empty;

    public void Normalize()
    {
        // Enforced invariant - not a user preference.
        BackendBindHost = ApiSettings.Loopback;
        InternalPortRangeStart = Math.Clamp(InternalPortRangeStart, 1024, 65500);
        InternalPortRangeEnd = Math.Clamp(InternalPortRangeEnd, InternalPortRangeStart + 1, 65535);
        HttpProxy = (HttpProxy ?? string.Empty).Trim();
        NoProxy = (NoProxy ?? string.Empty).Trim();
    }
}

public sealed class AdvancedSettings
{
    public string LogLevel { get; set; } = "Information";

    public int LogBufferSize { get; set; } = 2000;

    /// <summary>
    /// Always false by default: logs stay in memory unless the operator explicitly
    /// uses "Save" on the Runtime Logs page.
    /// </summary>
    public bool LogToDisk { get; set; }

    /// <summary>
    /// Logging full prompts and completions is opt-in and off by default so that no
    /// user content ends up in the log buffer.
    /// </summary>
    public bool LogPromptContent { get; set; }

    /// <summary>Maximum characters logged when <see cref="LogPromptContent"/> is on.</summary>
    public int LogContentMaxChars { get; set; } = 512;

    public long MaxRequestBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Escape hatch for binding to LAN without authentication. Off by default.</summary>
    public bool AllowLanWithoutApiKey { get; set; }

    public bool EnableBackendOutputCapture { get; set; } = true;

    public void Normalize()
    {
        LogLevel = string.IsNullOrWhiteSpace(LogLevel) ? "Information" : LogLevel.Trim();
        LogBufferSize = Math.Clamp(LogBufferSize, 100, 200_000);
        LogContentMaxChars = Math.Clamp(LogContentMaxChars, 0, 100_000);
        MaxRequestBytes = Math.Clamp(MaxRequestBytes, 1024L, 1024L * 1024 * 1024);
        LogToDisk = false;
    }
}

/// <summary>Whole application configuration, split into the dedicated settings pages.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public GeneralSettings General { get; set; } = new();

    public ApiSettings Api { get; set; } = new();

    public EngineSettings Engines { get; set; } = new();

    public ModelParameterSettings ModelParameters { get; set; } = new();

    public LifecycleSettings Lifecycle { get; set; } = new();

    public ResourceSettings Resources { get; set; } = new();

    public NetworkSettings Network { get; set; } = new();

    public AdvancedSettings Advanced { get; set; } = new();

    public void Normalize()
    {
        General ??= new GeneralSettings();
        Api ??= new ApiSettings();
        Engines ??= new EngineSettings();
        ModelParameters ??= new ModelParameterSettings();
        Lifecycle ??= new LifecycleSettings();
        Resources ??= new ResourceSettings();
        Network ??= new NetworkSettings();
        Advanced ??= new AdvancedSettings();

        General.Normalize();
        Api.Normalize();
        Engines.Normalize();
        ModelParameters.Normalize();
        Lifecycle.Normalize();
        Resources.Normalize();
        Network.Normalize();
        Advanced.Normalize();

        if (string.IsNullOrWhiteSpace(Api.ApiKey) && Api.ApiKeyEnabled)
        {
            Api.ApiKey = ApiKeyGenerator.Create();
        }
    }

    /// <summary>Security and sanity checks surfaced in the UI before the gateway starts.</summary>
    public IReadOnlyList<ValidationIssue> Validate()
    {
        var issues = new List<ValidationIssue>();

        if (!Api.IsLoopbackOnly && !Api.AllowLanAccess)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "API",
                Loc.T("validation.hostNotLoopback", Api.Host, ApiSettings.Loopback)));
        }

        if (Api.AllowLanAccess && !Api.ApiKeyEnabled && !Advanced.AllowLanWithoutApiKey)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "API",
                Loc.T("validation.lanWithoutKey")));
        }

        if (Api.AllowLanAccess && !Api.ApiKeyEnabled && Advanced.AllowLanWithoutApiKey)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "API",
                Loc.T("validation.lanWithoutKeyWarning")));
        }

        if (Lifecycle.PreloadOnStartup)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "Lifecycle",
                Loc.T("validation.preloadUnsupported")));
        }

        if (Lifecycle.IdleTimeoutSeconds < 30)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "Lifecycle",
                Loc.T("validation.idleTooShort")));
        }

        if (Advanced.LogToDisk)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Info,
                "Advanced",
                Loc.T("validation.logsMemoryOnly")));
        }

        if (Advanced.LogPromptContent)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "Advanced",
                Loc.T("validation.promptLoggingOn")));
        }

        if (Engines.Engines.Count == 0)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Info,
                "Inference Engine",
                Loc.T("validation.noEngines")));
        }

        foreach (var engine in Engines.Engines.Where(e => !e.ExecutableExists()))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "Inference Engine",
                Loc.T("validation.engineMissingExe", engine.Id, engine.ExecutablePath)));
        }

        return issues;
    }

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, JsonSerialization.Options);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, JsonSerialization.Options) ?? new AppSettings();
    }
}

internal static class DictionaryNormalizer
{
    public static Dictionary<string, string> Normalize(Dictionary<string, string>? source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return result;
        }

        foreach (var (key, value) in source)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key.Trim()] = (value ?? string.Empty).Trim();
            }
        }

        return result;
    }
}
