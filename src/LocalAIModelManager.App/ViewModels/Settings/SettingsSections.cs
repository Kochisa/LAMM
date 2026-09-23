using LocalAIModelManager.App.Services;
using LocalAIModelManager.Core.Backends;
using LocalAIModelManager.Core.Configuration;
using LocalAIModelManager.Core.Runtime;

namespace LocalAIModelManager.App.ViewModels.Settings;

/// <summary>
/// The eight settings pages, as data. Every page owns one section of
/// <see cref="AppSettings"/>; nothing is merged into a single mega page.
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

    private static readonly Lazy<IReadOnlyList<SettingsSection>> Sections = new(Build);

    public static IReadOnlyList<SettingsSection> All => Sections.Value;

    public static SettingsSection Get(string key) =>
        All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Unknown settings section '{key}'.");

    public static bool IsDescriptorDriven(string key) =>
        key is not (InferenceEngine or ModelParameters);

    private static IReadOnlyList<SettingsSection> Build() => new List<SettingsSection>
    {
        // ------------------------------------------------------------ General
        new()
        {
            Key = General,
            Title = "常规",
            Description = "启动行为、主题与托盘选项。所有模型在任何情况下都保持待机，不会随 Windows 启动自动加载。",
            Fields = new List<SettingField>
            {
                new()
                {
                    Key = "startWithWindows",
                    Label = "随 Windows 启动",
                    Kind = SettingFieldKind.Bool,
                    Help = "写入 HKCU\\...\\Run。启动命令不带任何加载参数，重启后模型仍全部处于待机。",
                    Read = s => Bool(s.General.StartWithWindows),
                    Write = (s, v) => s.General.StartWithWindows = Parse(v),
                },
                new()
                {
                    Key = "startMinimized",
                    Label = "启动时最小化到托盘",
                    Kind = SettingFieldKind.Bool,
                    Read = s => Bool(s.General.StartMinimized),
                    Write = (s, v) => s.General.StartMinimized = Parse(v),
                },
                new()
                {
                    Key = "closeToTray",
                    Label = "关闭窗口时最小化到托盘",
                    Kind = SettingFieldKind.Bool,
                    Read = s => Bool(s.General.CloseToTray),
                    Write = (s, v) => s.General.CloseToTray = Parse(v),
                },
                new()
                {
                    Key = "minimizeToTray",
                    Label = "最小化时隐藏到托盘",
                    Kind = SettingFieldKind.Bool,
                    Read = s => Bool(s.General.MinimizeToTray),
                    Write = (s, v) => s.General.MinimizeToTray = Parse(v),
                },
                new()
                {
                    Key = "confirmExit",
                    Label = "退出前确认",
                    Kind = SettingFieldKind.Bool,
                    Help = "退出会卸载所有已加载模型并结束引擎进程。",
                    Read = s => Bool(s.General.ConfirmExit),
                    Write = (s, v) => s.General.ConfirmExit = Parse(v),
                },
                new()
                {
                    Key = "theme",
                    Label = "主题",
                    Kind = SettingFieldKind.Choice,
                    Choices = new[] { "Dark", "Light" },
                    Read = s => s.General.Theme,
                    Write = (s, v) => s.General.Theme = v,
                },
                new()
                {
                    Key = "probeEnginesOnStartup",
                    Label = "启动时探测引擎能力",
                    Kind = SettingFieldKind.Bool,
                    Help = "只运行引擎的 --help，不会加载任何模型。",
                    Read = s => Bool(s.General.ProbeEnginesOnStartup),
                    Write = (s, v) => s.General.ProbeEnginesOnStartup = Parse(v),
                },
            },
            OnSaved = (services, settings) =>
            {
                var executable = Environment.ProcessPath ?? "LocalAIModelManager.exe";
                var arguments = StartupRegistration.BuildStartupArguments(settings.General.StartMinimized);

                if (settings.General.StartWithWindows)
                {
                    if (!StartupRegistration.TrySetEnabled(true, executable, arguments, out var error))
                    {
                        settings.General.StartWithWindows = false;
                        services.Settings.Update(s => s.General.StartWithWindows = false, persist: true);
                        throw new InvalidOperationException(
                            $"无法写入开机启动项（HKCU\\...\\Run）：{error}。该设置已回滚为关闭。");
                    }

                    services.Notify("已启用随 Windows 启动（不会自动加载模型）。");
                }
                else if (!StartupRegistration.TrySetEnabled(false, executable, string.Empty, out var removeError))
                {
                    throw new InvalidOperationException($"无法移除开机启动项：{removeError}");
                }
                else
                {
                    services.Notify("已关闭随 Windows 启动。");
                }
            },
            Notes = services => new[]
            {
                $"当前开机启动状态：{(StartupRegistration.IsEnabled() ? "已启用" : "未启用")}",
                "Windows 重启后：管理器只启动网关，所有模型保持在待机状态。",
            },
        },

        // ---------------------------------------------------------------- API
        new()
        {
            Key = Api,
            Title = "API",
            Description = "OpenAI 兼容网关的监听地址、端口与鉴权。默认只绑定回环地址。",
            RequiresGatewayRestart = true,
            Fields = new List<SettingField>
            {
                new()
                {
                    Key = "host",
                    Label = "监听地址",
                    Kind = SettingFieldKind.Choice,
                    Choices = new[] { ApiSettings.Loopback, "0.0.0.0" },
                    Help = "127.0.0.1 = 仅本机；0.0.0.0 = 允许局域网访问（需显式开启）。",
                    SecurityRelevant = true,
                    Read = s => s.Api.Host,
                    Write = (s, v) => s.Api.Host = v,
                },
                new()
                {
                    Key = "port",
                    Label = "监听端口",
                    Kind = SettingFieldKind.Number,
                    Min = 1,
                    Max = 65535,
                    Read = s => s.Api.Port.ToString(),
                    Write = (s, v) => s.Api.Port = int.Parse(v),
                },
                new()
                {
                    Key = "allowLan",
                    Label = "允许局域网访问",
                    Kind = SettingFieldKind.Bool,
                    Help = "必须在开启 API 密钥的前提下才能生效；否则网关会强制回退到回环地址。",
                    SecurityRelevant = true,
                    Read = s => Bool(s.Api.AllowLanAccess),
                    Write = (s, v) => s.Api.AllowLanAccess = Parse(v),
                },
                new()
                {
                    Key = "apiKeyEnabled",
                    Label = "启用 API 密钥",
                    Kind = SettingFieldKind.Bool,
                    SecurityRelevant = true,
                    Read = s => Bool(s.Api.ApiKeyEnabled),
                    Write = (s, v) => s.Api.ApiKeyEnabled = Parse(v),
                },
                new()
                {
                    Key = "apiKey",
                    Label = "API 密钥",
                    Kind = SettingFieldKind.Secret,
                    Help = "客户端使用 Authorization: Bearer <API_KEY>。日志中永不记录完整密钥。",
                    SecurityRelevant = true,
                    Read = s => s.Api.ApiKey,
                    Write = (s, v) => s.Api.ApiKey = v.Trim(),
                },
                new()
                {
                    Key = "requireKeyLocal",
                    Label = "本机请求同样要求密钥",
                    Kind = SettingFieldKind.Bool,
                    Read = s => Bool(s.Api.RequireApiKeyForLocalhost),
                    Write = (s, v) => s.Api.RequireApiKeyForLocalhost = Parse(v),
                },
                new()
                {
                    Key = "maxConcurrent",
                    Label = "最大并发请求数",
                    Kind = SettingFieldKind.Number,
                    Min = 1,
                    Max = 1024,
                    Read = s => s.Api.MaxConcurrentRequests.ToString(),
                    Write = (s, v) => s.Api.MaxConcurrentRequests = int.Parse(v),
                },
                new()
                {
                    Key = "requestTimeout",
                    Label = "请求超时",
                    Suffix = "秒",
                    Kind = SettingFieldKind.Number,
                    Min = 5,
                    Max = 86400,
                    Read = s => s.Api.RequestTimeoutSeconds.ToString(),
                    Write = (s, v) => s.Api.RequestTimeoutSeconds = int.Parse(v),
                },
                new()
                {
                    Key = "statusEndpoint",
                    Label = "开放 /v1/internal/status",
                    Kind = SettingFieldKind.Bool,
                    Help = "始终位于 API 密钥之后。",
                    Read = s => Bool(s.Api.EnableStatusEndpoint),
                    Write = (s, v) => s.Api.EnableStatusEndpoint = Parse(v),
                },
            },
            Notes = services => services.Current.Validate()
                .Where(i => i.Section == "API" && i.Severity != ValidationSeverity.Info)
                .Select(i => $"{(i.Severity == ValidationSeverity.Error ? "错误" : "警告")}：{i.Message}"),
        },

        // --------------------------------------------------- Inference engine
        new()
        {
            Key = InferenceEngine,
            Title = "推理引擎",
            Description = "注册、切换与升级推理引擎。引擎可独立替换，模型记录只引用引擎 ID。",
            Fields = Array.Empty<SettingField>(),
        },

        // --------------------------------------------------- Model parameters
        new()
        {
            Key = ModelParameters,
            Title = "模型参数",
            Description = "参数的可见项由所安装引擎的 --help 输出决定，升级 llama.cpp 后会自动出现新参数。",
            Fields = Array.Empty<SettingField>(),
        },

        // ---------------------------------------------------------- Lifecycle
        new()
        {
            Key = Lifecycle,
            Title = "生命周期",
            Description = "按需加载、空闲卸载与显存不足时的驱逐策略。",
            Fields = new List<SettingField>
            {
                new()
                {
                    Key = "idleTimeout",
                    Label = "空闲卸载超时",
                    Suffix = "秒",
                    Kind = SettingFieldKind.Number,
                    Min = 1,
                    Max = 86400,
                    Help = "默认 300 秒（5 分钟）。超时后自动卸载模型并释放显存。",
                    Read = s => s.Lifecycle.IdleTimeoutSeconds.ToString(),
                    Write = (s, v) => s.Lifecycle.IdleTimeoutSeconds = int.Parse(v),
                },
                new()
                {
                    Key = "idleUnloadEnabled",
                    Label = "启用空闲自动卸载",
                    Kind = SettingFieldKind.Bool,
                    Read = s => Bool(s.Lifecycle.IdleUnloadEnabled),
                    Write = (s, v) => s.Lifecycle.IdleUnloadEnabled = Parse(v),
                },
                new()
                {
                    Key = "maxLoaded",
                    Label = "同时加载的模型上限",
                    Kind = SettingFieldKind.Number,
                    Min = 1,
                    Max = 64,
                    Help = "超出上限时按最近最少使用（LRU）驱逐空闲模型。",
                    Read = s => s.Lifecycle.MaxLoadedModels.ToString(),
                    Write = (s, v) => s.Lifecycle.MaxLoadedModels = int.Parse(v),
                },
                new()
                {
                    Key = "vramEviction",
                    Label = "显存不足时驱逐空闲模型",
                    Kind = SettingFieldKind.Bool,
                    Read = s => Bool(s.Lifecycle.VramEvictionEnabled),
                    Write = (s, v) => s.Lifecycle.VramEvictionEnabled = Parse(v),
                },
                new()
                {
                    Key = "minFreeVram",
                    Label = "触发驱逐的空闲显存阈值",
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
                    Label = "关闭引擎的优雅等待时间",
                    Suffix = "秒",
                    Kind = SettingFieldKind.Number,
                    Min = 1,
                    Max = 600,
                    Help = "超时后强制结束进程树；Job Object 保证不会留下孤儿进程。",
                    Read = s => s.Lifecycle.ShutdownGraceSeconds.ToString(),
                    Write = (s, v) => s.Lifecycle.ShutdownGraceSeconds = int.Parse(v),
                },
                new()
                {
                    Key = "preload",
                    Label = "启动时预加载模型",
                    Kind = SettingFieldKind.ReadOnly,
                    Help = "出于安全与可预期性，本管理器不支持开机预加载；模型始终先待机，首次请求时再加载。",
                    Enforced = true,
                    Read = _ => "始终关闭",
                },
            },
        },

        // ---------------------------------------------------------- Resources
        new()
        {
            Key = Resources,
            Title = "资源",
            Description = "显存 / CPU 采样。仅在本机读取 nvidia-smi 与系统计数器，不联网。",
            Fields = new List<SettingField>
            {
                new()
                {
                    Key = "monitorEnabled",
                    Label = "启用资源监控",
                    Kind = SettingFieldKind.Bool,
                    Read = s => Bool(s.Resources.MonitorEnabled),
                    Write = (s, v) => s.Resources.MonitorEnabled = Parse(v),
                },
                new()
                {
                    Key = "pollInterval",
                    Label = "采样间隔",
                    Suffix = "毫秒",
                    Kind = SettingFieldKind.Number,
                    Min = 250,
                    Max = 60000,
                    Read = s => s.Resources.PollIntervalMs.ToString(),
                    Write = (s, v) => s.Resources.PollIntervalMs = int.Parse(v),
                },
                new()
                {
                    Key = "gpuIndex",
                    Label = "主 GPU 索引",
                    Kind = SettingFieldKind.Number,
                    Min = 0,
                    Max = 64,
                    Read = s => s.Resources.GpuIndex.ToString(),
                    Write = (s, v) => s.Resources.GpuIndex = int.Parse(v),
                },
                new()
                {
                    Key = "trackPerProcess",
                    Label = "按进程统计显存占用",
                    Kind = SettingFieldKind.Bool,
                    Help = "通过 nvidia-smi --query-compute-apps 关联 PID 与显存。",
                    Read = s => Bool(s.Resources.TrackPerProcessVram),
                    Write = (s, v) => s.Resources.TrackPerProcessVram = Parse(v),
                },
                new()
                {
                    Key = "autoTuneContext",
                    Label = "自动调参的目标上下文",
                    Suffix = "token",
                    Kind = SettingFieldKind.Number,
                    Min = 512,
                    Max = 1048576,
                    Help = "自动调参按这个值配上下文，而不是「显存能塞多少就塞多少」。KV cache 按上下文一次性预留，" +
                           "所以这个值直接决定显存占用：1.8B 模型在 8192 下 KV 约 0.5 GiB，在 131072 下约 8 GiB。默认 8192。",
                    Read = s => s.Resources.AutoTuneContextSize.ToString(),
                    Write = (s, v) => s.Resources.AutoTuneContextSize = int.Parse(v),
                },
                new()
                {
                    Key = "maxVramUsagePercent",
                    Label = "单模型显存安全上限",
                    Suffix = "%",
                    Kind = SettingFieldKind.Number,
                    Min = 20,
                    Max = 100,
                    Help = "只作为安全阀：当目标上下文都放不下时才用它来收缩。不是「尽量用满」的目标值。默认 70%。",
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
                        ? $"GPU：{snapshot.PrimaryGpu?.Name}，已用 {snapshot.PrimaryGpu?.UsedBytes / (1024.0 * 1024 * 1024):F1} / {snapshot.PrimaryGpu?.TotalBytes / (1024.0 * 1024 * 1024):F1} GiB"
                        : $"GPU：不可用（{snapshot.GpuError ?? "未检测到 nvidia-smi"}）",
                };

                if (snapshot.SystemCpuPercent is { } cpu)
                {
                    lines.Add($"CPU 使用率：{cpu:F1}%");
                }

                return lines;
            },
        },

        // ------------------------------------------------------------ Network
        new()
        {
            Key = Network,
            Title = "网络",
            Description = "内部引擎端口与出站代理。内部端口永远只绑定回环地址，不对外暴露。",
            Fields = new List<SettingField>
            {
                new()
                {
                    Key = "backendBindHost",
                    Label = "引擎内部绑定地址",
                    Kind = SettingFieldKind.ReadOnly,
                    Enforced = true,
                    SecurityRelevant = true,
                    Help = "强制为 127.0.0.1，即使开启局域网访问也不会改变。",
                    Read = _ => ApiSettings.Loopback,
                },
                new()
                {
                    Key = "portRangeStart",
                    Label = "内部端口范围起始",
                    Kind = SettingFieldKind.Number,
                    Min = 1024,
                    Max = 65500,
                    Read = s => s.Network.InternalPortRangeStart.ToString(),
                    Write = (s, v) => s.Network.InternalPortRangeStart = int.Parse(v),
                },
                new()
                {
                    Key = "portRangeEnd",
                    Label = "内部端口范围结束",
                    Kind = SettingFieldKind.Number,
                    Min = 1025,
                    Max = 65535,
                    Read = s => s.Network.InternalPortRangeEnd.ToString(),
                    Write = (s, v) => s.Network.InternalPortRangeEnd = int.Parse(v),
                },
                new()
                {
                    Key = "httpProxy",
                    Label = "出站 HTTP 代理",
                    Help = "仅用于引擎自身的下载类功能；管理器内部到引擎的流量永不使用代理。",
                    Read = s => s.Network.HttpProxy,
                    Write = (s, v) => s.Network.HttpProxy = v,
                },
                new()
                {
                    Key = "noProxy",
                    Label = "代理排除列表",
                    Read = s => s.Network.NoProxy,
                    Write = (s, v) => s.Network.NoProxy = v,
                },
            },
            Notes = _ => new[]
            {
                "内部端口仅在 127.0.0.1 上监听；网关是唯一对外的监听者。",
            },
        },

        // ----------------------------------------------------------- Advanced
        new()
        {
            Key = Advanced,
            Title = "高级",
            Description = "日志、请求上限与实验性开关。日志默认只保存在内存中。",
            Fields = new List<SettingField>
            {
                new()
                {
                    Key = "logLevel",
                    Label = "日志级别",
                    Kind = SettingFieldKind.Choice,
                    Choices = new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical" },
                    Read = s => s.Advanced.LogLevel,
                    Write = (s, v) => s.Advanced.LogLevel = v,
                },
                new()
                {
                    Key = "logBufferSize",
                    Label = "内存日志条数上限",
                    Kind = SettingFieldKind.Number,
                    Min = 100,
                    Max = 200000,
                    Help = "环形缓冲区，超出后覆盖最旧的记录。",
                    Read = s => s.Advanced.LogBufferSize.ToString(),
                    Write = (s, v) => s.Advanced.LogBufferSize = int.Parse(v),
                },
                new()
                {
                    Key = "logPromptContent",
                    Label = "记录完整提示词与输出",
                    Kind = SettingFieldKind.Bool,
                    SecurityRelevant = true,
                    Help = "默认关闭：开启后用户内容会进入内存日志缓冲区。",
                    Read = s => Bool(s.Advanced.LogPromptContent),
                    Write = (s, v) => s.Advanced.LogPromptContent = Parse(v),
                },
                new()
                {
                    Key = "logContentMaxChars",
                    Label = "内容日志截断长度",
                    Kind = SettingFieldKind.Number,
                    Min = 0,
                    Max = 100000,
                    Read = s => s.Advanced.LogContentMaxChars.ToString(),
                    Write = (s, v) => s.Advanced.LogContentMaxChars = int.Parse(v),
                },
                new()
                {
                    Key = "maxRequestBytes",
                    Label = "请求体大小上限",
                    Suffix = "字节",
                    Kind = SettingFieldKind.Number,
                    Min = 1024,
                    Max = 1073741824,
                    Read = s => s.Advanced.MaxRequestBytes.ToString(),
                    Write = (s, v) => s.Advanced.MaxRequestBytes = long.Parse(v),
                },
                new()
                {
                    Key = "captureBackendOutput",
                    Label = "捕获引擎标准输出",
                    Kind = SettingFieldKind.Bool,
                    Help = "引擎输出只进入内存日志缓冲区，不会写入磁盘。",
                    Read = s => Bool(s.Advanced.EnableBackendOutputCapture),
                    Write = (s, v) => s.Advanced.EnableBackendOutputCapture = Parse(v),
                },
                new()
                {
                    Key = "allowLanWithoutKey",
                    Label = "允许无密钥的局域网访问",
                    Kind = SettingFieldKind.Bool,
                    SecurityRelevant = true,
                    Help = "默认关闭。开启后，任何能访问该端口的主机都可以调用模型。",
                    Read = s => Bool(s.Advanced.AllowLanWithoutApiKey),
                    Write = (s, v) => s.Advanced.AllowLanWithoutApiKey = Parse(v),
                },
                new()
                {
                    Key = "configDirectory",
                    Label = "配置目录",
                    Kind = SettingFieldKind.ReadOnly,
                    Read = _ => string.Empty,
                },
            },
            Notes = services => new[]
            {
                $"配置文件：settings.json / models.json（{services.ConfigDirectory}）",
                "日志默认仅驻留内存；只有点击“保存”才会写入磁盘。",
            },
        },
    };

    private static string Bool(bool value) => value ? "true" : "false";

    private static bool Parse(string value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        value == "1" ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}
