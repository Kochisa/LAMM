using System.Windows.Controls;

namespace LocalAIModelManager.App.Views.Settings;

/// <summary>
/// Generic settings page view. The concrete section comes from the bound
/// <see cref="ViewModels.Settings.SettingsPageViewModel"/>.
/// </summary>
public partial class SettingsPageView : UserControl
{
    public SettingsPageView()
    {
        InitializeComponent();
    }
}
