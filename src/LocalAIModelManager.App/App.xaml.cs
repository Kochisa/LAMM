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

        // Make the native window frame follow the palette (title bar, caption text and the
        // 1px window border), for this window and every dialog created later.
        WindowChrome.HookAllWindows();

        // Headless commands run before anything else (no window, no single-instance
        // mutex, no gateway) so the manager can be scripted.
        var commandLineExit = await TryRunCommandLineAsync(e.Args).ConfigureAwait(true);
        if (commandLineExit is not null)
        {
            Shutdown(commandLineExit.Value);
            return;
        }

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

    /// <summary>
    /// Headless command line support. Returns null when the arguments do not contain one.
    ///
    /// <c>--autotune &lt;model.gguf&gt; [--out &lt;report.txt&gt;] [--max-vram pct]</c> reads the
    /// model's own GGUF metadata, combines it with this machine's VRAM and writes the
    /// parameters it would use, so model import can be scripted instead of hand tuned.
    ///
    /// <c>--show-command &lt;modelId&gt;</c> prints the exact llama-server command line that
    /// would be launched for a registered model. This is the acceptance instrument for
    /// "no unwanted inference parameters are added".
    /// </summary>
    private static async Task<int?> TryRunCommandLineAsync(string[] args)
    {
        var showCommandIndex = Array.FindIndex(args, a => string.Equals(a, "--show-command", StringComparison.OrdinalIgnoreCase));
        if (showCommandIndex >= 0)
        {
            if (showCommandIndex + 1 >= args.Length)
            {
                Console.Error.WriteLine("usage: LocalAIModelManager.exe --show-command <modelId>");
                return 2;
            }

            return await ShowCommandAsync(args[showCommandIndex + 1]).ConfigureAwait(false);
        }

        var autotuneIndex = Array.FindIndex(args, a => string.Equals(a, "--autotune", StringComparison.OrdinalIgnoreCase));
        if (autotuneIndex < 0)
        {
            return null;
        }

        if (autotuneIndex + 1 >= args.Length)
        {
            Console.Error.WriteLine("usage: LocalAIModelManager.exe --autotune <model.gguf> [--out <report.txt>]");
            return 2;
        }

        var modelPath = args[autotuneIndex + 1];
        var outIndex = Array.FindIndex(args, a => string.Equals(a, "--out", StringComparison.OrdinalIgnoreCase));
        var reportPath = outIndex >= 0 && outIndex + 1 < args.Length
            ? args[outIndex + 1]
            : modelPath + ".autotune.txt";

        try
        {
            await using var monitor = new Core.Resources.SystemResourceMonitor(
                Core.Logging.NullAppLogger.Instance,
                TimeSpan.FromSeconds(5));

            var snapshot = await monitor.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            var gpu = snapshot.PrimaryGpu;

            var maxVramIndex = Array.FindIndex(args, a => string.Equals(a, "--max-vram", StringComparison.OrdinalIgnoreCase));
            var maxVramPercent = maxVramIndex >= 0 &&
                                 maxVramIndex + 1 < args.Length &&
                                 int.TryParse(args[maxVramIndex + 1], out var parsedPercent)
                ? parsedPercent
                : 70;

            var result = Core.Models.ModelAutoTuner.Tune(new Core.Models.AutoTuneInput
            {
                ModelFilePath = modelPath,
                GpuAvailable = snapshot.GpuAvailable,
                TotalVramBytes = gpu?.TotalBytes,
                FreeVramBytes = gpu?.FreeBytes,
                MaxVramUsagePercent = maxVramPercent,
            });

            var report = new List<string>
            {
                $"模型：{modelPath}",
                string.Empty,
            };
            report.AddRange(result.Explanation.Select(line => "· " + line));
            if (result.Warnings.Count > 0)
            {
                report.Add(string.Empty);
                report.Add("注意：");
                report.AddRange(result.Warnings.Select(w => "! " + w));
            }

            report.Add(string.Empty);
            report.Add("参数：");
            report.AddRange(result.Parameters.Select(p => $"  {p.Key} = {p.Value}"));

            var text = string.Join(Environment.NewLine, report);
            File.WriteAllText(reportPath, text);
            Console.WriteLine(text);

            return result.UsedFallback ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"autotune failed: {ex.Message}");
            return 3;
        }
    }

    /// <summary>
    /// Prints the exact command line for a registered model, using the same launch-plan
    /// code path the real load uses. Nothing is started and no weights are read.
    /// </summary>
    private static async Task<int> ShowCommandAsync(string modelId)
    {
        AppRuntime? runtime = null;
        try
        {
            runtime = AppRuntime.Create(new AppRuntimeOptions
            {
                StartGateway = false,
                StartResourceMonitor = false,
                StartIdleMonitor = false,
                SeedDefaultEngines = false,
                SeedEngineExecutable = null,
            });

            var model = runtime.Models.Get(modelId);
            if (model is null)
            {
                Console.Error.WriteLine($"model '{modelId}' is not registered.");
                return 2;
            }

            var result = await runtime.Lifecycle.TestAsync(modelId, CancellationToken.None).ConfigureAwait(false);

            var lines = new List<string>
            {
                $"model        : {model.Id}",
                $"display name : {model.DisplayName}",
                $"file         : {model.FilePath}",
                $"engine       : {model.EngineId}",
                $"stored params: {(model.Parameters.Count == 0 ? "(none)" : string.Join(", ", model.Parameters.Select(p => $"{p.Key}={p.Value}")))}",
                $"extra args   : {(model.AdditionalArguments.Count == 0 ? "(none)" : string.Join(" | ", model.AdditionalArguments))}",
                string.Empty,
            };

            lines.AddRange(result.Details);
            if (result.Warnings.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("warnings:");
                lines.AddRange(result.Warnings.Select(w => "  ! " + w));
            }

            if (result.Errors.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("errors:");
                lines.AddRange(result.Errors.Select(e => "  x " + e));
            }

            Console.WriteLine(string.Join(Environment.NewLine, lines));
            return result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"show-command failed: {ex.Message}");
            return 3;
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
        }
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
