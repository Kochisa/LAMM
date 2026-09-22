using LocalAIModelManager.Core.Models;

namespace LocalAIModelManager.Core.Lifecycle;

public sealed class ModelNotFoundException : Exception
{
    public ModelNotFoundException(string modelId)
        : base($"Model '{modelId}' is not registered.")
    {
        ModelId = modelId;
    }

    public string ModelId { get; }
}

/// <summary>The model exists but could not be brought to a ready state.</summary>
public class ModelUnavailableException : Exception
{
    public ModelUnavailableException(string modelId, string message, Exception? inner = null)
        : base(message, inner)
    {
        ModelId = modelId;
    }

    public string ModelId { get; }
}

/// <summary>No capacity (loaded-model limit or VRAM) could be freed for this model.</summary>
public sealed class ModelCapacityException : ModelUnavailableException
{
    public ModelCapacityException(string modelId, string message)
        : base(modelId, message)
    {
    }
}

public sealed class ModelStateChangedEventArgs : EventArgs
{
    public required string ModelId { get; init; }

    public required ModelState Previous { get; init; }

    public required ModelState Current { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// A short-lived claim on a loaded model. Holding a lease keeps the model out of
/// the idle-unload path; disposing it refreshes the last-used timestamp.
/// </summary>
public sealed class ModelLease : IDisposable
{
    private Action<ModelLease>? _release;
    private bool _disposed;

    internal ModelLease(string modelId, string baseUrl, int port, Action<ModelLease> release)
    {
        ModelId = modelId;
        BaseUrl = baseUrl;
        Port = port;
        _release = release;
        AcquiredAtUtc = DateTimeOffset.UtcNow;
    }

    public string ModelId { get; }

    public string BaseUrl { get; }

    public int Port { get; }

    public DateTimeOffset AcquiredAtUtc { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var release = Interlocked.Exchange(ref _release, null);
        release?.Invoke(this);
    }
}

public interface IModelLifecycleManager : IAsyncDisposable
{
    event EventHandler<ModelStateChangedEventArgs>? StateChanged;

    IReadOnlyList<ModelRuntimeStatus> GetStatuses();

    ModelRuntimeStatus? GetStatus(string modelId);

    /// <summary>
    /// The heart of the lazy-loading contract: returns a ready lease, loading the
    /// model on demand (starting the engine process when necessary).
    /// </summary>
    Task<ModelLease> AcquireAsync(string modelId, CancellationToken cancellationToken);

    /// <summary>Explicitly loads a model (Models page / tray).</summary>
    Task<bool> StartAsync(string modelId, CancellationToken cancellationToken);

    /// <summary>Explicitly unloads a model and releases its VRAM.</summary>
    Task<bool> StopAsync(string modelId, CancellationToken cancellationToken);

    Task<bool> RestartAsync(string modelId, CancellationToken cancellationToken);

    /// <summary>Validates configuration and produces the exact command line without loading weights.</summary>
    Task<ModelTestResult> TestAsync(string modelId, CancellationToken cancellationToken);

    /// <summary>Unloads every model that has been idle for longer than the configured timeout.</summary>
    Task<IReadOnlyList<string>> UnloadIdleAsync(CancellationToken cancellationToken);

    Task UnloadAllAsync(CancellationToken cancellationToken);

    /// <summary>Starts the background idle sweep. No model is loaded by this call.</summary>
    Task StartIdleMonitorAsync(CancellationToken cancellationToken);
}
