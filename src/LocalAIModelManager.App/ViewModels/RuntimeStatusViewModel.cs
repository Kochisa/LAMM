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
                SetStatus(Loc.T("status.status.unloadedAll"));
            },
            () => !IsBusy);
    }

    public override string Title => Loc.T("page.runtimeStatus.title");

    public override string Description => Loc.T("page.runtimeStatus.desc");

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

    public string GpuName => _snapshot?.Resources.PrimaryGpu?.Name ?? Loc.T("status.value.gpuNotFound");

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
            return Loc.T(
                "status.value.vramUsageDetail",
                $"{gpu.UsedBytes / GiB:F1}",
                $"{gpu.TotalBytes / GiB:F1}",
                $"{gpu.FreeBytes / GiB:F1}");
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
                : Loc.T("status.value.modelsSummary", snapshot.LoadedModelCount, snapshot.StandbyModelCount, snapshot.Models.Count);
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
            GatewayRows.Add(new StatusRow(Loc.T("status.row.gatewayState"),
                gateway.IsRunning ? Loc.T("status.value.running") : Loc.T("status.value.stopped"),
                gateway.IsRunning ? null : Loc.T("status.value.gatewayStoppedHint"),
                gateway.IsRunning ? "ok" : "error"));
            GatewayRows.Add(new StatusRow(Loc.T("status.row.baseUrl"), gateway.BaseUrl ?? "—",
                gateway.LanAccessEnabled ? Loc.T("status.value.lanAllowed") : Loc.T("status.value.lanLocalOnly")));
            GatewayRows.Add(new StatusRow(Loc.T("status.row.auth"),
                gateway.ApiKeyEnabled ? Loc.T("status.value.authEnabled") : Loc.T("status.value.authDisabled"),
                gateway.ApiKeyEnabled ? Loc.T("status.value.authHeader") : Loc.T("status.value.anyHost"),
                gateway.ApiKeyEnabled ? "ok" : "warn"));
            GatewayRows.Add(new StatusRow(Loc.T("status.row.startedAt"), gateway.StartedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—"));
            GatewayRows.Add(new StatusRow(Loc.T("status.row.totalRequests"), gateway.TotalRequests.ToString()));
            GatewayRows.Add(new StatusRow(Loc.T("status.row.activeRequests"), gateway.ActiveRequests.ToString()));
            GatewayRows.Add(new StatusRow(Loc.T("status.row.configDir"), snapshot.ConfigDirectory ?? "—"));
            GatewayRows.Add(new StatusRow(Loc.T("status.row.logEntries"), snapshot.LogEntryCount.ToString()));

            if (gateway.LastError is { Length: > 0 } lastError)
            {
                GatewayRows.Add(new StatusRow(Loc.T("status.row.lastError"), lastError, null, "error"));
            }

            ResourceRows.Clear();
            ResourceRows.Add(new StatusRow(Loc.T("status.row.gpu"), GpuName, GpuAvailable ? null : snapshot.Resources.GpuError, GpuAvailable ? "ok" : "warn"));
            ResourceRows.Add(new StatusRow(Loc.T("status.row.vram"), GpuUsedText));
            ResourceRows.Add(new StatusRow(Loc.T("status.row.gpuUtil"), GpuAvailable ? $"{GpuUtilization:F0}%" : "—"));
            ResourceRows.Add(new StatusRow(Loc.T("status.row.gpuTemp"), GpuTemperature));
            ResourceRows.Add(new StatusRow(Loc.T("status.row.cpu"), _snapshot?.Resources.SystemCpuPercent is { } cpu ? $"{cpu:F1}%" : "—"));
            ResourceRows.Add(new StatusRow(Loc.T("status.row.memory"), MemoryText));
            ResourceRows.Add(new StatusRow(Loc.T("status.row.modelSummary"), LoadedSummary));

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
            SetError(Loc.T("status.status.refreshFailed", ex.Message));
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
            SetStatus(Loc.T("status.status.jsonCopied"));
        }
    }
}
