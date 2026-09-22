using LocalAIModelManager.Core.Models;

namespace LocalAIModelManager.Core.Processes;

/// <summary>
/// Authoritative register of every engine child process the manager owns, plus a
/// bounded history of exited processes for the Runtime Status page.
/// </summary>
public sealed class ProcessInventory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IBackendProcess> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<BackendProcessInfo> _history = new();
    private readonly int _historyCapacity;

    public ProcessInventory(int historyCapacity = 64)
    {
        _historyCapacity = Math.Max(4, historyCapacity);
    }

    public event Action? Changed;

    public void Register(IBackendProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);

        lock (_gate)
        {
            // A stale entry for the same model is simply replaced; its own Exited
            // handler uses a reference check and therefore cannot evict the new one.
            _live[process.ModelId] = process;
        }

        process.Exited += OnProcessExited;
        Changed?.Invoke();
    }

    public void Unregister(string modelId)
    {
        IBackendProcess? removed = null;
        lock (_gate)
        {
            if (_live.TryGetValue(modelId, out var existing))
            {
                _live.Remove(modelId);
                removed = existing;
            }
        }

        if (removed is not null)
        {
            removed.Exited -= OnProcessExited;
            PushHistory(removed.ToInfo());
            Changed?.Invoke();
        }
    }

    public IBackendProcess? Find(string modelId)
    {
        lock (_gate)
        {
            return _live.TryGetValue(modelId, out var process) ? process : null;
        }
    }

    public IReadOnlyList<IBackendProcess> Live
    {
        get
        {
            lock (_gate)
            {
                return _live.Values.ToList();
            }
        }
    }

    public IReadOnlyList<int> LivePids
    {
        get
        {
            lock (_gate)
            {
                return _live.Values.Select(p => p.Pid).Where(pid => pid > 0).ToList();
            }
        }
    }

    public int LiveCount
    {
        get
        {
            lock (_gate)
            {
                return _live.Count;
            }
        }
    }

    public IReadOnlyList<BackendProcessInfo> Snapshot()
    {
        lock (_gate)
        {
            return _live.Values.Select(p => p.ToInfo()).ToList();
        }
    }

    public IReadOnlyList<BackendProcessInfo> History
    {
        get
        {
            lock (_gate)
            {
                return _history.Reverse().ToList();
            }
        }
    }

    /// <summary>Graceful-first shutdown of every tracked process.</summary>
    public async Task StopAllAsync(TimeSpan grace, CancellationToken cancellationToken = default)
    {
        var processes = Live;
        if (processes.Count == 0)
        {
            return;
        }

        var tasks = processes.Select(p => p.StopAsync(grace, cancellationToken)).ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Individual failures are already logged by the process itself.
        }

        foreach (var process in processes)
        {
            Unregister(process.ModelId);
        }
    }

    private void OnProcessExited(IBackendProcess process, int? exitCode)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(process.ModelId, out var current) && ReferenceEquals(current, process))
            {
                _live.Remove(process.ModelId);
            }
        }

        process.Exited -= OnProcessExited;
        PushHistory(process.ToInfo());
        Changed?.Invoke();
    }

    private void PushHistory(BackendProcessInfo info)
    {
        lock (_gate)
        {
            _history.Enqueue(info with { ExitedAtUtc = DateTimeOffset.UtcNow });
            while (_history.Count > _historyCapacity)
            {
                _history.Dequeue();
            }
        }
    }
}
