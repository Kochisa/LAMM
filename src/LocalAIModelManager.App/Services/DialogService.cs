using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LocalAIModelManager.App.Services;

public interface IDialogService
{
    void ShowInfo(string title, string message);

    void ShowError(string title, string message);

    bool Confirm(string title, string message);

    /// <summary>Single line text prompt. Returns null when the user cancels.</summary>
    string? Prompt(string title, string message, string initialValue = "");
}

/// <summary>
/// WPF dialog implementation. Kept deliberately small: the manager is a tool, not a
/// chat client, and every confirmation must be explicit.
/// </summary>
public sealed class DialogService : IDialogService
{
    public void ShowInfo(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public string? Prompt(string title, string message, string initialValue = "")
    {
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = Application.Current?.MainWindow,
            ShowInTaskbar = false,
        };

        if (Application.Current?.TryFindResource("App.Background") is System.Windows.Media.Brush background)
        {
            window.Background = background;
        }

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        var input = new TextBox { Text = initialValue, MinWidth = 380 };
        panel.Children.Add(input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        string? result = null;

        var ok = new Button { Content = "确定", Width = 88, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) =>
        {
            result = input.Text;
            window.DialogResult = true;
            window.Close();
        };

        var cancel = new Button { Content = "取消", Width = 88, IsCancel = true };
        cancel.Click += (_, _) =>
        {
            window.DialogResult = false;
            window.Close();
        };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        window.Content = panel;
        window.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        window.ShowDialog();
        return result;
    }
}
