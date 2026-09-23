using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using LocalAIModelManager.App.Infrastructure;
using LocalAIModelManager.App.Services;
using LocalAIModelManager.App.Views.Models;
using LocalAIModelManager.Core.Lifecycle;
using LocalAIModelManager.Core.Models;

namespace LocalAIModelManager.App.ViewModels;

public sealed class ModelRowViewModel : ObservableObject
{
    private ModelDefinition _definition;
    private ModelRuntimeStatus? _status;

    public ModelRowViewModel(ModelDefinition definition, ModelRuntimeStatus? status)
    {
        _definition = definition;
        _status = status;
    }

    public ModelDefinition Definition => _definition;

    public string Id => _definition.Id;

    public string DisplayName => _definition.DisplayName;

    public string FilePath => _definition.FilePath;

    public string EngineId => _definition.EngineId;

    public bool Enabled => _definition.Enabled;

    public bool FileExists => _definition.FileExists();

    public ModelState State => _status?.State ?? ModelState.Standby;

    public string StateLabel => Labels.ModelState(State);

    public bool IsLoaded => State == ModelState.Ready;

    public int? Pid => _status?.Pid;

    public int? Port => _status?.Port;

    public int ActiveRequests => _status?.ActiveRequests ?? 0;

    public long TotalRequests => _status?.TotalRequests ?? 0;

    public long? VramBytes => _status?.VramBytes;

    public DateTimeOffset? LastUsedAtUtc => _status?.LastUsedAtUtc;

    public TimeSpan? IdleFor => _status?.IdleFor;

    public string EngineName => _status?.EngineName ?? _definition.EngineId;

    public string LastStopLabel => _status is null ? "—" : Labels.StopMode(_status.LastStopMode);

    /// <summary>Exact engine command line, the ground truth for "is it on the GPU?".</summary>
    public string LaunchCommandLine => _status?.LaunchCommandLine ?? string.Empty;

    public string? LaunchWarning => _status?.LaunchWarning;

    public string? LastError => _status?.LastError;

    public string ParametersSummary
    {
        get
        {
            if (_definition.Parameters.Count == 0)
            {
                return "使用全局默认值";
            }

            return string.Join(", ", _definition.Parameters
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => p.Value.Length == 0 ? p.Key : $"{p.Key}={p.Value}"));
        }
    }

    public void Update(ModelDefinition definition, ModelRuntimeStatus? status)
    {
        _definition = definition;
        _status = status;
        RaiseAllPropertiesChangedPublic();
    }

    private void RaiseAllPropertiesChangedPublic() => OnPropertyChanged(string.Empty);
}

/// <summary>Models page: the model catalog with full lifecycle control.</summary>
public sealed class ModelsViewModel : PageViewModelBase
{
    private readonly DispatcherTimer _timer;
    private ModelRowViewModel? _selected;
    private string? _testOutput;
    private bool _initialized;

    public ModelsViewModel(AppServices services)
        : base(services)
    {
        AddCommand = new AsyncRelayCommand(AddAsync, () => !IsBusy);
        EditCommand = new AsyncRelayCommand(EditAsync, () => Selected is not null && !IsBusy);
        RenameCommand = new AsyncRelayCommand(RenameAsync, () => Selected is not null && !IsBusy);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => Selected is not null && !IsBusy);
        StartCommand = new AsyncRelayCommand(() => StartAsync(), () => Selected is not null && !IsBusy);
        StopCommand = new AsyncRelayCommand(() => StopAsync(), () => Selected is not null && !IsBusy);
        RestartCommand = new AsyncRelayCommand(() => RestartAsync(), () => Selected is not null && !IsBusy);
        TestCommand = new AsyncRelayCommand(() => TestAsync(), () => Selected is not null && !IsBusy);
        AutoTuneCommand = new AsyncRelayCommand(AutoTuneAsync, () => Selected is not null && !IsBusy);
        AutoTuneAllCommand = new AsyncRelayCommand(AutoTuneAllAsync, () => Models.Count > 0 && !IsBusy);
        UnloadAllCommand = new AsyncRelayCommand(UnloadAllAsync, () => !IsBusy);
        RefreshCommand = new RelayCommand(_ => ReloadRows());
        CopyIdCommand = new RelayCommand(_ => CopySelectedId(), _ => Selected is not null);
        OpenFolderCommand = new RelayCommand(_ => OpenSelectedFolder(), _ => Selected is not null);

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _timer.Tick += (_, _) => RefreshStatuses();

