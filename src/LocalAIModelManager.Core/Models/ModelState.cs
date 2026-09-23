namespace LocalAIModelManager.Core.Models;

/// <summary>
/// Lifecycle state of one registered model. The manager starts with every model in
/// <see cref="Standby"/>; nothing is loaded until an API call actually asks for it.
/// </summary>
public enum ModelState
{
    /// <summary>Configured but not loaded. No process, no VRAM held.</summary>
    Standby = 0,

    /// <summary>The backend child process is being created.</summary>
    Starting = 1,

    /// <summary>The process is up and weights are being read into VRAM.</summary>
    Loading = 2,

    /// <summary>Readiness probe succeeded, the model can serve requests.</summary>
    Ready = 3,

    /// <summary>A graceful stop is in progress.</summary>
    Stopping = 4,

    /// <summary>Startup or inference failed; <c>LastError</c> explains why.</summary>
    Failed = 5,
}

/// <summary>How a backend process actually exited.</summary>
public enum BackendStopMode
{
    None = 0,
    Graceful = 1,
    ForcedKill = 2,
    Crashed = 3,
}

/// <summary>Point-in-time runtime view of a registered model, consumed by the Runtime Status page.</summary>
public sealed record ModelRuntimeStatus
{
    public required string ModelId { get; init; }

    public required string DisplayName { get; init; }

    public required ModelState State { get; init; }

    public string? EngineId { get; init; }

    public string? EngineName { get; init; }

    public string? FilePath { get; init; }

    public int? Pid { get; init; }

    public int? Port { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? LastUsedAtUtc { get; init; }

    public TimeSpan? IdleFor { get; init; }

    public int ActiveRequests { get; init; }

    public long? VramBytes { get; init; }

    public long? WorkingSetBytes { get; init; }

    public double? CpuPercent { get; init; }

    public long TotalRequests { get; init; }

    public BackendStopMode LastStopMode { get; init; }

    public string? LastError { get; init; }

    /// <summary>
    /// The exact command line the engine was launched with. This is the ground truth
    /// for questions like "is it really offloading to the GPU?".
    /// </summary>
    public IReadOnlyList<string> LaunchArguments { get; init; } = Array.Empty<string>();

    /// <summary>Non-fatal warning raised while loading, e.g. requested GPU offload but the engine sees no GPU device.</summary>
    public string? LaunchWarning { get; init; }

    public string LaunchCommandLine => LaunchArguments.Count == 0
        ? string.Empty
        : string.Join(' ', LaunchArguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

    public bool IsLoaded => State == ModelState.Ready;
}

/// <summary>Result of the Models page "Test" action: validates configuration without loading weights.</summary>
public sealed record ModelTestResult
{
    public required bool Success { get; init; }

    public required string Summary { get; init; }

    public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LaunchArguments { get; init; } = Array.Empty<string>();
}
