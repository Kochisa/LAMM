namespace LocalAIModelManager.Core.Models;

/// <summary>Snapshot of a tracked backend child process (Process management page / logs).</summary>
public sealed record BackendProcessInfo
{
    public required int Pid { get; init; }

    public required string ModelId { get; init; }

    public required string EngineId { get; init; }

    public required int Port { get; init; }

    public required string State { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset? ExitedAtUtc { get; init; }

    public int? ExitCode { get; init; }

    public BackendStopMode StopMode { get; init; }

    public bool IsAlive { get; init; }

    public long WorkingSetBytes { get; init; }

    public string? LastError { get; init; }
}
