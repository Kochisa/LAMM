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
        Title = _isNew ? Loc.T("editor.model.title.new") : Loc.T("editor.model.title.edit", _model.DisplayName);

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

            // A model only carries what was explicitly set on it. Nothing is inherited
            // from global defaults here and nothing is pre-filled: an imported model must
            // start with zero custom inference parameters.
            _model.Parameters.TryGetValue(key, out var value);
            rows.Add(ParameterRowFactory.Create(descriptor, capabilities, value));
        }

        if (rows.Count > 0)
        {
            Groups.Add(new ParameterGroupViewModel(Loc.T("params.section.common"), rows));
        }

        // MTP / speculative decoding is written out as its own group: these flags differ
        // between builds, and they are the ones people reach for on a modern llama.cpp.
        var speculativeRows = ParameterCatalog.SpeculativeFor(capabilities)
            .Select(descriptor =>
            {
                _model.Parameters.TryGetValue(descriptor.Key, out var value);
                return ParameterRowFactory.Create(descriptor, capabilities, value);
            })
            .ToList();

        if (speculativeRows.Count > 0)
        {
            Groups.Add(new ParameterGroupViewModel(Loc.T("params.section.speculative"), speculativeRows));
        }

        AdditionalArgumentsText = string.Join(Environment.NewLine, _model.AdditionalArguments);

        var globals = _services.Current.ModelParameters.Defaults;
        GlobalHint.Text = globals.Count == 0
            ? Loc.T("editor.model.globalsNone")
            : Loc.T("editor.model.globalsPresent", string.Join("，", globals.Select(g => $"{g.Key}={g.Value}")));
        GlobalHint.Visibility = System.Windows.Visibility.Visible;
    }

    private async void OnBrowseExecutable(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("editor.engine.pickModel"),
            Filter = Loc.T("editor.filter.gguf") + " (*.gguf)|*.gguf|"
                     + Loc.T("editor.filter.allFiles") + " (*.*)|*.*",
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
            // user can press the auto-tune button if they want suggestions.
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
                AutoTuneSummary.Text = Loc.T("editor.model.metadataNone");
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
                parts.Add(Loc.T("autotune.meta.layers", layers));
            }

            if (metadata.EffectiveHeadCountKv is { } kvHeads)
            {
                parts.Add(Loc.T("autotune.meta.kvHeads", kvHeads));
            }

            if (metadata.ContextLength is { } trained)
            {
                parts.Add(Loc.T("autotune.meta.trainedContext", trained));
            }

            parts.Add(Loc.T("autotune.meta.weights", (metadata.FileSizeBytes / (1024.0 * 1024 * 1024)).ToString("F2")));

            var kvLine = metadata.KvBytesPerToken() is { } kv
                ? Loc.T("editor.model.metadataKv", (kv / 1024.0).ToString("F1"))
                : string.Empty;

            AutoTuneSummary.Text = Loc.T(
                "editor.model.metadata",
                string.Join(" · ", parts),
                kvLine + Environment.NewLine + Loc.T("editor.model.metadataNotSet"));
            AutoTuneSummary.Visibility = System.Windows.Visibility.Visible;
        }
        catch (Exception ex)
        {
            AutoTuneSummary.Text = Loc.T("editor.model.metadataFailed", ex.Message);
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
            AutoTuneSummary.Text = Loc.T("editor.model.needFileFirst");
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
            AutoTuneSummary.Text = Loc.T("editor.model.autotuneFailed", ex.Message);
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
                errors.Add(Loc.T("editor.model.error.idRequired"));
            }
            else if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9._-]{1,96}$"))
            {
                errors.Add(Loc.T("editor.model.error.idCharset"));
            }
            else if (_services.Models.Exists(id))
            {
                errors.Add(Loc.T("editor.model.error.idTaken", id));
            }
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            errors.Add(Loc.T("editor.model.error.fileRequired"));
        }
        else if (!File.Exists(filePath))
        {
            errors.Add(Loc.T("editor.model.error.fileMissing", filePath));
        }

        if (EngineBox.SelectedItem is null)
        {
            errors.Add(Loc.T("editor.model.error.engineRequired"));
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
