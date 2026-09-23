using LocalAIModelManager.App.Services;
using LocalAIModelManager.Core.Backends;
using LocalAIModelManager.Core.Configuration;
using LocalAIModelManager.Core.Runtime;

namespace LocalAIModelManager.App.ViewModels.Settings;

/// <summary>
/// The eight settings pages, as data. Every page owns one section of
/// <see cref="AppSettings"/>; nothing is merged into a single mega page.
///
/// The descriptors are rebuilt on every access so that section titles, descriptions
/// and field labels follow a runtime language switch. Nothing here is cached in a
/// static field: caching would freeze the language the first time the page loaded.
/// </summary>
public static class SettingsSections
{
    public const string General = "general";
    public const string Api = "api";
    public const string InferenceEngine = "engine";
    public const string ModelParameters = "model-parameters";
    public const string Lifecycle = "lifecycle";
    public const string Resources = "resources";
    public const string Network = "network";
    public const string Advanced = "advanced";

    public static IReadOnlyList<SettingsSection> All => Build();

    public static SettingsSection Get(string key) =>
        All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Unknown settings section '{key}'.");

    public static bool IsDescriptorDriven(string key) =>
        key is not (InferenceEngine or ModelParameters);

    /// <summary>
    /// Selectable language codes. The option label is the native display name from
    /// <see cref="Localizer.Languages"/>; the stored value is always the code.
    /// </summary>
    public static IReadOnlyList<string> LanguageChoices() =>
        Localizer.Languages.Select(l => l.Code).ToList();

    /// <summary>Display name for a stored language code.</summary>
    public static string LanguageDisplay(string? code)
    {
        var normalized = Localizer.Normalize(code);
        return Localizer.Languages.FirstOrDefault(l => l.Code == normalized)?.DisplayName ?? normalized;
    }

    /// <summary>Stored language code for one of the language option labels.</summary>
    public static string LanguageCodeFor(string displayName) =>
        Localizer.Languages.FirstOrDefault(l => l.DisplayName == displayName)?.Code
        ?? Localizer.Normalize(displayName);

