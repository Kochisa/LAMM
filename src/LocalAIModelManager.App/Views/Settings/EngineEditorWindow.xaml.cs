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
        Title = isNew ? "添加推理引擎" : $"编辑引擎 - {existing?.Name}";

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
            Title = "选择引擎可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
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
            Description = "选择引擎工作目录",
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
            errors.Add("引擎 ID 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(executable))
        {
            errors.Add("请选择引擎可执行文件。");
        }
        else if (!File.Exists(executable))
        {
            errors.Add($"找不到可执行文件：{executable}");
        }

        if (!int.TryParse(StartupTimeoutBox.Text.Trim(), out var startupTimeout) || startupTimeout <= 0)
        {
            errors.Add("启动超时必须是正整数。");
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
