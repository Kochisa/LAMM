using System.Collections.ObjectModel;
using System.Windows.Input;
using LocalAIModelManager.App.Infrastructure;
using LocalAIModelManager.App.Services;
using LocalAIModelManager.Core.Backends;
using LocalAIModelManager.Core.Models;

namespace LocalAIModelManager.App.ViewModels.Settings;

public sealed record EngineOption(string Id, string Display);

public sealed class ParameterRowViewModel : ObservableObject
{
    private bool _isEnabled;
    private string _value = string.Empty;

    public ParameterRowViewModel(ParameterDescriptor descriptor, bool supported, string? engineDefault)
    {
        Descriptor = descriptor;
        Supported = supported;
        EngineDefault = engineDefault;
    }

    public ParameterDescriptor Descriptor { get; }

    /// <summary>True when the installed engine build advertises this flag.</summary>
    public bool Supported { get; }

    public string? EngineDefault { get; }

    public string Key => Descriptor.Key;

    public string DisplayName => Descriptor.DisplayName;

    public string Category => Descriptor.Category;

    public string? Help => Descriptor.Description;

    public string KindLabel => Labels.ParameterKindLabel(Descriptor.Kind);

    public string SupportLabel => Descriptor.DetectedOnly
        ? "该引擎新增参数"
        : Supported ? "支持" : "该引擎不支持";

    public bool IsBoolean => Descriptor.Kind == ParameterKind.Boolean;

    public bool IsChoice => Descriptor.Kind == ParameterKind.Enum;

    public bool IsText => !IsBoolean && !IsChoice;

    public IReadOnlyList<string> Choices => Descriptor.AllowedValues;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(IsEditable));
            }
        }
    }

    public bool IsEditable => IsEnabled && Supported;

    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                OnPropertyChanged(nameof(BoolValue));
                OnPropertyChanged(nameof(SelectionValue));
            }
        }
    }

    public bool BoolValue
    {
        get => string.Equals(_value, "true", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(_value);
        set => Value = value ? "true" : "false";
    }

    public string? SelectionValue
    {
        get => string.IsNullOrEmpty(_value) ? null : _value;
        set
        {
            if (!string.IsNullOrEmpty(value))
            {
                Value = value;
            }
        }
    }
}

public sealed class ParameterGroupViewModel
{
    public ParameterGroupViewModel(string name, IEnumerable<ParameterRowViewModel> rows)
    {
        Name = name;
        Rows = new ObservableCollection<ParameterRowViewModel>(rows);
    }

    public string Name { get; }

    public ObservableCollection<ParameterRowViewModel> Rows { get; }

    public string CountLabel => $"{Rows.Count} 项";
}

/// <summary>
/// Model Parameters settings page. It edits the application wide parameter defaults.
/// Which parameters exist is decided by the installed engine's --help output, never
/// by a hard-coded list, so upgrading llama.cpp immediately exposes new switches.
/// </summary>
public sealed class ModelParametersViewModel : PageViewModelBase
{
    private EngineOption? _selectedEngine;
    private string _summary = string.Empty;
    private bool _initialized;

    public ModelParametersViewModel(AppServices services)
        : base(services)
    {
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        ReloadCommand = new RelayCommand(_ => Load());
        ProbeCommand = new AsyncRelayCommand(ProbeAsync, () => !IsBusy && SelectedEngine is not null);
    }

    public override string Title => "模型参数";

    public override string Description =>
        "参数的可见项由所安装引擎的 --help 输出决定，升级 llama.cpp 后会自动出现新参数。这里设置的是全局默认值，单个模型可以覆盖。";

    public ObservableCollection<EngineOption> EngineOptions { get; } = new();

    public ObservableCollection<ParameterGroupViewModel> Groups { get; } = new();