    private static IReadOnlyList<SettingsSection> Build() => new List<SettingsSection>
    {
        // ------------------------------------------------------------ General
        new()
        {
            Key = General,
            Title = Loc.T("page.settingsGeneral.title"),
            Description = Loc.T("page.settingsGeneral.desc"),
            Fields = new List<SettingField>
            {
                new()
                {
                    Key = "startWithWindows",
                    Label = Loc.T("general.startWithWindows.label"),
                    Kind = SettingFieldKind.Bool,
                    Help = Loc.T("general.startWithWindows.help"),
                    Read = s => Bool(s.General.StartWithWindows),
                    Write = (s, v) => s.General.StartWithWindows = Parse(v),
                },
                new()
                {
                    Key = "startMinimized",
                    Label = Loc.T("general.startMinimized.label"),
                    Kind = SettingFieldKind.Bool,
                    Read = s => Bool(s.General.StartMinimized),
                    Write = (s, v) => s.General.StartMinimized = Parse(v),
                },
                new()
                {
                    Key = "closeToTray",
                    Label = Loc.T("general.closeToTray.label"),
                    Kind = SettingFieldKind.Bool,
                    Read = s => Bool(s.General.CloseToTray),
                    Write = (s, v) => s.General.CloseToTray = Parse(v),
                },
                new()
                {
                    Key = "minimizeToTray",
                    Label = Loc.T("general.minimizeToTray.label"),
                    Kind = SettingFieldKind.Bool,
                    Read = s => Bool(s.General.MinimizeToTray),
                    Write = (s, v) => s.General.MinimizeToTray = Parse(v),
                },
                new()
                {
                    Key = "confirmExit",
                    Label = Loc.T("general.confirmExit.label"),
                    Kind = SettingFieldKind.Bool,
                    Help = Loc.T("general.confirmExit.help"),
                    Read = s => Bool(s.General.ConfirmExit),
                    Write = (s, v) => s.General.ConfirmExit = Parse(v),
                },
                new()
                {
                    Key = "theme",
                    Label = Loc.T("general.theme.label"),
                    Kind = SettingFieldKind.Choice,
                    Choices = new[] { "Dark", "Light" },
                    Help = Loc.T("general.theme.help"),
                    Read = s => s.General.Theme,
                    Write = (s, v) => s.General.Theme = v,
                },
                new()
                {
                    // Options are the installed languages; the stored value is the code
                    // (zh-CN / en-US / ja-JP / fr-FR) so the setting is not translated.
                    Key = "language",
                    Label = Loc.T("general.language.label"),
                    Kind = SettingFieldKind.Choice,
                    Choices = LanguageChoices(),
                    Help = Loc.T("general.language.help"),
                    Read = s => LanguageDisplay(s.General.Language),
                    Write = (s, v) => s.General.Language = LanguageCodeFor(v),
                },
                new()
                {
                    Key = "probeEnginesOnStartup",
                    Label = Loc.T("general.probeEnginesOnStartup.label"),
                    Kind = SettingFieldKind.Bool,
                    Help = Loc.T("general.probeEnginesOnStartup.help"),
                    Read = s => Bool(s.General.ProbeEnginesOnStartup),
                    Write = (s, v) => s.General.ProbeEnginesOnStartup = Parse(v),
                },
            },
            OnSaved = (services, settings) =>
            {
                var executable = Environment.ProcessPath ?? "LAMM.exe";
                var arguments = StartupRegistration.BuildStartupArguments(settings.General.StartMinimized);

                if (settings.General.StartWithWindows)
                {
                    if (!StartupRegistration.TrySetEnabled(true, executable, arguments, out var error))
                    {
                        settings.General.StartWithWindows = false;
                        services.Settings.Update(s => s.General.StartWithWindows = false, persist: true);
                        throw new InvalidOperationException(
                            Loc.T("general.error.startupWrite", error));
                    }

                    services.Notify(Loc.T("general.notify.startupEnabled"));
                }
                else if (!StartupRegistration.TrySetEnabled(false, executable, string.Empty, out var removeError))
                {
                    throw new InvalidOperationException(Loc.T("general.error.startupRemove", removeError));
                }
                else
                {
                    services.Notify(Loc.T("general.notify.startupDisabled"));
                }
            },
            Notes = services => new[]
            {
                Loc.T(
                    "general.note.startup",
                    StartupRegistration.IsEnabled() ? Loc.T("general.note.enabled") : Loc.T("general.note.disabled")),
                Loc.T("general.note.reboot"),
            },
        },

        // ---------------------------------------------------------------- API
        new()
        {
            Key = Api,
            Title = Loc.T("page.settingsApi.title"),
            Description = Loc.T("page.settingsApi.desc"),
            RequiresGatewayRestart = true,
            Fields = new List<SettingField>
            {
                new()
                {
                    Key = "host",
                    Label = Loc.T("api.host.label"),
                    Kind = SettingFieldKind.Choice,
                    Choices = new[] { ApiSettings.Loopback, "0.0.0.0" },
                    Help = Loc.T("api.host.help"),
                    SecurityRelevant = true,
                    Read = s => s.Api.Host,
                    Write = (s, v) => s.Api.Host = v,
                },
                new()
                {
                    Key = "port",
                    Label = Loc.T("api.port.label"),
                    Kind = SettingFieldKind.Number,
                    Min = 1,
                    Max = 65535,
                    Read = s => s.Api.Port.ToString(),
                    Write = (s, v) => s.Api.Port = int.Parse(v),
                },
                new()
                {
                    Key = "allowLan",
                    Label = Loc.T("api.allowLan.label"),
                    Kind = SettingFieldKind.Bool,
                    Help = Loc.T("api.allowLan.help"),
                    SecurityRelevant = true,
                    Read = s => Bool(s.Api.AllowLanAccess),
                    Write = (s, v) => s.Api.AllowLanAccess = Parse(v),
                },
                new()
                {
                    Key = "apiKeyEnabled",
                    Label = Loc.T("api.keyEnabled.label"),
                    Kind = SettingFieldKind.Bool,
                    SecurityRelevant = true,
                    Read = s => Bool(s.Api.ApiKeyEnabled),
                    Write = (s, v) => s.Api.ApiKeyEnabled = Parse(v),
                },
                new()
                {
                    Key = "apiKey",
                    Label = Loc.T("api.key.label"),
                    Kind = SettingFieldKind.Secret,
                    Help = Loc.T("api.key.help"),
                    SecurityRelevant = true,
                    Read = s => s.Api.ApiKey,
                    Write = (s, v) => s.Api.ApiKey = v.Trim(),
                },
                new()
                {
                    Key = "requireKeyLocal",
                    Label = Loc.T("api.requireKeyLocal.label"),
                    Kind = SettingFieldKind.Bool,
                    Read = s => Bool(s.Api.RequireApiKeyForLocalhost),
                    Write = (s, v) => s.Api.RequireApiKeyForLocalhost = Parse(v),
                },
                new()
                {
                    Key = "maxConcurrent",
                    Label = Loc.T("api.maxConcurrent.label"),
                    Kind = SettingFieldKind.Number,
                    Min = 1,
                    Max = 1024,
                    Read = s => s.Api.MaxConcurrentRequests.ToString(),
                    Write = (s, v) => s.Api.MaxConcurrentRequests = int.Parse(v),
                },
                new()
                {
                    Key = "requestTimeout",
                    Label = Loc.T("api.requestTimeout.label"),
                    Suffix = Loc.T("general.unit.seconds"),
                    Kind = SettingFieldKind.Number,
                    Min = 5,
                    Max = 86400,
                    Read = s => s.Api.RequestTimeoutSeconds.ToString(),
                    Write = (s, v) => s.Api.RequestTimeoutSeconds = int.Parse(v),
                },
                new()
                {
                    Key = "statusEndpoint",
                    Label = Loc.T("api.statusEndpoint.label"),
                    Kind = SettingFieldKind.Bool,
                    Help = Loc.T("api.statusEndpoint.help"),
                    Read = s => Bool(s.Api.EnableStatusEndpoint),
                    Write = (s, v) => s.Api.EnableStatusEndpoint = Parse(v),
                },
            },
            Notes = services => services.Current.Validate()
                .Where(i => i.Section == "API" && i.Severity != ValidationSeverity.Info)
                .Select(i => Loc.T(
                    "settings.validation.note",
                    i.Severity == ValidationSeverity.Error
                        ? Loc.T("settings.validation.error")
                        : Loc.T("settings.validation.warning"),
                    i.Message)),
        },

        // --------------------------------------------------- Inference engine
        new()
        {
            Key = InferenceEngine,
            Title = Loc.T("page.settingsEngine.title"),
            Description = Loc.T("page.settingsEngine.desc"),
            Fields = Array.Empty<SettingField>(),
        },

        // --------------------------------------------------- Model parameters
        new()
        {
            Key = ModelParameters,
            Title = Loc.T("page.settingsModelParameters.title"),
            Description = Loc.T("page.settingsModelParameters.desc"),
            Fields = Array.Empty<SettingField>(),
        },

        // ---------------------------------------------------------- Lifecycle
        new()
        {
            Key = Lifecycle,
            Title = Loc.T("page.settingsLifecycle.title"),
            Description = Loc.T("page.settingsLifecycle.desc"),
            Fields = new List<SettingField>
            {
                new()
                {
                    Key = "idleTimeout",
                    Label = Loc.T("lifecycle.idleTimeout.label"),
                    Suffix = Loc.T("general.unit.seconds"),
                    Kind = SettingFieldKind.Number,
                    Min = 1,
                    Max = 86400,
                    Help = Loc.T("lifecycle.idleTimeout.help"),
                    Read = s => s.Lifecycle.IdleTimeoutSeconds.ToString(),
                    Write = (s, v) => s.Lifecycle.IdleTimeoutSeconds = int.Parse(v),
                },
                new()
                {
                    Key = "idleUnloadEnabled",
                    Label = Loc.T("lifecycle.idleUnloadEnabled.label"),
                    Kind = SettingFieldKind.Bool,
                    Read = s => Bool(s.Lifecycle.IdleUnloadEnabled),
                    Write = (s, v) => s.Lifecycle.IdleUnloadEnabled = Parse(v),
                },
                new()
                {
                    Key = "maxLoaded",
                    Label = Loc.T("lifecycle.maxLoaded.label"),
                    Kind = SettingFieldKind.Number,
                    Min = 1,
                    Max = 64,
                    Help = Loc.T("lifecycle.maxLoaded.help"),
                    Read = s => s.Lifecycle.MaxLoadedModels.ToString(),
                    Write = (s, v) => s.Lifecycle.MaxLoadedModels = int.Parse(v),
                },
                new()
                {
                    Key = "vramEviction",
                    Label = Loc.T("lifecycle.vramEviction.label"),
                    Kind = SettingFieldKind.Bool,
                    Read = s => Bool(s.Lifecycle.VramEvictionEnabled),
                    Write = (s, v) => s.Lifecycle.VramEvictionEnabled = Parse(v),
                },
                new()
                {
                    Key = "minFreeVram",
                    Label = Loc.T("lifecycle.minFreeVram.label"),
                    Suffix = "MiB",
                    Kind = SettingFieldKind.Number,
                    Min = 0,
                    Max = 1000000,
                    Read = s => s.Lifecycle.MinFreeVramMiB.ToString(),
                    Write = (s, v) => s.Lifecycle.MinFreeVramMiB = int.Parse(v),
                },
                new()
                {
                    Key = "shutdownGrace",
                    Label = Loc.T("lifecycle.shutdownGrace.label"),
                    Suffix = Loc.T("general.unit.seconds"),
                    Kind = SettingFieldKind.Number,
                    Min = 1,
                    Max = 600,
                    Help = Loc.T("lifecycle.shutdownGrace.help"),
                    Read = s => s.Lifecycle.ShutdownGraceSeconds.ToString(),
                    Write = (s, v) => s.Lifecycle.ShutdownGraceSeconds = int.Parse(v),
                },
                new()
                {
                    Key = "preload",
                    Label = Loc.T("lifecycle.preload.label"),
                    Kind = SettingFieldKind.ReadOnly,
                    Help = Loc.T("lifecycle.preload.help"),
                    Enforced = true,
                    Read = _ => Loc.T("lifecycle.preload.value"),
                },
            },
        },

        // ---------------------------------------------------------- Resources
        new()
        {
            Key = Resources,
            Title = Loc.T("page.settingsResources.title"),
            Description = Loc.T("page.settingsResources.desc"),
            Fields = new List<SettingField>
            {
                new()
                {
                    Key = "monitorEnabled",
                    Label = Loc.T("resources.monitorEnabled.label"),
                    Kind = SettingFieldKind.Bool,
                    Read = s => Bool(s.Resources.MonitorEnabled),
                    Write = (s, v) => s.Resources.MonitorEnabled = Parse(v),
                },
                new()
                {
                    Key = "pollInterval",
                    Label = Loc.T("resources.pollInterval.label"),
                    Suffix = Loc.T("general.unit.milliseconds"),
                    Kind = SettingFieldKind.Number,
                    Min = 250,
                    Max = 60000,
                    Read = s => s.Resources.PollIntervalMs.ToString(),
                    Write = (s, v) => s.Resources.PollIntervalMs = int.Parse(v),
                },
                new()
                {
                    Key = "gpuIndex",
                    Label = Loc.T("resources.gpuIndex.label"),
                    Kind = SettingFieldKind.Number,
                    Min = 0,
                    Max = 64,
                    Read = s => s.Resources.GpuIndex.ToString(),
                    Write = (s, v) => s.Resources.GpuIndex = int.Parse(v),
                },
                new()
                {
                    Key = "trackPerProcess",
                    Label = Loc.T("resources.trackPerProcess.label"),
                    Kind = SettingFieldKind.Bool,
                    Help = Loc.T("resources.trackPerProcess.help"),
                    Read = s => Bool(s.Resources.TrackPerProcessVram),
                    Write = (s, v) => s.Resources.TrackPerProcessVram = Parse(v),
                },
                new()
                {
                    Key = "autoTuneContext",
                    Label = Loc.T("resources.autoTuneContext.label"),
                    Suffix = Loc.T("general.unit.tokens"),
                    Kind = SettingFieldKind.Number,
                    Min = 512,
                    Max = 1048576,
                    Help = Loc.T("resources.autoTuneContext.help"),
                    Read = s => s.Resources.AutoTuneContextSize.ToString(),
                    Write = (s, v) => s.Resources.AutoTuneContextSize = int.Parse(v),
                },
                new()
                {
                    Key = "maxVramUsagePercent",
                    Label = Loc.T("resources.maxVramUsage.label"),
                    Suffix = "%",
                    Kind = SettingFieldKind.Number,
                    Min = 20,
                    Max = 100,
                    Help = Loc.T("resources.maxVramUsage.help"),
                    Read = s => s.Resources.MaxVramUsagePercent.ToString(),
                    Write = (s, v) => s.Resources.MaxVramUsagePercent = int.Parse(v),
                },
            },
            Notes = services =>
            {
                var snapshot = services.Runtime.Resources.Current;
                var lines = new List<string>
                {
                    snapshot.GpuAvailable
                        ? Loc.T(
                            "resources.note.gpu",
                            snapshot.PrimaryGpu?.Name,
                            (snapshot.PrimaryGpu?.UsedBytes / (1024.0 * 1024 * 1024)).GetValueOrDefault().ToString("F1"),
                            (snapshot.PrimaryGpu?.TotalBytes / (1024.0 * 1024 * 1024)).GetValueOrDefault().ToString("F1"))
                        : Loc.T(
                            "resources.note.gpuUnavailable",
                            snapshot.GpuError ?? Loc.T("resources.note.gpuMissing")),
                };

                if (snapshot.SystemCpuPercent is { } cpu)
                {
                    lines.Add(Loc.T("resources.note.cpu", cpu.ToString("F1")));
                }

                return lines;
            },
        },

        // ------------------------------------------------------------ Network
        new()
        {
            Key = Network,
            Title = Loc.T("page.settingsNetwork.title"),
            Description = Loc.T("page.settingsNetwork.desc"),
            Fields = new List<SettingField>
            {
                new()
                {
                    Key = "backendBindHost",
                    Label = Loc.T("network.backendBindHost.label"),
                    Kind = SettingFieldKind.ReadOnly,
                    Enforced = true,
                    SecurityRelevant = true,
                    Help = Loc.T("network.backendBindHost.help"),
                    Read = _ => ApiSettings.Loopback,
                },
                new()
                {
                    Key = "portRangeStart",
                    Label = Loc.T("network.portRangeStart.label"),
                    Kind = SettingFieldKind.Number,
                    Min = 1024,
                    Max = 65500,
                    Read = s => s.Network.InternalPortRangeStart.ToString(),
                    Write = (s, v) => s.Network.InternalPortRangeStart = int.Parse(v),
                },
                new()
                {
                    Key = "portRangeEnd",
                    Label = Loc.T("network.portRangeEnd.label"),
                    Kind = SettingFieldKind.Number,
                    Min = 1025,
                    Max = 65535,
                    Read = s => s.Network.InternalPortRangeEnd.ToString(),
                    Write = (s, v) => s.Network.InternalPortRangeEnd = int.Parse(v),
                },
                new()
                {
                    Key = "httpProxy",
                    Label = Loc.T("network.httpProxy.label"),
                    Help = Loc.T("network.httpProxy.help"),
                    Read = s => s.Network.HttpProxy,
                    Write = (s, v) => s.Network.HttpProxy = v,
                },
                new()
                {
                    Key = "noProxy",
                    Label = Loc.T("network.noProxy.label"),
                    Read = s => s.Network.NoProxy,
                    Write = (s, v) => s.Network.NoProxy = v,
                },
            },
            Notes = _ => new[]
            {
                Loc.T("network.note.internal"),
            },
        },

        // ----------------------------------------------------------- Advanced
        new()
        {
            Key = Advanced,
            Title = Loc.T("page.settingsAdvanced.title"),
            Description = Loc.T("page.settingsAdvanced.desc"),
            Fields = new List<SettingField>
            {
                new()
                {
                    Key = "logLevel",
                    Label = Loc.T("advanced.logLevel.label"),
                    Kind = SettingFieldKind.Choice,
                    Choices = new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical" },
                    Read = s => s.Advanced.LogLevel,
                    Write = (s, v) => s.Advanced.LogLevel = v,
                },
                new()
                {
                    Key = "logBufferSize",
                    Label = Loc.T("advanced.logBufferSize.label"),
                    Kind = SettingFieldKind.Number,
                    Min = 100,
                    Max = 200000,
                    Help = Loc.T("advanced.logBufferSize.help"),
                    Read = s => s.Advanced.LogBufferSize.ToString(),
                    Write = (s, v) => s.Advanced.LogBufferSize = int.Parse(v),
                },
                new()
                {
                    Key = "logPromptContent",
                    Label = Loc.T("advanced.logPromptContent.label"),
                    Kind = SettingFieldKind.Bool,
                    SecurityRelevant = true,
                    Help = Loc.T("advanced.logPromptContent.help"),
                    Read = s => Bool(s.Advanced.LogPromptContent),
                    Write = (s, v) => s.Advanced.LogPromptContent = Parse(v),
                },
                new()
                {
                    Key = "logContentMaxChars",
                    Label = Loc.T("advanced.logContentMaxChars.label"),
                    Kind = SettingFieldKind.Number,
                    Min = 0,
                    Max = 100000,
                    Read = s => s.Advanced.LogContentMaxChars.ToString(),
                    Write = (s, v) => s.Advanced.LogContentMaxChars = int.Parse(v),
                },
                new()
                {
                    Key = "maxRequestBytes",
                    Label = Loc.T("advanced.maxRequestBytes.label"),
                    Suffix = Loc.T("general.unit.bytes"),
                    Kind = SettingFieldKind.Number,
                    Min = 1024,
                    Max = 1073741824,
                    Read = s => s.Advanced.MaxRequestBytes.ToString(),
                    Write = (s, v) => s.Advanced.MaxRequestBytes = long.Parse(v),
                },
                new()
                {
                    Key = "captureBackendOutput",
                    Label = Loc.T("advanced.captureBackendOutput.label"),
                    Kind = SettingFieldKind.Bool,
                    Help = Loc.T("advanced.captureBackendOutput.help"),
                    Read = s => Bool(s.Advanced.EnableBackendOutputCapture),
                    Write = (s, v) => s.Advanced.EnableBackendOutputCapture = Parse(v),
                },
                new()
                {
                    Key = "allowLanWithoutKey",
                    Label = Loc.T("advanced.allowLanWithoutKey.label"),
                    Kind = SettingFieldKind.Bool,
                    SecurityRelevant = true,
                    Help = Loc.T("advanced.allowLanWithoutKey.help"),
                    Read = s => Bool(s.Advanced.AllowLanWithoutApiKey),
                    Write = (s, v) => s.Advanced.AllowLanWithoutApiKey = Parse(v),
                },
                new()
                {
                    Key = "configDirectory",
                    Label = Loc.T("advanced.configDirectory.label"),
                    Kind = SettingFieldKind.ReadOnly,
                    Read = _ => string.Empty,
                },
            },
            Notes = services => new[]
            {
                Loc.T("advanced.note.files", services.ConfigDirectory),
                Loc.T("advanced.note.memory"),
            },
        },
    };

    private static string Bool(bool value) => value ? "true" : "false";

    private static bool Parse(string value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        value == "1" ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}
