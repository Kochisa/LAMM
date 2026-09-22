using LocalAIModelManager.Core.Logging;
using LocalAIModelManager.Core.Models;

namespace LocalAIModelManager.Core.Processes;

public enum BackendProcessState
{
    Created = 0,
    Running = 1,
    Stopping = 2,
    Exited = 3,
    Faulted = 4,
}

/// <summary>Everything the process layer needs that is not part of the launch plan.</summary>
public sealed record BackendProcessContext
{
    public required IAppLogger Logger { get; init; }

    public int OutputBufferLines { get; init; } = 400;

    public bool CaptureOutput { get; init; } = true;

    /// <summary>
    /// Path of the disposable helper that delivers CTRL_BREAK to the engine's
    /// console. See <c>LocalAIModelManager.ControlHelper</c>: the long-lived
    /// manager must never attach itself to an engine's console.
    /// </summary>
    public string? ControlHelperPath { get; init; }

    /// <summary>Extra environment variables for the engine process (for example outbound proxy settings).</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>
/// A controlled child process hosting one inference engine instance for one model.
/// The UI never touches this type directly - only the lifecycle manager does.
/// </summary>
public interface IBackendProcess : IAsyncDisposable
{
    int Pid { get; }

    string ModelId { get; }

    string EngineId { get; }

    int Port { get; }

    string BindHost { get; }

    DateTimeOffset StartedAtUtc { get; }

    BackendProcessState State { get; }

    bool IsAlive { get; }

    int? ExitCode { get; }

    BackendStopMode StopMode { get; }

    string? LastError { get; }

    long WorkingSetBytes { get; }

    /// <summary>Raised for each captured engine output line (stdout + stderr).</summary>
    event Action<IBackendProcess, string>? OutputLine;

    event Action<IBackendProcess, int?>? Exited;

    string Tail(int lines = 25);

    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Graceful shutdown first, forced kill after the grace period. Never leaves an orphan.</summary>
    Task<bool> StopAsync(TimeSpan grace, CancellationToken cancellationToken);

    BackendProcessInfo ToInfo();
}