        Services.Runtime.Models.Changed += OnRegistryChanged;
    }

    public override string Title => "模型";

    public override string Description =>
        "注册、编辑与运行模型。启动管理器时不会加载任何模型；模型在收到 API 请求时才按需加载。";

    public ObservableCollection<ModelRowViewModel> Models { get; } = new();

    public ModelRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                UpdateCommandStates();
                OnPropertyChanged(nameof(SelectedDetails));
            }
        }
    }

    public string SelectedDetails
    {
        get
        {
            var row = Selected;
            if (row is null)
            {
                return "未选择模型。";
            }

            var lines = new List<string>
            {
                $"模型 ID：{row.Id}",
                $"文件：{row.FilePath}（{(row.FileExists ? "存在" : "缺失")}）",
                $"引擎：{row.EngineName}",
                $"状态：{row.StateLabel}" + (row.IsLoaded ? $"（PID {row.Pid}，内部端口 {row.Port}）" : string.Empty),
                $"参数：{row.ParametersSummary}",
            };

            if (row.LastError is { Length: > 0 } error)
            {
                lines.Add($"最近错误：{error}");
            }

            if (row.LaunchWarning is { Length: > 0 } warning)
            {
                lines.Add(string.Empty);
                lines.Add($"⚠ {warning}");
            }

            if (row.LaunchCommandLine.Length > 0)
            {
                lines.Add(string.Empty);
                lines.Add("实际启动命令行：");
                lines.Add(row.LaunchCommandLine);

                if (!row.LaunchCommandLine.Contains("--n-gpu-layers", StringComparison.OrdinalIgnoreCase) &&
                    !row.LaunchCommandLine.Contains("-ngl", StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add(string.Empty);
                    lines.Add("提示：未设置 GPU 层数，将按引擎自身默认运行（llama.cpp 默认 -ngl 0，即纯 CPU）。"
                              + "要用显卡请在「编辑…」里设置 --n-gpu-layers，或点「自动调参」。");
                }
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    public string? TestOutput
    {
        get => _testOutput;
        private set => SetProperty(ref _testOutput, value);
    }

    public ICommand AutoTuneCommand { get; }

    public ICommand AutoTuneAllCommand { get; }

    public ICommand AddCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand RenameCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand RestartCommand { get; }

    public ICommand TestCommand { get; }

    public ICommand UnloadAllCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand CopyIdCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public override Task InitializeAsync()
    {
        if (!_initialized)
        {
            _initialized = true;
            ReloadRows();
        }

        return Task.CompletedTask;
    }

    public override Task RefreshAsync()
    {
        ReloadRows();
        return Task.CompletedTask;
    }

    protected override void OnActivated() => _timer.Start();

    protected override void OnDeactivated() => _timer.Stop();

    private void OnRegistryChanged() => UiDispatcher.Invoke(ReloadRows);

    private void ReloadRows()
    {
        var statuses = Services.Lifecycle.GetStatuses().ToDictionary(s => s.ModelId, StringComparer.OrdinalIgnoreCase);
        var selectedId = Selected?.Id;

        Models.Clear();
        foreach (var definition in Services.Models.All.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            statuses.TryGetValue(definition.Id, out var status);
            Models.Add(new ModelRowViewModel(definition, status));
        }

        Selected = Models.FirstOrDefault(m => string.Equals(m.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                   ?? Models.FirstOrDefault();

        UpdateCommandStates();
        OnPropertyChanged(nameof(SelectedDetails));
    }

    private void RefreshStatuses()
    {
        var statuses = Services.Lifecycle.GetStatuses().ToDictionary(s => s.ModelId, StringComparer.OrdinalIgnoreCase);
        foreach (var row in Models)
        {
            statuses.TryGetValue(row.Id, out var status);
            row.Update(Services.Models.Get(row.Id) ?? row.Definition, status);
        }

        OnPropertyChanged(nameof(SelectedDetails));
    }

    private void UpdateCommandStates()
    {
        foreach (var command in new ICommand[] { EditCommand, RenameCommand, DeleteCommand, StartCommand, StopCommand, RestartCommand, TestCommand, AutoTuneCommand, AutoTuneAllCommand })
        {
            (command as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        (CopyIdCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task AddAsync()
    {
        if (Services.Engines.Count == 0)
        {
            Services.Dialogs.ShowError("添加模型", "请先在“推理引擎”设置页注册一个推理引擎。");
            return;
        }

        var dialog = new ModelEditorWindow(null, Services) { Owner = System.Windows.Application.Current?.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        try
        {
            var added = Services.Models.Add(dialog.Result);
            SetStatus($"已注册模型 {added.Id}。模型文件未被复制或移动。");
            Services.Notify($"模型 {added.Id} 已注册（处于待机状态，不会自动加载）。");
        }
        catch (InvalidOperationException ex)
        {
            Services.Dialogs.ShowError("添加模型", ex.Message);
            SetError(ex.Message);
        }

        ReloadRows();
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task EditAsync()
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        var dialog = new ModelEditorWindow(row.Definition, Services) { Owner = System.Windows.Application.Current?.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        Services.Models.Update(dialog.Result);
        SetStatus($"已更新模型 {dialog.Result.Id}。");
        ReloadRows();
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task RenameAsync()
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        var displayName = Services.Dialogs.Prompt("重命名模型", "新的显示名称：", row.DisplayName);
        if (displayName is null)
        {
            return;
        }

        var newId = Services.Dialogs.Prompt(
            "重命名模型",
            $"API 中的模型 ID（保持 “{row.Id}” 则只改显示名称）：",
            row.Id);

        if (newId is null)
        {
            return;
        }

        try
        {
            // A loaded model must be unloaded before its id can change safely.
            var wasLoaded = row.IsLoaded;
            if (wasLoaded)
            {
                await Services.Lifecycle.StopAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
            }

            Services.Models.Rename(row.Id, displayName, newId);
            SetStatus($"已重命名模型 {row.Id} → {newId}。");
            ReloadRows();
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message);
            Services.Dialogs.ShowError("重命名模型", ex.Message);
        }
    }

    private async Task DeleteAsync()
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        var confirmed = Services.Dialogs.Confirm(
            "删除模型",
            $"确定从管理器中删除模型“{row.DisplayName}”（{row.Id}）吗？\n\n" +
            "只删除注册信息；磁盘上的模型文件不会被删除。");

        if (!confirmed)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await Services.Lifecycle.StopAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
            Services.Models.Remove(row.Id);
            SetStatus($"已删除模型记录 {row.Id}（模型文件未被删除）。");
            Services.Notify($"已删除模型记录 {row.Id}；文件仍保留在磁盘上。");
            ReloadRows();
        }).ConfigureAwait(true);
    }

    private Task StartAsync() => RunAsync(async () =>
    {
        var row = Selected!;
        await Services.Lifecycle.StartAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
        SetStatus($"模型 {row.Id} 已加载并进入就绪状态。");
        RefreshStatuses();
    }, "正在加载模型…");

    private Task StopAsync() => RunAsync(async () =>
    {
        var row = Selected!;
        await Services.Lifecycle.StopAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
        SetStatus($"模型 {row.Id} 已卸载，显存已释放。");
        RefreshStatuses();
    }, "正在卸载模型…");

    private Task RestartAsync() => RunAsync(async () =>
    {
        var row = Selected!;
        await Services.Lifecycle.RestartAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
        SetStatus($"模型 {row.Id} 已重启。");
        RefreshStatuses();
    }, "正在重启模型…");

    /// <summary>
    /// Reads the model file's own GGUF metadata and combines it with the card's VRAM to
    /// write sensible parameters, so importing a model is one click instead of hand tuning.
    /// </summary>
    private Task AutoTuneAsync() => RunAsync(async () =>
    {
        var row = Selected!;
        var model = Services.Models.Get(row.Id)
                    ?? throw new InvalidOperationException($"模型 {row.Id} 已不存在。");

        var result = await Services.AutoTuneAsync(model.FilePath).ConfigureAwait(true);

        var updated = model.Clone();
        foreach (var (key, value) in result.Parameters)
        {
            updated.Parameters[key] = value;
        }

        Services.Models.Update(updated);

        TestOutput = BuildTuningReport(model, result, wasLoaded: row.IsLoaded);
        SetStatus(result.UsedFallback
            ? $"已为 {model.Id} 写入保守默认参数（元数据或显存信息不足）。"
            : $"已按模型元数据与显存为 {model.Id} 写入参数。"
              + (row.IsLoaded ? " 该模型当前已加载，请点「重启」生效。" : string.Empty));

        ReloadRows();
    }, "正在读取模型元数据并按显存计算参数…");

    private Task AutoTuneAllAsync() => RunAsync(async () =>
    {
        var all = Services.Models.All;
        var lines = new List<string>();
        var changed = 0;

        foreach (var model in all)
        {
            var result = await Services.AutoTuneAsync(model.FilePath).ConfigureAwait(true);
            if (result.UsedFallback)
            {
                lines.Add($"· {model.Id}：跳过（{string.Join(" ", result.Warnings)}）");
                continue;
            }

            var updated = model.Clone();
            foreach (var (key, value) in result.Parameters)
            {
                updated.Parameters[key] = value;
            }

            Services.Models.Update(updated);
            changed++;
            lines.Add($"· {model.Id}：{string.Join("　", result.Explanation.Skip(1))}");
        }

        TestOutput = $"按显存自动调参（{changed}/{all.Count} 个模型已更新）"
                     + Environment.NewLine + Environment.NewLine
                     + string.Join(Environment.NewLine, lines)
                     + Environment.NewLine + Environment.NewLine
                     + "已加载的模型需要点「重启」才会使用新参数。";

        SetStatus($"已自动调参 {changed} 个模型。");
        ReloadRows();
    }, "正在为所有模型读取元数据并计算参数…");

    private static string BuildTuningReport(
        Core.Models.ModelDefinition model,
        Core.Models.AutoTuneResult result,
        bool wasLoaded)
    {
        var lines = new List<string>
        {
            $"自动调参：{model.Id}",
            $"文件：{model.FilePath}",
            string.Empty,
        };

        lines.AddRange(result.Explanation.Select(e => "· " + e));

        if (result.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("注意：");
            lines.AddRange(result.Warnings.Select(w => "! " + w));
        }

        lines.Add(string.Empty);
        lines.Add("已写入模型的参数：");
        lines.AddRange(result.Parameters.Select(p => $"  {p.Key} = {p.Value}"));

        if (wasLoaded)
        {
            lines.Add(string.Empty);
            lines.Add("该模型当前已加载，参数改动需要点「重启」才会生效。");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private Task TestAsync() => RunAsync(async () =>
    {        var row = Selected!;
        var result = await Services.Lifecycle.TestAsync(row.Id, CancellationToken.None).ConfigureAwait(true);

        var lines = new List<string> { $"模型：{row.Id}", $"结论：{result.Summary}", string.Empty };

        foreach (var detail in result.Details)
        {
            lines.Add("· " + detail);
        }

        if (result.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("警告：");
            lines.AddRange(result.Warnings.Select(w => "! " + w));
        }

        if (result.Errors.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("错误：");
            lines.AddRange(result.Errors.Select(e => "× " + e));
        }

        TestOutput = string.Join(Environment.NewLine, lines);
        SetStatus(result.Success ? "测试通过：配置可用。" : "测试未通过，请查看下方输出。");
    }, "正在测试模型配置…");

    private Task UnloadAllAsync() => RunAsync(async () =>
    {
        await Services.Lifecycle.UnloadAllAsync(CancellationToken.None).ConfigureAwait(true);
        SetStatus("已卸载全部模型并释放显存。");
        RefreshStatuses();
    }, "正在卸载全部模型…");

    private void CopySelectedId()
    {
        if (Selected is null)
        {
            return;
        }

        if (ClipboardHelper.SetText(Selected.Id))
        {
            SetStatus($"已复制模型 ID：{Selected.Id}");
        }
    }

    private void OpenSelectedFolder()
    {
        var path = Selected?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    ArgumentList = { "/select,", path },
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            SetError($"无法打开所在目录：{ex.Message}");
        }
    }
}
