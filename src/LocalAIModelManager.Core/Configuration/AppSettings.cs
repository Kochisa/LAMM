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

    /// <summary>"zh-CN" or "en-US".</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>Probe every engine with <c>--help</c> when the manager starts.</summary>
    public bool ProbeEnginesOnStartup { get; set; } = true;

    public void Normalize()
    {
        Theme = string.Equals(Theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        Language = string.IsNullOrWhiteSpace(Language) ? "zh-CN" : Language.Trim();
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
    /// <summary>Version of the built-in baseline that has already been seeded into <see cref="Defaults"/>.</summary>
    public int DefaultsVersion { get; set; }

    /// <summary>Current built-in baseline version. Bump when <see cref="BuiltInDefaults"/> changes.</summary>
    public const int CurrentDefaultsVersion = 2;

    /// <summary>
    /// Baseline applied to every model, tuned for a desktop with one GPU.
    ///
    /// <list type="bullet">
    /// <item><c>--n-gpu-layers 99</c>: llama.cpp defaults to <c>-ngl 0</c> (pure CPU), which is
    /// almost never what a desktop user wants.</item>
    /// <item><c>--ctx-size 8192</c>: since llama.cpp treats an unset context as "use the model's
    /// trained context", a long-context model silently reserves an enormous KV cache at load
    /// time (a 256K-context 1.8B model costs ~16 GiB). Bounding the context keeps a small model
    /// actually small; raise it per model when long context is needed.</item>
    /// </list>
    ///
    /// Engines that do not advertise a flag simply drop it (with a warning).
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuiltInDefaults { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["--n-gpu-layers"] = "99",
            ["--ctx-size"] = "8192",
        };

    /// <summary>Default CLI flag values applied to every model unless overridden.</summary>
    public Dictionary<string, string> Defaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Free-form extra flags appended verbatim.</summary>
    public Dictionary<string, string> ExtraArguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Normalize()
    {
        Defaults ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ExtraArguments ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (DefaultsVersion < CurrentDefaultsVersion)
        {
            // One-time migration: seed the baseline, but never overwrite a value the
            // user has already chosen. The version stamp means that if the user later
            // deletes the entry on purpose, it stays deleted.
            foreach (var (key, value) in BuiltInDefaults)
            {
                if (!Defaults.ContainsKey(key))
                {
                    Defaults[key] = value;
                }
            }

            DefaultsVersion = CurrentDefaultsVersion;
        }

        Defaults = DictionaryNormalizer.Normalize(Defaults);
        ExtraArguments = DictionaryNormalizer.Normalize(ExtraArguments);
    }
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
    /// percentage of total VRAM. Keeping this below 100 is what stops a small model
    /// with a long context from filling the whole card and starving everything else.
    /// </summary>
    public int MaxVramUsagePercent { get; set; } = 70;

    public void Normalize()
    {
        PollIntervalMs = Math.Clamp(PollIntervalMs, 250, 60_000);
        GpuIndex = Math.Max(0, GpuIndex);
        MaxVramUsagePercent = Math.Clamp(MaxVramUsagePercent, 20, 100);
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
                $"Bind address '{Api.Host}' is not loopback while LAN access is disabled. The address will be reset to {ApiSettings.Loopback}."));
        }

        if (Api.AllowLanAccess && !Api.ApiKeyEnabled && !Advanced.AllowLanWithoutApiKey)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "API",
                "LAN access requires the API key to be enabled. Enable the API key or turn LAN access off."));
        }

        if (Api.AllowLanAccess && !Api.ApiKeyEnabled && Advanced.AllowLanWithoutApiKey)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "API",
                "LAN access is enabled WITHOUT authentication. Any host on the network can use this gateway."));
        }

        if (Lifecycle.PreloadOnStartup)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "Lifecycle",
                "Preloading models at startup is not supported; models always start in standby."));
        }

        if (Lifecycle.IdleTimeoutSeconds < 30)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "Lifecycle",
                "An idle timeout below 30 seconds will thrash model loading."));
        }

        if (Advanced.LogToDisk)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Info,
                "Advanced",
                "Logs are memory-only by default; use Save on the Runtime Logs page to persist a snapshot."));
        }

        if (Advanced.LogPromptContent)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "Advanced",
                "Prompt/completion logging is enabled. User content will appear in the in-memory log buffer."));
        }

        if (Engines.Engines.Count == 0)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Info,
                "Inference Engine",
                "No inference engine registered yet. Add a llama-server executable to start serving models."));
        }

        foreach (var engine in Engines.Engines.Where(e => !e.ExecutableExists()))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "Inference Engine",
                $"Engine '{engine.Id}' points at a missing executable: {engine.ExecutablePath}"));
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
