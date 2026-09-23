using System.Windows;
using System.Windows.Controls;
using LocalAIModelManager.Core.Backends;
using LocalAIModelManager.Core.Models;

namespace LocalAIModelManager.App.Views.Settings;

/// <summary>Add / edit dialog for an inference engine installation.</summary>
public partial class EngineEditorWindow : Window
{
    private readonly bool _isNew;

    public EngineEditorWindow(EngineDefinition? existing, bool isNew)
    {
        InitializeComponent();
        _isNew = isNew;
        Title = isNew ? Loc.T("editor.engine.title.new") : Loc.T("editor.engine.title.edit", existing?.Name);

        var engine = existing?.Clone() ?? new EngineDefinition
        {
            AdapterKind = BackendAdapterKinds.LlamaCpp,
            Name = "llama.cpp (llama-server)",
        };

        IdBox.Text = engine.Id;
        NameBox.Text = engine.Name;
        ExecutableBox.Text = engine.ExecutablePath;
        WorkingDirectoryBox.Text = engine.WorkingDirectory ?? string.Empty;
        StartupTimeoutBox.Text = engine.StartupTimeoutSeconds.ToString();
        ExtraArgumentsBox.Text = string.Join(
            Environment.NewLine,
            engine.ExtraArguments.Select(kv => kv.Value.Length == 0 ? kv.Key : $"{kv.Key} {kv.Value}"));

        foreach (var item in AdapterBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, engine.AdapterKind, StringComparison.OrdinalIgnoreCase))
            {
                AdapterBox.SelectedItem = item;
                break;
            }
        }

        AdapterBox.SelectedItem ??= AdapterBox.Items[0];
    }

    public EngineDefinition? Result { get; private set; }

    private void OnBrowseExecutable(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("editor.engine.pickExe"),
            Filter = Loc.T("editor.engine.executable") + " (*.exe)|*.exe|"
                     + Loc.T("editor.filter.allFiles") + " (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            ExecutableBox.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
            }

            if (string.IsNullOrWhiteSpace(WorkingDirectoryBox.Text))
            {
                WorkingDirectoryBox.Text = System.IO.Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
            }
        }
    }

    private void OnBrowseWorkingDirectory(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = Loc.T("editor.engine.pickWorkDir"),
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            WorkingDirectoryBox.Text = dialog.SelectedPath;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        var id = IdBox.Text.Trim();
        var executable = ExecutableBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(id))
        {
            errors.Add(Loc.T("editor.engine.error.idRequired"));
        }

        if (string.IsNullOrWhiteSpace(executable))
        {
            errors.Add(Loc.T("editor.engine.error.exeRequired"));
        }
        else if (!File.Exists(executable))
        {
            errors.Add(Loc.T("editor.engine.error.exeMissing", executable));
        }

        if (!int.TryParse(StartupTimeoutBox.Text.Trim(), out var startupTimeout) || startupTimeout <= 0)
        {
            errors.Add(Loc.T("editor.engine.error.timeout"));
            startupTimeout = 180;
        }

        if (errors.Count > 0)
        {
            ErrorText.Text = string.Join(Environment.NewLine, errors);
            return;
        }

        var extraArguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ExtraArgumentsBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            extraArguments[parts[0]] = parts.Length > 1 ? parts[1] : string.Empty;
        }

        var result = new EngineDefinition
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? id : NameBox.Text.Trim(),
            AdapterKind = (AdapterBox.SelectedItem as ComboBoxItem)?.Tag as string ?? BackendAdapterKinds.LlamaCpp,
            ExecutablePath = executable,
            WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectoryBox.Text) ? null : WorkingDirectoryBox.Text.Trim(),
            ExtraArguments = extraArguments,
            StartupTimeoutSeconds = startupTimeout,
        };

        result.Normalize();
        Result = result;
        DialogResult = true;
        Close();
    }
}
