using System.Diagnostics;
using System.Text;
using LocalAIModelManager.Core.Localization;
using LocalAIModelManager.Core.Logging;
using LocalAIModelManager.Core.Models;

namespace LocalAIModelManager.Core.Processes;

/// <summary>
/// One supervised engine process. Shutdown is always graceful-first: stdin is closed
/// and a CTRL_BREAK is delivered when a console can be attached; only if the child is
/// still alive after the grace period is the process tree force-killed.
/// </summary>
public sealed class BackendProcess : IBackendProcess
{
    private readonly Process _process;
    private readonly IAppLogger _logger;
    private readonly string? _controlHelperPath;
    private readonly Queue<string> _output;
    private readonly object _gate = new();
    private readonly TaskCompletionSource<int?> _exitSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private BackendProcessState _state = BackendProcessState.Created;
    private BackendStopMode _stopMode = BackendStopMode.None;
    private string? _lastError;
    private int? _exitCode;
    private long _outputLines;

    public BackendProcess(
        ProcessStartInfo startInfo,
        string modelId,
        string engineId,
        string bindHost,
        int port,
        int outputBufferLines,
        IAppLogger logger,
        BackendJobObject? job = null,
        string? controlHelperPath = null)
    {
        StartInfo = startInfo;
        ModelId = modelId;
        EngineId = engineId;
        BindHost = bindHost;
        Port = port;
        _logger = logger;
        _controlHelperPath = controlHelperPath;
        _output = new Queue<string>(Math.Max(16, outputBufferLines));
        OutputCapacity = Math.Max(16, outputBufferLines);

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => OnOutputLine(e.Data);
        _process.ErrorDataReceived += (_, e) => OnOutputLine(e.Data);
        _process.Exited += (_, _) => OnExited();

        Job = job;
    }

    public ProcessStartInfo StartInfo { get; }

    public BackendJobObject? Job { get; }

    public int OutputCapacity { get; }

    public int Pid { get; private set; }

    public string ModelId { get; }

    public string EngineId { get; }

    public int Port { get; }

    public string BindHost { get; }

    public DateTimeOffset StartedAtUtc => _startedAt;

