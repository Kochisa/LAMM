using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LocalAIModelManager.App.Infrastructure;
using LocalAIModelManager.App.Services;
using LocalAIModelManager.App.ViewModels;
using LocalAIModelManager.Core.Logging;
using LocalAIModelManager.Core.Runtime;

namespace LocalAIModelManager.App;

/// <summary>
/// Application entry point. Startup ordering matters and is deliberate:
/// 1) configuration and core services come up,
/// 2) the API gateway starts listening,
/// 3) every model stays in STANDBY - nothing is loaded until a request arrives.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\LocalAIModelManager.SingleInstance";

    private Mutex? _singleInstanceMutex;
    private AppRuntime? _runtime;
    private AppServices? _services;
    private ShellViewModel? _shell;
    private TrayIconService? _tray;
    private MainWindow? _window;
    private bool _shuttingDown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UiDispatcher.Initialize(Dispatcher);

        var startMinimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        var startedByWindows = e.Args.Any(a => string.Equals(a, "--startup", StringComparison.OrdinalIgnoreCase));

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show(
                "Local AI Model Manager 已经在运行。请查看系统托盘图标。",
                "Local AI Model Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            TryLog("fatal", "未处理异常", args.ExceptionObject as Exception);
        };

        try
        {
            _runtime = AppRuntime.Create(new AppRuntimeOptions
            {
                StartGateway = true,
                StartResourceMonitor = true,
                StartIdleMonitor = true,
                SeedDefaultEngines = true,
            });

            ThemeManager.Apply(_runtime.Settings.Current.General.Theme);

            // Manager only: gateway + monitors. No model is loaded here.
            await _runtime.StartAsync().ConfigureAwait(true);

            _services = new AppServices(_runtime, new DialogService());
            _shell = new ShellViewModel(_services);
            _shell.WindowShown += ShowMainWindow;
            _shell.WindowHidden += HideMainWindow;
            _shell.ExitRequested += ExitApplication;

            _window = new MainWindow(_shell, _services);
            MainWindow = _window;

            // ShutdownMode is OnExplicitShutdown so that "close to tray" keeps the
            // manager (and its gateway) alive. Closing for real must therefore end
            // the application explicitly.
            _window.Closed += (_, _) =>
            {
                if (_shuttingDown || _services.Current.General.CloseToTray)
                {
                    return;
                }

                _shuttingDown = true;
                Shutdown(0);
            };

            _tray = new TrayIconService(_services, _shell);

            _services.Notification += message =>
            {
                if (message.Contains("失败", StringComparison.Ordinal) || message.Contains("错误", StringComparison.Ordinal))
                {
                    _tray?.ShowBalloon("Local AI Model Manager", message);
                }
            };

            await _shell.InitializeAsync().ConfigureAwait(true);

            var minimize = startMinimized || _runtime.Settings.Current.General.StartMinimized;
            if (minimize)
            {
                _window.Hide();
                _tray.ShowBalloon(
                    "Local AI Model Manager",
                    startedByWindows
                        ? "已随 Windows 启动。网关已就绪，所有模型保持待机。"
                        : "已最小化到托盘。网关已就绪，所有模型保持待机。");
            }
            else
            {
                _window.Show();
            }

            _runtime.Logs.Info("ui", $"window shown (minimized={minimize}), {_runtime.Models.Count} model(s) registered in standby");
        }
        catch (Exception ex)
        {
            TryLog("fatal", "启动失败", ex);
            MessageBox.Show(
                $"启动失败：{ex.Message}\n\n详细信息已写入内存日志（可在“运行日志”页面查看）。",
                "Local AI Model Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _tray?.Dispose();
            _runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            TryLog("fatal", "关闭时发生错误", ex);
        }
        finally
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }

        base.OnExit(e);
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void HideMainWindow() => _window?.Hide();

    private void ExitApplication()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;

        if (_services?.Current.General.ConfirmExit == true)
        {
            var loadedCount = _services.Lifecycle.GetStatuses().Count(s => s.IsLoaded);
            var message = loadedCount > 0
                ? $"退出将卸载 {loadedCount} 个已加载模型并结束所有引擎进程。确定退出吗？"
                : "退出将停止 API 网关并结束所有引擎进程。确定退出吗？";

            if (!_services.Dialogs.Confirm("退出 Local AI Model Manager", message))
            {
                _shuttingDown = false;
                return;
            }
        }

        _window?.CloseForReal();
        Shutdown(0);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryLog("fatal", "UI 线程未处理异常", e.Exception);
        MessageBox.Show(
            $"发生未处理的错误：{e.Exception.Message}",
            "Local AI Model Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void TryLog(string category, string message, Exception? exception)
    {
        try
        {
            _runtime?.Logs.Error(category, message, exception);
        }
        catch (Exception)
        {
            // Logging must never mask the original failure.
        }
    }
}
