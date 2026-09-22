using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using LocalAIModelManager.App.Infrastructure;
using LocalAIModelManager.App.Services;
using LocalAIModelManager.Core.Configuration;
using LocalAIModelManager.Core.Models;
using LocalAIModelManager.Core.Runtime;

namespace LocalAIModelManager.App.ViewModels;

public sealed record StatusRow(string Label, string Value, string? Hint = null, string Tone = "normal");

/// <summary>Runtime Status page: gateway, loaded models, processes and resources.</summary>
public sealed class RuntimeStatusViewModel : PageViewModelBase
{
    private readonly DispatcherTimer _timer;
    private bool _initialized;
    private RuntimeStatusSnapshot? _snapshot;

    public RuntimeStatusViewModel(AppServices services)
        : base(services)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => RefreshSnapshot();

        RefreshCommand = new RelayCommand(_ => RefreshSnapshot());
        CopyJsonCommand = new RelayCommand(_ => CopyJson());
        UnloadAllCommand = new AsyncRelayCommand(
            async () =>
            {
                await Services.Lifecycle.UnloadAllAsync(CancellationToken.None).ConfigureAwait(true);
                RefreshSnapshot();
                SetStatus("已卸载全部模型。");
            },
            () => !IsBusy);
    }

    public override string Title => "运行状态";

    public override string Description => "网关、已加载模型、引擎进程与显存占用的实时视图。";

    public ObservableCollection<ModelRowViewModel> Models { get; } = new();

    public ObservableCollection<BackendProcessInfo> Processes { get; } = new();

    public ObservableCollection<BackendProcessInfo> ExitedProcesses { get; } = new();

    public ObservableCollection<StatusRow> GatewayRows { get; } = new();

    public ObservableCollection<StatusRow> ResourceRows { get; } = new();

    public ObservableCollection<EngineStatusInfo> Engines { get; } = new();

    public ObservableCollection<ValidationIssue> Issues { get; } = new();

    public ICommand RefreshCommand { get; }

    public ICommand CopyJsonCommand { get; }

    public ICommand UnloadAllCommand { get; }

    public bool GpuAvailable => _snapshot?.Resources.GpuAvailable ?? false;

    public string GpuName => _snapshot?.Resources.PrimaryGpu?.Name ?? "未检测到 NVIDIA GPU";

    public double GpuUsedPercent => _snapshot?.Resources.PrimaryGpu?.UsedPercent ?? 0;

    public string GpuUsedText
    {
        get
        {
            var gpu = _snapshot?.Resources.PrimaryGpu;
            if (gpu is null)
            {
                return "—";
            }

            const double GiB = 1024.0 * 1024 * 1024;
            return $"{gpu.UsedBytes / GiB:F1} / {gpu.TotalBytes / GiB:F1} GiB（空闲 {gpu.FreeBytes / GiB:F1} GiB）";
        }
    }

    public double GpuUtilization => _snapshot?.Resources.PrimaryGpu?.UtilizationPercent ?? 0;

    public string GpuTemperature =>
        _snapshot?.Resources.PrimaryGpu is { TemperatureCelsius: > 0 } gpu ? $"{gpu.TemperatureCelsius:F0} °C" : "—";

    public double CpuPercent => _snapshot?.Resources.SystemCpuPercent ?? 0;

    public string MemoryText
    {
        get
        {
            var total = _snapshot?.Resources.TotalMemoryBytes;
            var available = _snapshot?.Resources.AvailableMemoryBytes;
            if (total is null || available is null)
            {
                return "—";
            }

            const double GiB = 1024.0 * 1024 * 1024;
            return $"{(total - available) / GiB:F1} / {total / GiB:F1} GiB";
        }
    }

    public string LoadedSummary
    {
        get
        {
            var snapshot = _snapshot;
            return snapshot is null
                ? "—"
                : $"已加载 {snapshot.LoadedModelCount} 个，待机 {snapshot.StandbyModelCount} 个，共 {snapshot.Models.Count} 个模型";
        }
    }

    public override Task InitializeAsync()
    {
        if (!_initialized)
        {
            _initialized = true;
            RefreshSnapshot();
        }

        return Task.CompletedTask;
    }

    public override Task RefreshAsync()
    {
        RefreshSnapshot();
        return Task.CompletedTask;
    }

    protected override void OnActivated() => _timer.Start();

    protected override void OnDeactivated() => _timer.Stop();

    private void RefreshSnapshot()
    {
        try
        {
            var snapshot = Services.GetStatus();
            _snapshot = snapshot;

            GatewayRows.Clear();
            var gateway = snapshot.Gateway;
            GatewayRows.Add(new StatusRow("网关状态", gateway.IsRunning ? "运行中" : "已停止",
                gateway.IsRunning ? null : "可在“API”设置页保存后重启网关",
                gateway.IsRunning ? "ok" : "error"));
            GatewayRows.Add(new StatusRow("Base URL", gateway.BaseUrl ?? "—",
                gateway.LanAccessEnabled ? "已允许局域网访问" : "仅本机可访问（127.0.0.1）"));
            GatewayRows.Add(new StatusRow("鉴权", gateway.ApiKeyEnabled ? "已启用 API 密钥" : "未启用鉴权",
                gateway.ApiKeyEnabled ? "Authorization: Bearer <API_KEY>" : "任何能访问该端口的主机都可调用",
                gateway.ApiKeyEnabled ? "ok" : "warn"));
            GatewayRows.Add(new StatusRow("启动时间", gateway.StartedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—"));
            GatewayRows.Add(new StatusRow("累计请求", gateway.TotalRequests.ToString()));
            GatewayRows.Add(new StatusRow("进行中请求", gateway.ActiveRequests.ToString()));
            GatewayRows.Add(new StatusRow("配置目录", snapshot.ConfigDirectory ?? "—"));
            GatewayRows.Add(new StatusRow("内存日志条数", snapshot.LogEntryCount.ToString()));

            if (gateway.LastError is { Length: > 0 } lastError)
            {
                GatewayRows.Add(new StatusRow("最近错误", lastError, null, "error"));
            }

            ResourceRows.Clear();
            ResourceRows.Add(new StatusRow("GPU", GpuName, GpuAvailable ? null : snapshot.Resources.GpuError, GpuAvailable ? "ok" : "warn"));
            ResourceRows.Add(new StatusRow("显存", GpuUsedText));
            ResourceRows.Add(new StatusRow("GPU 利用率", GpuAvailable ? $"{GpuUtilization:F0}%" : "—"));
            ResourceRows.Add(new StatusRow("GPU 温度", GpuTemperature));
            ResourceRows.Add(new StatusRow("系统 CPU", _snapshot?.Resources.SystemCpuPercent is { } cpu ? $"{cpu:F1}%" : "—"));
            ResourceRows.Add(new StatusRow("物理内存", MemoryText));
            ResourceRows.Add(new StatusRow("模型汇总", LoadedSummary));

            Models.Clear();
            var definitions = Services.Models.All.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var status in snapshot.Models.OrderBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase))
            {
                definitions.TryGetValue(status.ModelId, out var definition);
                Models.Add(new ModelRowViewModel(definition ?? new ModelDefinition { Id = status.ModelId, DisplayName = status.DisplayName }, status));
            }

            Processes.Clear();
            foreach (var process in snapshot.Processes)
            {
                Processes.Add(process);
            }

            ExitedProcesses.Clear();
            foreach (var process in snapshot.ExitedProcesses.Take(15))
            {
                ExitedProcesses.Add(process);
            }

            Engines.Clear();
            foreach (var engine in snapshot.Engines)
            {
                Engines.Add(engine);
            }

            Issues.Clear();
            foreach (var issue in snapshot.ValidationIssues.Where(i => i.Severity != ValidationSeverity.Info))
            {
                Issues.Add(issue);
            }

            OnPropertyChanged(nameof(GpuAvailable));
            OnPropertyChanged(nameof(GpuName));
            OnPropertyChanged(nameof(GpuUsedPercent));
            OnPropertyChanged(nameof(GpuUsedText));
            OnPropertyChanged(nameof(GpuUtilization));
            OnPropertyChanged(nameof(GpuTemperature));
            OnPropertyChanged(nameof(CpuPercent));
            OnPropertyChanged(nameof(MemoryText));
            OnPropertyChanged(nameof(LoadedSummary));
        }
        catch (Exception ex)
        {
            SetError($"刷新运行状态失败：{ex.Message}");
        }
    }

    private void CopyJson()
    {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(snapshot, JsonSerialization.Options);
        if (ClipboardHelper.SetText(json))
        {
            SetStatus("运行状态 JSON 已复制到剪贴板。");
        }
    }
}
