using System.Collections.ObjectModel;
using System.Windows.Input;
using LocalAIModelManager.App.Infrastructure;
using LocalAIModelManager.App.Services;
using LocalAIModelManager.App.Views.Settings;
using LocalAIModelManager.Core.Backends;
using LocalAIModelManager.Core.Logging;
using LocalAIModelManager.Core.Models;

namespace LocalAIModelManager.App.ViewModels.Settings;

public sealed class EngineRowViewModel : ObservableObject
{
    public EngineRowViewModel(EngineDefinition definition)
    {
        Definition = definition;
    }

    public EngineDefinition Definition { get; }

    public string Id => Definition.Id;

    public string Name => Definition.Name;

    public string AdapterKind => Definition.AdapterKind;

    public string ExecutablePath => Definition.ExecutablePath;

    private bool _isSelected;
    private string _status = "未探测";
    private string _version = "—";
    private int _parameterCount;
    private int _modelCount;
    private bool _executableExists;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Version
    {
        get => _version;
        private set => SetProperty(ref _version, value);
    }

    public int ParameterCount
    {
        get => _parameterCount;
        private set => SetProperty(ref _parameterCount, value);
    }

    public int ModelCount
    {
        get => _modelCount;
        private set => SetProperty(ref _modelCount, value);
    }

    public bool ExecutableExists
    {
        get => _executableExists;
        private set => SetProperty(ref _executableExists, value);
    }

    public string Tags => Definition.Tags.Count == 0 ? "—" : string.Join(", ", Definition.Tags);

    public void Refresh(AppServices services, EngineCapabilities? capabilities)
    {
        ExecutableExists = Definition.ExecutableExists();
        ModelCount = services.ModelCountForEngine(Definition.Id);
        IsSelected = string.Equals(
            services.Current.Engines.SelectedEngineId,
            Definition.Id,
            StringComparison.OrdinalIgnoreCase);

        if (!ExecutableExists)
        {
            Status = "可执行文件缺失";
            Version = "—";
            ParameterCount = 0;
            return;
        }

        if (capabilities is null)
        {
            Status = "未探测";
            return;
        }

        Status = capabilities.IsAvailable ? "可用" : $"探测失败：{capabilities.Error}";
        Version = capabilities.Version ?? "未知";
        ParameterCount = capabilities.Parameters.Count;
    }
}

/// <summary>Inference Engine settings page: register, probe and switch engines.</summary>
public sealed class InferenceEngineViewModel : PageViewModelBase
{
    private EngineRowViewModel? _selected;
    private string? _probeSummary;
    private bool _initialized;