    public EngineOption? SelectedEngine
    {
        get => _selectedEngine;
        set
        {
            if (SetProperty(ref _selectedEngine, value))
            {
                Load();
            }
        }
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public ICommand SaveCommand { get; }

    public ICommand ReloadCommand { get; }

    public ICommand ProbeCommand { get; }

    public override Task InitializeAsync()
    {
        if (!_initialized)
        {
            _initialized = true;
            Load();
        }

        return Task.CompletedTask;
    }

    public override Task RefreshAsync()
    {
        Load();
        return Task.CompletedTask;
    }

    private void Load()
    {
        var selectedId = _selectedEngine?.Id ?? Services.Current.Engines.SelectedEngineId;
        EngineOptions.Clear();

        foreach (var engine in Services.Engines)
        {
            EngineOptions.Add(new EngineOption(engine.Id, $"{engine.Name}（{engine.Id}）"));
        }

        _selectedEngine = EngineOptions.FirstOrDefault(o => string.Equals(o.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                          ?? EngineOptions.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedEngine));

        Groups.Clear();

        var engineDefinition = Services.FindEngine(_selectedEngine?.Id);
        if (engineDefinition is null)
        {
            Summary = "尚未配置任何推理引擎。请先在“推理引擎”页面添加一个 llama-server。";
            return;
        }

        var capabilities = Services.GetCapabilities(engineDefinition.Id);
        var defaults = Services.Current.ModelParameters.Defaults;
        var rows = new List<ParameterRowViewModel>();

        foreach (var descriptor in capabilities.Parameters)
        {
            var supported = capabilities.Supports(descriptor.Key);
            var engineDefault = descriptor.DefaultValue;
            var row = new ParameterRowViewModel(descriptor, supported, engineDefault);

            if (defaults.TryGetValue(descriptor.Key, out var configured))
            {
                row.IsEnabled = true;
                row.Value = configured;
            }
            else
            {
                // Not explicitly configured: shown as a hint value, switched off.
                row.IsEnabled = false;
                row.Value = descriptor.Kind == ParameterKind.Boolean
                    ? "false"
                    : engineDefault ?? string.Empty;
            }

            rows.Add(row);
        }

        // Catalog entries the engine does not advertise are still shown, greyed out,
        // so the user can see exactly what this build is missing.
        var advertised = capabilities.Parameters.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in ParameterCatalog.Known.Where(k => !advertised.Contains(k.Key)))
        {
            var row = new ParameterRowViewModel(descriptor, supported: false, descriptor.DefaultValue) { Value = descriptor.DefaultValue ?? string.Empty };
            rows.Add(row);
        }

        foreach (var category in ParameterCategories.Ordered)
        {
            var categoryRows = rows
                .Where(r => string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Descriptor.DetectedOnly ? 1 : 0)
                .ThenBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (categoryRows.Count > 0)
            {
                Groups.Add(new ParameterGroupViewModel(category, categoryRows));
            }
        }

        var supportedCount = capabilities.Parameters.Count;
        Summary = capabilities.IsAvailable
            ? $"引擎 {engineDefinition.Id}（版本 {capabilities.Version ?? "未知"}）：可用参数 {supportedCount} 个，" +
              $"未支持 {rows.Count - supportedCount} 个。"
            : $"引擎能力不可用：{capabilities.Error}";

        SetStatus(string.Empty);
    }

    private async Task SaveAsync()
    {
        var rows = Groups.SelectMany(g => g.Rows).ToList();
        var invalid = rows.Where(r => r.IsEnabled && r.IsText && string.IsNullOrWhiteSpace(r.Value)).ToList();
        if (invalid.Count > 0)
        {
            SetError("以下参数已启用但没有填写值：" + string.Join("、", invalid.Select(r => r.Key)));
            return;
        }

        Services.SaveSettings(settings =>
        {
            settings.ModelParameters.Defaults.Clear();
            foreach (var row in rows.Where(r => r.IsEnabled && r.Supported))
            {
                settings.ModelParameters.Defaults[row.Key] = row.Value;
            }
        });

        SetStatus($"已保存 {rows.Count(r => r.IsEnabled && r.Supported)} 个默认参数。");
        Services.Notify("模型参数默认值已保存。");
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task ProbeAsync()
    {
        var engine = SelectedEngine;
        if (engine is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await Services.RefreshCapabilitiesAsync(engine.Id).ConfigureAwait(true);
            Load();
            SetStatus("已按当前安装的引擎重新探测参数集合。");
        }, "正在探测引擎参数…").ConfigureAwait(true);
    }
}
