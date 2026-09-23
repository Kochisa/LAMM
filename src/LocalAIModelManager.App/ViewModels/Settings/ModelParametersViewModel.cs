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
    private string _additionalArgumentsText = string.Empty;
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
        "只暴露最常用的几项；其余 llama.cpp 参数写在“其他参数”里，一行一个。"
        + "未勾选/留空的参数不会传给引擎——VRAM 由模型和引擎自身的默认值决定，而不是由本应用替你决定。";

    public ObservableCollection<EngineOption> EngineOptions { get; } = new();

    public ObservableCollection<ParameterGroupViewModel> Groups { get; } = new();

    /// <summary>Free-form advanced arguments, one CLI line per entry.</summary>
    public string AdditionalArgumentsText
    {
        get => _additionalArgumentsText;
        set => SetProperty(ref _additionalArgumentsText, value);
    }

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

        // Only the handful of parameters that people actually change are exposed as
        // controls. Everything else is reachable through the free-form "Other" box, so
        // the page stays readable instead of listing dozens of obscure flags.
        var rows = new List<ParameterRowViewModel>();
        var unsupported = new List<string>();

        foreach (var key in EssentialParameters.Ordered)
        {
            var descriptor = capabilities.Find(key) ?? ParameterCatalog.Find(key);
            if (descriptor is null)
            {
                continue;
            }

            var supported = capabilities.Supports(key);
            if (!supported)
            {
                unsupported.Add(key);
            }

            var row = new ParameterRowViewModel(descriptor, supported, descriptor.DefaultValue);

            // Unset stays unset: a parameter that was never configured is shown switched
            // off with an empty value, and is NOT passed to the engine.
            if (defaults.TryGetValue(key, out var configured))
            {
                row.IsEnabled = true;
                row.Value = configured;
            }
            else
            {
                row.IsEnabled = false;
                row.Value = descriptor.Kind == ParameterKind.Boolean ? "false" : string.Empty;
            }

            rows.Add(row);
        }

        if (rows.Count > 0)
        {
            Groups.Add(new ParameterGroupViewModel("常用参数", rows));
        }

        AdditionalArgumentsText = string.Join(
            Environment.NewLine,
            Services.Current.ModelParameters.AdditionalArguments);

        Summary = capabilities.IsAvailable
            ? $"引擎 {engineDefinition.Id}（版本 {capabilities.Version ?? "未知"}）。"
              + $"仅以下 {rows.Count} 项会作为控件显示；其他参数请写在“其他参数”里。"
              + (unsupported.Count > 0 ? $" 该构建不支持：{string.Join("、", unsupported)}。" : string.Empty)
            : $"引擎能力不可用：{capabilities.Error}";

        SetStatus(string.Empty);
    }

    private async Task SaveAsync()
    {
        var rows = Groups.SelectMany(g => g.Rows).ToList();
        var invalid = rows.Where(r => r.IsEnabled && r.IsText && string.IsNullOrWhiteSpace(r.Value)).ToList();
        if (invalid.Count > 0)
        {
            SetError("以下参数已勾选但没有填写值：" + string.Join("、", invalid.Select(r => r.Key)));
            return;
        }

        var additional = AdditionalArgumentsText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        Services.SaveSettings(settings =>
        {
            // Rebuild from scratch: whatever is not switched on is simply not sent.
            settings.ModelParameters.Defaults.Clear();
            foreach (var row in rows.Where(r => r.IsEnabled && r.Supported))
            {
                settings.ModelParameters.Defaults[row.Key] = row.Value;
            }

            settings.ModelParameters.AdditionalArguments = additional;
        });

        var count = rows.Count(r => r.IsEnabled && r.Supported);
        SetStatus($"已保存 {count} 个默认参数，另有 {additional.Count} 行自定义参数。未勾选的参数不会传给引擎。");
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