    public InferenceEngineViewModel(AppServices services)
        : base(services)
    {
        AddCommand = new AsyncRelayCommand(AddAsync, () => !IsBusy);
        EditCommand = new AsyncRelayCommand(EditAsync, () => Selected is not null && !IsBusy);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => Selected is not null && !IsBusy);
        ProbeCommand = new AsyncRelayCommand(ProbeAsync, () => Selected is not null && !IsBusy);
        UseCommand = new RelayCommand(_ => UseSelected(), _ => Selected is not null);
    }

    public override string Title => "推理引擎";

    public override string Description => "注册、切换与升级推理引擎。引擎可独立替换，模型记录只引用引擎 ID。";

    public ObservableCollection<EngineRowViewModel> Engines { get; } = new();

    public ObservableCollection<string> SupportedParameters { get; } = new();

    public EngineRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                UpdateCommandStates();
                _ = LoadSupportedParametersAsync();
            }
        }
    }

    public string? ProbeSummary
    {
        get => _probeSummary;
        private set => SetProperty(ref _probeSummary, value);
    }

    public ICommand AddCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand ProbeCommand { get; }

    public ICommand UseCommand { get; }

    public override async Task InitializeAsync()
    {
        if (!_initialized)
        {
            _initialized = true;
            Load();
        }

        await Task.CompletedTask.ConfigureAwait(true);
    }

    public override async Task RefreshAsync()
    {
        Load();
        await LoadSupportedParametersAsync().ConfigureAwait(true);
    }

    private void Load()
    {
        var selectedId = Selected?.Id;
        Engines.Clear();

        foreach (var engine in Services.Engines)
        {
            var row = new EngineRowViewModel(engine.Clone());
            EngineCapabilities? capabilities = null;
            if (engine.ExecutableExists())
            {
                try
                {
                    capabilities = Services.GetCapabilities(engine.Id);
                }
                catch (Exception ex)
                {
                    Services.Logs.Warn("ui", $"引擎能力读取失败 {engine.Id}: {ex.Message}");
                }
            }

            row.Refresh(Services, capabilities);
            Engines.Add(row);
        }

        Selected = Engines.FirstOrDefault(e => string.Equals(e.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                   ?? Engines.FirstOrDefault(e => e.IsSelected)
                   ?? Engines.FirstOrDefault();

        UpdateCommandStates();
    }

    private void UpdateCommandStates()
    {
        (EditCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RemoveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ProbeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (UseCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task LoadSupportedParametersAsync()
    {
        SupportedParameters.Clear();
        ProbeSummary = null;

        var row = Selected;
        if (row is null || !row.ExecutableExists)
        {
            return;
        }

        var capabilities = await Task.Run(() => Services.GetCapabilities(row.Id)).ConfigureAwait(true);
        ProbeSummary = capabilities.IsAvailable
            ? $"版本 {capabilities.Version ?? "未知"}；检测到 {capabilities.Parameters.Count} 个参数，" +
              $"其中 {capabilities.Parameters.Count(p => p.DetectedOnly)} 个为新增（未在内置目录中）。"
            : $"探测失败：{capabilities.Error}";

        foreach (var parameter in capabilities.Parameters.Where(p => !p.DetectedOnly))
        {
            SupportedParameters.Add($"{parameter.Key}  ({parameter.Category})");
        }

        foreach (var parameter in capabilities.Parameters.Where(p => p.DetectedOnly))
        {
            SupportedParameters.Add($"{parameter.Key}  (检测到的新参数)");
        }

        row.Refresh(Services, capabilities);
    }

    private async Task AddAsync()
    {
        var discovered = await Task.Run(() => EngineDiscovery.Discover(Services.ConfigDirectory)).ConfigureAwait(true);
        var suggested = discovered.FirstOrDefault();

        var dialog = new EngineEditorWindow(suggested is null ? null : EngineDiscovery.ToEngineDefinition(suggested), isNew: true)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };

        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        var engine = dialog.Result;
        if (Services.Engines.Any(e => string.Equals(e.Id, engine.Id, StringComparison.OrdinalIgnoreCase)))
        {
            SetError($"引擎 ID “{engine.Id}” 已存在。");
            Services.Dialogs.ShowError("添加引擎", $"引擎 ID “{engine.Id}” 已存在，请换一个 ID。");
            return;
        }

        Services.SaveSettings(s => s.Engines.Engines.Add(engine));
        Services.InvalidateCapabilities(engine.Id);
        SetStatus($"已添加引擎 {engine.Id}。");
        Load();
    }

    private async Task EditAsync()
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        var dialog = new EngineEditorWindow(row.Definition, isNew: false)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };

        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        var updated = dialog.Result;
        if (!string.Equals(updated.Id, row.Id, StringComparison.OrdinalIgnoreCase) &&
            Services.Engines.Any(e => string.Equals(e.Id, updated.Id, StringComparison.OrdinalIgnoreCase)))
        {
            Services.Dialogs.ShowError("编辑引擎", $"引擎 ID “{updated.Id}” 已存在。");
            return;
        }

        Services.SaveSettings(s =>
        {
            var index = s.Engines.Engines.FindIndex(e => string.Equals(e.Id, row.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                s.Engines.Engines[index] = updated;
            }

            if (string.Equals(s.Engines.SelectedEngineId, row.Id, StringComparison.OrdinalIgnoreCase))
            {
                s.Engines.SelectedEngineId = updated.Id;
            }
        });

        // Models follow an engine rename so no model ends up orphaned.
        if (!string.Equals(updated.Id, row.Id, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var model in Services.Models.All.Where(m => string.Equals(m.EngineId, row.Id, StringComparison.OrdinalIgnoreCase)))
            {
                var moved = model.Clone();
                moved.EngineId = updated.Id;
                Services.Models.Update(moved);
            }
        }

        Services.InvalidateCapabilities(row.Id);
        Services.InvalidateCapabilities(updated.Id);
        SetStatus($"已更新引擎 {updated.Id}。");
        Load();
    }

    private async Task RemoveAsync()
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        var attached = Services.ModelCountForEngine(row.Id);
        var message = attached > 0
            ? $"引擎“{row.Name}”仍被 {attached} 个模型引用。删除前请先把这些模型改指到别的引擎。"
            : $"确定删除引擎“{row.Name}”吗？这只会移除注册信息，不会删除磁盘上的任何文件。";

        if (attached > 0)
        {
            Services.Dialogs.ShowError("删除引擎", message);
            return;
        }

        if (!Services.Dialogs.Confirm("删除引擎", message))
        {
            return;
        }

        Services.SaveSettings(s =>
        {
            s.Engines.Engines.RemoveAll(e => string.Equals(e.Id, row.Id, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(s.Engines.SelectedEngineId, row.Id, StringComparison.OrdinalIgnoreCase))
            {
                s.Engines.SelectedEngineId = s.Engines.Engines.FirstOrDefault()?.Id ?? string.Empty;
            }
        });

        Services.InvalidateCapabilities(row.Id);
        SetStatus($"已删除引擎 {row.Id}（磁盘文件未被删除）。");
        Load();
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task ProbeAsync()
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        if (!row.ExecutableExists)
        {
            SetError($"可执行文件不存在：{row.ExecutablePath}");
            return;
        }

        await RunAsync(async () =>
        {
            var capabilities = await Services.RefreshCapabilitiesAsync(row.Id).ConfigureAwait(true);
            await LoadSupportedParametersAsync().ConfigureAwait(true);
            SetStatus(capabilities.IsAvailable
                ? $"探测完成：{capabilities.Parameters.Count} 个参数可用。"
                : $"探测失败：{capabilities.Error}");
        }, "正在探测引擎能力（仅执行 --help，不会加载模型）…").ConfigureAwait(true);
    }

    private void UseSelected()
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        Services.SaveSettings(s => s.Engines.SelectedEngineId = row.Id);
        SetStatus($"默认引擎已切换为 {row.Id}。新建模型将默认使用该引擎。");
        Load();
    }
}
