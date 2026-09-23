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

    /// <summary>Advanced engine arguments typed by hand, one CLI line per entry.</summary>
    public string AdditionalArgumentsText { get; set; } = string.Empty;

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
        var rows = new List<ParameterRowViewModel>();

        // Only the common parameters get a control. Obscure llama.cpp flags belong in the
        // "Other" box, which keeps this dialog readable.
        foreach (var key in EssentialParameters.Ordered)
        {
            var descriptor = capabilities.Find(key) ?? ParameterCatalog.Find(key);
            if (descriptor is null)
            {
                continue;
            }

            var supported = capabilities.Supports(key);
            var row = new ParameterRowViewModel(descriptor, supported, descriptor.DefaultValue);

            // A model only carries what was explicitly set on it. Nothing is inherited
            // from global defaults here and nothing is pre-filled: an imported model must
            // start with zero custom inference parameters.
            if (_model.Parameters.TryGetValue(key, out var value))
            {
                row.IsEnabled = true;
                row.Value = value;
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

        AdditionalArgumentsText = string.Join(Environment.NewLine, _model.AdditionalArguments);

        var globals = _services.Current.ModelParameters.Defaults;
        GlobalHint.Text = globals.Count == 0
            ? "未设置任何参数：引擎按自身默认值运行（llama.cpp 默认 -ngl 0，即纯 CPU）。"
            : "全局默认值也会生效：" + string.Join("，", globals.Select(g => $"{g.Key}={g.Value}"));
        GlobalHint.Visibility = System.Windows.Visibility.Visible;
    }

    private async void OnBrowseExecutable(object sender, System.Windows.RoutedEventArgs e)
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

            // Read the model's own metadata and show it, but do NOT set any parameter:
            // an imported model must start with zero custom inference parameters. The
            // user can press 按显存自动计算 if they want suggestions.
            await ShowMetadataAsync(dialog.FileName).ConfigureAwait(true);
        }
    }

    private async Task ShowMetadataAsync(string path)
    {
        try
        {
            var metadata = await Task.Run(() => Core.Models.GgufMetadataReader.TryRead(path)).ConfigureAwait(true);
            if (metadata is null)
            {
                AutoTuneSummary.Text = "无法读取该文件的 GGUF 元数据（不是 GGUF，或文件不完整）。参数保持为空。";
                AutoTuneSummary.Visibility = System.Windows.Visibility.Visible;
                return;
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(metadata.Architecture))
            {
                parts.Add(metadata.Architecture!);
            }

            if (metadata.BlockCount is { } layers)
            {
                parts.Add($"{layers} 层");
            }

            if (metadata.EffectiveHeadCountKv is { } kvHeads)
            {
                parts.Add($"{kvHeads} 个 KV 头");
            }

            if (metadata.ContextLength is { } trained)
            {
                parts.Add($"训练上下文 {trained}");
            }

            parts.Add($"权重 {metadata.FileSizeBytes / (1024.0 * 1024 * 1024):F2} GiB");

            var kvLine = metadata.KvBytesPerToken() is { } kv
                ? $" KV cache 约 {kv / 1024.0:F1} KB/token（未设 --ctx-size 时按训练上下文一次性预留）。"
                : string.Empty;

            AutoTuneSummary.Text =
                "模型元数据：" + string.Join(" · ", parts) + "。" + kvLine
                + Environment.NewLine + "未设置任何参数（未勾选 = 不下发）。需要建议值请点“按显存自动计算”。";
            AutoTuneSummary.Visibility = System.Windows.Visibility.Visible;
        }
        catch (Exception ex)
        {
            AutoTuneSummary.Text = $"读取模型元数据失败：{ex.Message}";
            AutoTuneSummary.Visibility = System.Windows.Visibility.Visible;
        }
    }

    private async void OnAutoTune(object sender, System.Windows.RoutedEventArgs e) =>
        await ApplyAutoTuneAsync().ConfigureAwait(true);

    private async Task ApplyAutoTuneAsync()
    {
        var path = FilePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            AutoTuneSummary.Text = "请先选择存在的模型文件，然后才能自动计算参数。";
            AutoTuneSummary.Visibility = System.Windows.Visibility.Visible;
            return;
        }

        AutoTuneButton.IsEnabled = false;
        try
        {
            var result = await _services.AutoTuneAsync(path).ConfigureAwait(true);

            foreach (var row in Groups.SelectMany(g => g.Rows))
            {
                if (result.Parameters.TryGetValue(row.Key, out var value))
                {
                    row.IsEnabled = row.Supported;
                    row.Value = value;
                }
            }

            var lines = new List<string>(result.Explanation);
            if (result.Warnings.Count > 0)
            {
                lines.AddRange(result.Warnings.Select(w => "⚠ " + w));
            }

            AutoTuneSummary.Text = string.Join(Environment.NewLine, lines);
            AutoTuneSummary.Visibility = System.Windows.Visibility.Visible;
        }
        catch (Exception ex)
        {
            AutoTuneSummary.Text = $"自动调参失败：{ex.Message}";
            AutoTuneSummary.Visibility = System.Windows.Visibility.Visible;
        }
        finally
        {
            AutoTuneButton.IsEnabled = true;
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
            if (row.IsEnabled && row.Supported && !string.IsNullOrWhiteSpace(row.Value))
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
        model.AdditionalArguments = (AdditionalArgumentsText ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        model.AutoLoad = false;
        model.Normalize();

        Result = model;
        DialogResult = true;
        Close();
    }
}
