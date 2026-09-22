using System.Collections.ObjectModel;
using LocalAIModelManager.App.Services;
using LocalAIModelManager.App.ViewModels.Settings;
using LocalAIModelManager.Core.Backends;
using LocalAIModelManager.Core.Models;

namespace LocalAIModelManager.App.Views.Models;

/// <summary>
/// Add / edit dialog for a model entry. The parameter rows come from the selected
/// engine's capability probe, so the dialog only ever offers flags the installed
/// engine actually understands.
/// </summary>
public partial class ModelEditorWindow : System.Windows.Window
{
    private readonly AppServices _services;
    private readonly ModelDefinition _model;
    private readonly bool _isNew;

    public ModelEditorWindow(ModelDefinition? existing, AppServices services)
    {
        _services = services;
        _isNew = existing is null;
        _model = existing?.Clone() ?? new ModelDefinition
        {
            EngineId = services.Current.Engines.SelectedEngineId,
        };

        InitializeComponent();
        DataContext = this;
        Title = _isNew ? "添加模型" : $"编辑模型 - {_model.DisplayName}";

        IdBox.Text = _model.Id;
        DisplayNameBox.Text = _model.DisplayName;
        FilePathBox.Text = _model.FilePath;
        NotesBox.Text = _model.Notes ?? string.Empty;
        EnabledBox.IsChecked = _model.Enabled;
        IdBox.IsEnabled = _isNew;
        IsNewInfo.Visibility = _isNew ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        foreach (var engine in services.Engines)
        {
            EngineOptions.Add(new EngineOption(engine.Id, $"{engine.Name}（{engine.Id}）"));
        }

        EngineBox.SelectedItem = EngineOptions.FirstOrDefault(o => string.Equals(o.Id, _model.EngineId, StringComparison.OrdinalIgnoreCase))
                                 ?? EngineOptions.FirstOrDefault();

        RebuildParameters();
    }

    public ObservableCollection<EngineOption> EngineOptions { get; } = new();

    public ObservableCollection<ParameterGroupViewModel> Groups { get; } = new();

    public ModelDefinition? Result { get; private set; }

    private void OnEngineChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RebuildParameters();

    private void RebuildParameters()
    {
        Groups.Clear();
        var engineId = (EngineBox.SelectedItem as EngineOption)?.Id;
        if (engineId is null)
        {
            return;
        }

        var capabilities = _services.GetCapabilities(engineId);
        var globalDefaults = _services.Current.ModelParameters.Defaults;

        foreach (var category in ParameterCategories.Ordered)
        {
            var rows = new List<ParameterRowViewModel>();

            foreach (var descriptor in capabilities.ByCategory(category))
            {
                var supported = capabilities.Supports(descriptor.Key);
                var row = new ParameterRowViewModel(descriptor, supported, descriptor.DefaultValue);

                // Model override wins; otherwise inherit the global default so the
                // dialog shows exactly what will be applied (e.g. GPU offload).
                if (_model.Parameters.TryGetValue(descriptor.Key, out var value))
                {
                    row.IsEnabled = true;
                    row.Value = value;
                }
                else if (globalDefaults.TryGetValue(descriptor.Key, out var globalValue))
                {
                    row.IsEnabled = true;
                    row.Value = globalValue;
                }
                else if (!string.IsNullOrEmpty(descriptor.DefaultValue) && descriptor.Kind != ParameterKind.Boolean)
                {
                    row.Value = descriptor.DefaultValue!;
                }

                rows.Add(row);
            }

            if (rows.Count > 0)
            {
                Groups.Add(new ParameterGroupViewModel(category, rows));
            }
        }
    }

    private void OnBrowseExecutable(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择模型文件",
            Filter = "GGUF 模型 (*.gguf)|*.gguf|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            FilePathBox.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(DisplayNameBox.Text))
            {
                DisplayNameBox.Text = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
            }

            if (string.IsNullOrWhiteSpace(IdBox.Text))
            {
                var candidate = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName).ToLowerInvariant();
                IdBox.Text = new string(candidate.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
            }
        }
    }

    private void OnCancel(object sender, System.Windows.RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnConfirm(object sender, System.Windows.RoutedEventArgs e)
    {
        var errors = new List<string>();
        var id = IdBox.Text.Trim();
        var filePath = FilePathBox.Text.Trim();

        if (_isNew)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                errors.Add("模型 ID 不能为空。");
            }
            else if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9._-]{1,96}$"))
            {
                errors.Add("模型 ID 只能包含字母、数字、点、短横线和下划线。");
            }
            else if (_services.Models.Exists(id))
            {
                errors.Add($"模型 ID “{id}” 已存在。");
            }
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            errors.Add("请选择模型文件。");
        }
        else if (!File.Exists(filePath))
        {
            errors.Add($"模型文件不存在：{filePath}");
        }

        if (EngineBox.SelectedItem is null)
        {
            errors.Add("请选择推理引擎。");
        }

        if (errors.Count > 0)
        {
            ErrorText.Text = string.Join(Environment.NewLine, errors);
            return;
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Groups.SelectMany(g => g.Rows))
        {
            if (row.IsEnabled && row.Supported)
            {
                parameters[row.Key] = row.Value;
            }
        }

        var model = _model.Clone();
        model.Id = _isNew ? id : _model.Id;
        model.DisplayName = string.IsNullOrWhiteSpace(DisplayNameBox.Text) ? model.Id : DisplayNameBox.Text.Trim();
        model.FilePath = filePath;
        model.EngineId = (EngineBox.SelectedItem as EngineOption)!.Id;
        model.Enabled = EnabledBox.IsChecked == true;
        model.Notes = string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim();
        model.Parameters = parameters;
        model.AutoLoad = false;
        model.Normalize();

        Result = model;
        DialogResult = true;
        Close();
    }
}