    public BackendProcessState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public BackendStopMode StopMode
    {
        get
        {
            lock (_gate)
            {
                return _stopMode;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    public long OutputLineCount => Interlocked.Read(ref _outputLines);

    public event Action<IBackendProcess, string>? OutputLine;

    public event Action<IBackendProcess, int?>? Exited;

    /// <summary>Start time of the tracked process, or null when the process object cannot answer.</summary>
    public DateTimeOffset? ProcessStartTime
    {
        get
        {
            try
            {
                return _process.StartTime;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public bool IsAlive
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            lock (_gate)
            {
                return _exitCode;
            }
        }
    }

    public long WorkingSetBytes
    {
        get
        {
            try
            {
                _process.Refresh();
                return _process.WorkingSet64;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return 0;
            }
        }
    }

    public void Start()
    {
        if (!_process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{StartInfo.FileName}'.");
        }

        Pid = _process.Id;
        lock (_gate)
        {
            _state = BackendProcessState.Running;
        }

        if (StartInfo.RedirectStandardOutput)
        {
            _process.BeginOutputReadLine();
        }

        if (StartInfo.RedirectStandardError)
        {
            _process.BeginErrorReadLine();
        }

        Job?.TryAssign(_process);

        _logger.Info("process", Loc.T(
            "log.process.started",
            Pid,
            ModelId,
            EngineId,
            Port,
            Job?.IsAvailable == true ? Loc.T("log.process.jobAttached") : Loc.T("log.process.jobUnavailable")));
        _logger.Debug("process", Loc.T("log.process.command", $"{StartInfo.FileName} {string.Join(' ', StartInfo.ArgumentList)}"));
    }

    public string Tail(int lines = 25)
    {
        lock (_gate)
        {
            if (_output.Count == 0)
            {
                return string.Empty;
            }

            var take = Math.Clamp(lines, 1, _output.Count);
            return string.Join(Environment.NewLine, _output.Skip(_output.Count - take));
        }
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsAlive && _exitSource.Task.IsCompleted)
        {
            return true;
        }

        var delay = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(_exitSource.Task, delay).ConfigureAwait(false);
        return completed == _exitSource.Task;
    }

    public async Task<bool> StopAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        if (!IsAlive)
        {
            return true;
        }

        lock (_gate)
        {
            _state = BackendProcessState.Stopping;
        }

        _logger.Info("process", Loc.T("log.lifecycle.stopping", Pid, ModelId, grace.TotalSeconds.ToString("0.#")));
        await TryGracefulShutdownAsync().ConfigureAwait(false);

        var exited = await WaitForExitAsync(grace, cancellationToken).ConfigureAwait(false);
        if (exited)
        {
            lock (_gate)
            {
                _stopMode = BackendStopMode.Graceful;
            }

            _logger.Info("process", Loc.T("log.lifecycle.stoppedGraceful", Pid, ModelId));
            return true;
        }

        _logger.Warn("process", Loc.T("log.lifecycle.forceKill", Pid, ModelId, grace.TotalSeconds.ToString("0.#")));

        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            lock (_gate)
            {
                _lastError = ex.Message;
            }

            return false;
        }

        lock (_gate)
        {
            _stopMode = BackendStopMode.ForcedKill;
        }

        var killed = await WaitForExitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        _logger.Warn("process", Loc.T("log.lifecycle.killed", Pid, ModelId, killed));
        return killed;
    }

    public BackendProcessInfo ToInfo()
    {
        lock (_gate)
        {
            return new BackendProcessInfo
            {
                Pid = Pid,
                ModelId = ModelId,
                EngineId = EngineId,
                Port = Port,
                State = _state.ToString(),
                StartedAtUtc = _startedAt,
                ExitCode = _exitCode,
                StopMode = _stopMode,
                IsAlive = IsAlive,
                WorkingSetBytes = WorkingSetBytes,
                LastError = _lastError,
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (IsAlive)
            {
                await StopAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("process", Loc.T("log.process.disposeFailed", Pid), ex);
        }
        finally
        {
            try
            {
                _process.CancelOutputRead();
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
        }
    }

    private async Task TryGracefulShutdownAsync()
    {
        // 1) Some engines exit when their stdin reaches EOF.
        try
        {
            if (StartInfo.RedirectStandardInput)
            {
                await _process.StandardInput.WriteLineAsync().ConfigureAwait(false);
                _process.StandardInput.Close();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }

        // 2) Deliver CTRL_BREAK through a disposable helper process, which llama-server
        //    turns into a clean shutdown. The helper is used instead of attaching this
        //    process, so the manager can never be terminated by its own signal.
        ConsoleControl.TrySendCtrlBreak(Pid, _logger, _controlHelperPath);
    }

    private void OnOutputLine(string? line)
    {
        if (line is null)
        {
            return;
        }

        Interlocked.Increment(ref _outputLines);
        lock (_gate)
        {
            if (_output.Count >= OutputCapacity)
            {
                _output.Dequeue();
            }

            _output.Enqueue(line);
        }

        // Engine stdout is never written to disk; it only feeds the in-memory buffer.
        // Lines that explain GPU/backend behaviour (or a failure) are raised to
        // Information so they stay visible at the default log level - otherwise the
        // answer to "why is this running on the CPU?" would be filtered out.
        _logger.Log(ClassifyEngineLine(line), $"engine:{EngineId}", line);
        OutputLine?.Invoke(this, line);
    }

    private static Logging.LogLevel ClassifyEngineLine(string line)
    {
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("cannot", StringComparison.OrdinalIgnoreCase))
        {
            return Logging.LogLevel.Warning;
        }

        if (line.Contains("cuda", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("vulkan", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("rocm", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("metal", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("gpu", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("device", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("offload", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("backend", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("warning", StringComparison.OrdinalIgnoreCase))
        {
            return Logging.LogLevel.Information;
        }

        return Logging.LogLevel.Debug;
    }

    private void OnExited()
    {
        int? exitCode = null;
        try
        {
            exitCode = _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
        }

        lock (_gate)
        {
            _exitCode = exitCode;
            if (_state != BackendProcessState.Stopping)
            {
                _state = BackendProcessState.Exited;
                if (exitCode is not null and not 0 && _stopMode == BackendStopMode.None)
                {
                    _stopMode = BackendStopMode.Crashed;
                    _lastError ??= $"Engine exited with code {exitCode}.";
                }
            }
            else
            {
                _state = BackendProcessState.Exited;
            }
        }

        _logger.Info("process", Loc.T("log.process.exited", Pid, ModelId, exitCode?.ToString() ?? "?"));
        _exitSource.TrySetResult(exitCode);
        Exited?.Invoke(this, exitCode);
    }
}

/// <summary>
/// Best-effort CTRL_BREAK delivery so engines can shut down cleanly.
///
/// A windowless GUI process has no console, while <c>GenerateConsoleCtrlEvent</c>
/// only reaches processes attached to the caller's console. The attach-and-send
/// sequence therefore runs inside the disposable
/// <c>LocalAIModelManager.ControlHelper</c> process: if the helper is itself
/// terminated by the signal after delivering it, nothing of value is lost.
/// </summary>
internal static class ConsoleControl
{
    private const string HelperArgument = "--send-ctrl-break";

    public static bool TrySendCtrlBreak(int pid, IAppLogger logger, string? helperPath)
    {
        if (pid <= 0 || string.IsNullOrWhiteSpace(helperPath) || !File.Exists(helperPath))
        {
            if (!string.IsNullOrWhiteSpace(helperPath))
            {
                logger.Debug("process", Loc.T("log.process.helperMissing", helperPath));
            }

            return false;
        }

        try
        {
            using var helper = Process.Start(new ProcessStartInfo
            {
                FileName = helperPath!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                ArgumentList = { HelperArgument, pid.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            });

            if (helper is null)
            {
                return false;
            }

            if (!helper.WaitForExit(5000))
            {
                TryKill(helper);
                logger.Debug("process", Loc.T("log.process.helperTimeout", pid));
                return false;
            }

            var delivered = helper.ExitCode == 0;
            if (delivered)
            {
                logger.Debug("process", Loc.T("log.process.ctrlBreak", pid));
            }
            else
            {
                logger.Debug("process", Loc.T("log.process.ctrlBreakFailed", pid, helper.ExitCode));
            }

            return delivered;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            logger.Debug("process", Loc.T("log.process.helperFailed", pid, ex.Message));
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
