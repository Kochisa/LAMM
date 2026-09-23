using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LocalAIModelManager.App.Infrastructure;
using LocalAIModelManager.App.Services;
using LocalAIModelManager.App.ViewModels;
using LocalAIModelManager.Core.Configuration;
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

        // The interface language is itself a setting, and both the headless commands
        // below and the "already running" dialog print text before any runtime exists.
        // Read it from disk first; AppRuntime.Create applies the same value again later,
        // where it is a no-op.
        SettingsService.ApplyStoredLanguage(AppPaths.ResolveConfigDirectory());

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
                Loc.T("dialog.alreadyRunning"),
                Loc.T("app.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            TryLog("fatal", Loc.T("log.fatal.unhandledAppDomain"), args.ExceptionObject as Exception);
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

            // Severity comes from the producer (AppServices.NotifyFailure), never from the
            // message text: matching localized words would stop working in any language
            // but the one the words were written in.
            _services.FailureNotification += message =>
            {
                _tray?.ShowBalloon(Loc.T("app.title"), message);
            };

            await _shell.InitializeAsync().ConfigureAwait(true);

            var minimize = startMinimized || _runtime.Settings.Current.General.StartMinimized;
            if (minimize)
            {
                _window.Hide();
                _tray.ShowBalloon(
                    Loc.T("app.title"),
                    startedByWindows
                        ? Loc.T("app.notify.startedWithWindows")
                        : Loc.T("app.notify.minimizedToTray"));
            }
            else
            {
                _window.Show();
            }

            _runtime.Logs.Info("ui", Loc.T("log.runtime.windowShown", minimize, _runtime.Models.Count));
        }
        catch (Exception ex)
        {
            TryLog("fatal", Loc.T("log.fatal.startupFailed"), ex);
            MessageBox.Show(
                Loc.T("dialog.startupFailed", ex.Message),
                Loc.T("app.title"),
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
            _shell?.Dispose();
            _runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            TryLog("fatal", Loc.T("log.fatal.exitFailed"), ex);
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

            var contextIndex = Array.FindIndex(args, a => string.Equals(a, "--context", StringComparison.OrdinalIgnoreCase));
            var targetContext = contextIndex >= 0 &&
                                contextIndex + 1 < args.Length &&
                                int.TryParse(args[contextIndex + 1], out var parsedContext)
                ? parsedContext
                : 8192;

            var result = Core.Models.ModelAutoTuner.Tune(new Core.Models.AutoTuneInput
            {
                ModelFilePath = modelPath,
                GpuAvailable = snapshot.GpuAvailable,
                TotalVramBytes = gpu?.TotalBytes,
                FreeVramBytes = gpu?.FreeBytes,
                MaxVramUsagePercent = maxVramPercent,
                TargetContextSize = targetContext,
            });

            var report = new List<string>
            {
                Loc.T("models.test.header", modelPath),
                string.Empty,
            };
            report.AddRange(result.Explanation.Select(line => "· " + line));
            if (result.Warnings.Count > 0)
            {
                report.Add(string.Empty);
                report.Add(Loc.T("app.cli.warnings"));
                report.AddRange(result.Warnings.Select(w => "! " + w));
            }

            report.Add(string.Empty);
            report.Add(Loc.T("app.cli.parameters"));
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
                ? Loc.T("dialog.exit.withLoaded", loadedCount)
                : Loc.T("dialog.exit.noLoaded");

            if (!_services.Dialogs.Confirm(Loc.T("dialog.exit.title"), message))
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
        TryLog("fatal", Loc.T("log.fatal.unhandled"), e.Exception);
        MessageBox.Show(
            Loc.T("dialog.unhandled", e.Exception.Message),
            Loc.T("app.title"),
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
