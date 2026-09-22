using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using LocalAIModelManager.App.Services;
using LocalAIModelManager.App.ViewModels;

namespace LocalAIModelManager.App;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _chromeTimer;
    private bool _allowClose;

    public MainWindow(ShellViewModel shell, AppServices services)
    {
        InitializeComponent();
        _services = services;
        DataContext = shell;
        Shell = shell;

        _chromeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _chromeTimer.Tick += (_, _) => Shell.RefreshChrome();
        _chromeTimer.Start();
    }

    public ShellViewModel Shell { get; }

    /// <summary>Set by the application when the user really wants to exit.</summary>
    public void CloseForReal()
    {
        _allowClose = true;
        _chromeTimer.Stop();
        Close();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        if (WindowState == WindowState.Minimized && _services.Current.General.MinimizeToTray)
        {
            Hide();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose && _services.Current.General.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
