using System.Collections.Concurrent;
using System.Diagnostics;
using LocalAIModelManager.Core.Backends;
using LocalAIModelManager.Core.Configuration;
using LocalAIModelManager.Core.Logging;
using LocalAIModelManager.Core.Models;
using LocalAIModelManager.Core.Processes;
using LocalAIModelManager.Core.Resources;

namespace LocalAIModelManager.Core.Lifecycle;

/// <summary>
/// Owns the model lifecycle: standby -> starting -> loading -> ready, plus idle
/// unloading and LRU eviction under VRAM pressure. It is the only component that
/// talks to backend adapters and child processes; the UI and the API gateway talk
/// to this class.
/// </summary>
public sealed class ModelLifecycleManager : IModelLifecycleManager
{
    private readonly SettingsService _settings;
    private readonly ModelRegistry _models;
    private readonly BackendAdapterRegistry _adapters;
    private readonly ProcessInventory _inventory;
    private readonly PortAllocator _ports;
    private readonly IResourceMonitor _resources;
    private readonly IAppLogger _logger;
    private readonly TimeSpan _idleSweepInterval;
    private readonly string? _controlHelperPath;

    private readonly ConcurrentDictionary<string, ModelSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _idleLoopSource;
    private Task? _idleLoopTask;
    private bool _disposed;

    public ModelLifecycleManager(
        SettingsService settings,
        ModelRegistry models,
        BackendAdapterRegistry adapters,
        ProcessInventory inventory,
        PortAllocator ports,
        IResourceMonitor resources,
        IAppLogger logger,
        TimeSpan? idleSweepInterval = null,
        string? controlHelperPath = null)
    {
        _settings = settings;
        _models = models;
        _adapters = adapters;
        _inventory = inventory;
        _ports = ports;
        _resources = resources;
        _logger = logger;
        _idleSweepInterval = idleSweepInterval ?? TimeSpan.FromSeconds(5);
        _controlHelperPath = controlHelperPath;
    }

    public event EventHandler<ModelStateChangedEventArgs>? StateChanged;

    /// <summary>Raised after a model becomes ready or is unloaded, for status dashboards.</summary>
    public event EventHandler<string>? ModelLoaded;

    public event EventHandler<string>? ModelUnloaded;

    public IReadOnlyList<ModelRuntimeStatus> GetStatuses()
    {
        var snapshot = _resources.Current;
        var statuses = new List<ModelRuntimeStatus>();

        foreach (var model in _models.All)
        {
            statuses.Add(BuildStatus(model, snapshot));
        }

        return statuses;
    }

    public ModelRuntimeStatus? GetStatus(string modelId)
    {
        var model = _models.Get(modelId);
        return model is null ? null : BuildStatus(model, _resources.Current);
    }

    public async Task<ModelLease> AcquireAsync(string modelId, CancellationToken cancellationToken)
    {
        var model = RequireModel(modelId);
        if (!model.Enabled)
        {
            throw new ModelUnavailableException(model.Id, $"Model '{model.Id}' is disabled.");
        }

        var slot = GetSlot(model.Id);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            Task<BackendReadyResult>? pending;
            var isLoader = false;
            var retryLater = false;

            await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (slot.State == ModelState.Ready && slot.Process is { IsAlive: true })
                {
                    Interlocked.Increment(ref slot.ActiveRequests);
                    Interlocked.Increment(ref slot.TotalRequests);
                    slot.LastUsedUtc = DateTimeOffset.UtcNow;
                    return new ModelLease(
                        model.Id,
                        $"http://{slot.Process.BindHost}:{slot.Process.Port}",
                        slot.Process.Port,
                        ReleaseLease);
                }

                if (slot.State is ModelState.Starting or ModelState.Loading && slot.Ready is not null)
                {
                    pending = slot.Ready.Task;
                }
                else if (slot.State == ModelState.Stopping)
                {
                    // An unload is in flight; do not race it with a second loader.
                    pending = null;
                    retryLater = true;
                }
                else
                {
                    slot.Ready = new TaskCompletionSource<BackendReadyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    slot.LastError = null;
                    SetState(slot, model.Id, ModelState.Starting, "on demand");
                    pending = slot.Ready.Task;
                    isLoader = true;
                }
            }
            finally
            {
                slot.Gate.Release();
            }

            if (retryLater)
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var result = isLoader
                ? await LoadAsync(model, slot, cancellationToken).ConfigureAwait(false)
                : await pending!.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (!result.Ready)
            {
                throw new ModelUnavailableException(model.Id, result.Message ?? "The model could not be loaded.");
            }
        }

        throw new ModelUnavailableException(modelId, "The model could not be acquired after several attempts.");
    }

    public async Task<bool> StartAsync(string modelId, CancellationToken cancellationToken)
    {
        using var lease = await AcquireAsync(modelId, cancellationToken).ConfigureAwait(false);
        _logger.Info("lifecycle", $"model '{modelId}' started manually and is ready on port {lease.Port}");
        return true;
    }

    public Task<bool> StopAsync(string modelId, CancellationToken cancellationToken)
    {
        RequireModel(modelId);
        return StopInternalAsync(modelId, "manual stop", cancellationToken);
    }

    public async Task<bool> RestartAsync(string modelId, CancellationToken cancellationToken)
    {
        RequireModel(modelId);
        await StopInternalAsync(modelId, "restart", cancellationToken).ConfigureAwait(false);
        return await StartAsync(modelId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ModelTestResult> TestAsync(string modelId, CancellationToken cancellationToken)
    {
        var model = RequireModel(modelId);
        var details = new List<string>();
        var warnings = new List<string>();
        var errors = new List<string>();

        var engine = _settings.Current.Engines.Engines
            .FirstOrDefault(e => string.Equals(e.Id, model.EngineId, StringComparison.OrdinalIgnoreCase));

        if (engine is null)
        {
            errors.Add($"Model references engine '{model.EngineId}', which is no longer configured.");
            return new ModelTestResult
            {
                Success = false,
                Summary = "Engine configuration is missing.",
                Errors = errors,
                Details = details,
            };
        }

        details.Add($"Engine: {engine.Name} ({engine.AdapterKind}) at {engine.ExecutablePath}");

        if (!engine.ExecutableExists())
        {
            errors.Add($"Engine executable not found: {engine.ExecutablePath}");
        }

        if (!model.FileExists())
        {
            errors.Add($"Model file not found: {model.FilePath}");
        }
        else
        {
            var info = new FileInfo(model.FilePath);
            details.Add($"Model file: {info.Length / (1024.0 * 1024.0):F1} MiB, last write {info.LastWriteTimeUtc:u}");
            if (!string.Equals(info.Extension, ".gguf", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"'{info.Extension}' is not the usual .gguf extension; make sure the engine supports it.");
            }
        }

        var adapter = _adapters.TryGet(engine.AdapterKind);
        if (adapter is null)
        {
            errors.Add($"No adapter registered for kind '{engine.AdapterKind}'.");
            return new ModelTestResult
            {
                Success = false,
                Summary = "No adapter for this engine kind.",
                Errors = errors,
                Details = details,
            };
        }

        var capabilities = await Task.Run(() => adapter.Inspect(engine, forceRefresh: false), cancellationToken)
            .ConfigureAwait(false);

        if (!capabilities.IsAvailable)
        {
            warnings.Add($"Capability probe failed: {capabilities.Error}");
        }
        else
        {
            details.Add($"Detected engine version: {capabilities.Version ?? "(unknown)"}");
            details.Add($"Detected supported parameters: {capabilities.Parameters.Count}");
        }

        var parameters = MergeParameters(model);
        var port = _ports.ReserveNext();
        if (port < 0)
        {
            port = _ports.RangeStart;
            warnings.Add("No free internal port in the configured range; the test used the range start for the preview only.");
        }

        try
        {
            var plan = adapter.BuildLaunchPlan(new LaunchRequest
            {
                Model = model,
                Engine = engine,
                Parameters = parameters,
                Port = port,
                BindHost = _settings.Current.Network.BackendBindHost,
                Capabilities = capabilities,
            });

            details.Add($"Working directory: {plan.WorkingDirectory}");
            details.Add($"Command line: {plan.CommandLine}");

            foreach (var warning in plan.Warnings)
            {
                warnings.Add(warning);
            }

            foreach (var flag in parameters.Keys)
            {
                if (capabilities.IsAvailable && !capabilities.Supports(flag) && !capabilities.DetectedFlags.Contains(flag))
                {
                    warnings.Add($"Parameter '{flag}' is not advertised by this build.");
                }
            }

            errors.AddRange(adapter.ValidateLaunch(plan, capabilities));

            return new ModelTestResult
            {
                Success = errors.Count == 0,
                Summary = errors.Count == 0
                    ? "Configuration looks valid; the model can be loaded."
                    : "Configuration has blocking problems.",
                Details = details,
                Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Errors = errors,
                LaunchArguments = plan.Arguments,
            };
        }
        finally
        {
            if (_ports.IsReserved(port))
            {
                _ports.Release(port);
            }
        }
    }

    public async Task<IReadOnlyList<string>> UnloadIdleAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        if (!settings.Lifecycle.IdleUnloadEnabled)
        {
            return Array.Empty<string>();
        }

        var timeout = TimeSpan.FromSeconds(settings.Lifecycle.IdleTimeoutSeconds);
        var now = DateTimeOffset.UtcNow;

        var candidates = _slots
            .Where(kv => kv.Value.State == ModelState.Ready &&
                         Volatile.Read(ref kv.Value.ActiveRequests) == 0 &&
                         now - kv.Value.LastUsedUtc >= timeout)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var modelId in candidates)
        {
            await StopInternalAsync(
                modelId,
                $"idle for {timeout.TotalSeconds:0}s (timeout)",
                cancellationToken).ConfigureAwait(false);
        }

        if (candidates.Count > 0)
        {
            _logger.Info("lifecycle", $"idle unload released {candidates.Count} model(s): {string.Join(", ", candidates)}");
        }

        return candidates;
    }

    public async Task UnloadAllAsync(CancellationToken cancellationToken)
    {
        foreach (var modelId in _slots.Keys.ToList())
        {
            await StopInternalAsync(modelId, "shutdown", cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StartIdleMonitorAsync(CancellationToken cancellationToken)
    {
        if (_idleLoopTask is not null)
        {
            return Task.CompletedTask;
        }

        _idleLoopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _idleLoopTask = Task.Run(() => IdleLoopAsync(_idleLoopSource.Token));
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_idleLoopSource is not null)
        {
            await _idleLoopSource.CancelAsync().ConfigureAwait(false);
        }

        if (_idleLoopTask is not null)
        {
            try
            {
                await _idleLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _idleLoopSource?.Dispose();

        try
        {
            await UnloadAllAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn("lifecycle", "error while unloading models during shutdown", ex);
        }

        foreach (var slot in _slots.Values)
        {
            slot.Gate.Dispose();
        }

        _slots.Clear();
    }

    private async Task IdleLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_idleSweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await UnloadIdleAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("lifecycle", "the idle unload loop stopped unexpectedly", ex);
        }
    }

    private async Task<BackendReadyResult> LoadAsync(ModelDefinition model, ModelSlot slot, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        IBackendProcess? process = null;
        var port = 0;

        try
        {
            var engine = _settings.Current.Engines.Engines
                .FirstOrDefault(e => string.Equals(e.Id, model.EngineId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ModelUnavailableException(model.Id, $"Engine '{model.EngineId}' is not configured.");

            var adapter = _adapters.Get(engine.AdapterKind);
            var capabilities = await Task.Run(() => adapter.Inspect(engine), cancellationToken).ConfigureAwait(false);

            await EnsureCapacityAsync(model.Id, cancellationToken).ConfigureAwait(false);

            port = _ports.ReserveNext();
            if (port < 0)
            {
                throw new ModelCapacityException(
                    model.Id,
                    $"No free internal port in range {_ports.RangeStart}-{_ports.RangeEnd}.");
            }

            slot.Port = port;

            var parameters = MergeParameters(model);
            var plan = adapter.BuildLaunchPlan(new LaunchRequest
            {
                Model = model,
                Engine = engine,
                Parameters = parameters,
                Port = port,
                BindHost = _settings.Current.Network.BackendBindHost,
                Capabilities = capabilities,
            });

            foreach (var warning in plan.Warnings)
            {
                _logger.Warn("lifecycle", $"model '{model.Id}': {warning}");
            }

            var problems = adapter.ValidateLaunch(plan, capabilities);
            if (problems.Count > 0)
            {
                throw new ModelUnavailableException(model.Id, string.Join(" ", problems));
            }

            SetState(slot, model.Id, ModelState.Loading, $"loading on port {port}");
            _logger.Info("lifecycle", $"loading model '{model.Id}' with engine '{engine.Name}' on {plan.BindHost}:{port}");
            _logger.Debug("lifecycle", $"launch: {plan.CommandLine}");

            var settings = _settings.Current;
            process = adapter.StartProcess(plan, new BackendProcessContext
            {
                Logger = _logger,
                CaptureOutput = settings.Advanced.EnableBackendOutputCapture,
                ControlHelperPath = _controlHelperPath,
                Environment = BuildEngineEnvironment(settings),
            });

            slot.Process = process;
            _inventory.Register(process);

            var timeout = TimeSpan.FromSeconds(engine.StartupTimeoutSeconds);
            var ready = await adapter.WaitUntilReadyAsync(process, plan, timeout, cancellationToken).ConfigureAwait(false);

            if (!ready.Ready)
            {
                throw new ModelUnavailableException(model.Id, ready.Message ?? "The engine did not become ready.");
            }

            slot.StartedUtc = DateTimeOffset.UtcNow;
            slot.LastUsedUtc = DateTimeOffset.UtcNow;
            slot.LastStopMode = BackendStopMode.None;
            SetState(slot, model.Id, ModelState.Ready, $"ready in {ready.Elapsed.TotalSeconds:0.0}s");

            _logger.Info("lifecycle",
                $"model '{model.Id}' is READY (pid={process.Pid}, port={port}, load time {ready.Elapsed.TotalSeconds:0.0}s)");

            slot.Ready?.TrySetResult(ready);
            ModelLoaded?.Invoke(this, model.Id);
            return ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var failure = BackendReadyResult.Fail(stopwatch.Elapsed, "Model load was cancelled.");
            slot.Ready?.TrySetResult(failure);
            await CleanupAsync(model.Id, slot, process, port).ConfigureAwait(false);
            SetState(slot, model.Id, ModelState.Standby, "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            var message = ex is ModelUnavailableException ? ex.Message : $"Failed to load model '{model.Id}': {ex.Message}";
            _logger.Error("lifecycle", message, ex);
            slot.LastError = message;
            await CleanupAsync(model.Id, slot, process, port).ConfigureAwait(false);
            SetState(slot, model.Id, ModelState.Failed, message);
            var failure = BackendReadyResult.Fail(stopwatch.Elapsed, message);
            slot.Ready?.TrySetResult(failure);
            return failure;
        }
    }

    private async Task CleanupAsync(string modelId, ModelSlot slot, IBackendProcess? process, int port)
    {
        if (process is not null)
        {
            try
            {
                await process.StopAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warn("lifecycle", $"failed to stop the failed engine for model '{modelId}'", ex);
            }

            slot.LastStopMode = process.StopMode;
            _inventory.Unregister(modelId);
            await process.DisposeAsync().ConfigureAwait(false);
        }

        slot.Process = null;
        if (slot.Port is { } reservedPort)
        {
            _ports.Release(reservedPort);
            slot.Port = null;
        }

        if (port > 0)
        {
            _ports.Release(port);
        }
    }

    private async Task<bool> StopInternalAsync(string modelId, string reason, CancellationToken cancellationToken)
    {
        var slot = GetSlot(modelId);
        IBackendProcess? process = null;

        await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (slot.State == ModelState.Standby && slot.Process is null)
            {
                return true;
            }

            if (slot.State == ModelState.Stopping)
            {
                return true;
            }

            SetState(slot, modelId, ModelState.Stopping, reason);
            process = slot.Process;
        }
        finally
        {
            slot.Gate.Release();
        }

        // Give in-flight requests a bounded chance to finish before the engine dies.
        var drainDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (Volatile.Read(ref slot.ActiveRequests) > 0 && DateTimeOffset.UtcNow < drainDeadline)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        if (Volatile.Read(ref slot.ActiveRequests) > 0)
        {
            _logger.Warn("lifecycle",
                $"model '{modelId}' is being stopped with {slot.ActiveRequests} active request(s); those requests will fail.");
        }

        if (process is not null)
        {
            var grace = TimeSpan.FromSeconds(_settings.Current.Lifecycle.ShutdownGraceSeconds);
            await process.StopAsync(grace, cancellationToken).ConfigureAwait(false);
            slot.LastStopMode = process.StopMode;
            _inventory.Unregister(modelId);
            await process.DisposeAsync().ConfigureAwait(false);
        }

        slot.Process = null;
        if (slot.Port is { } port)
        {
            _ports.Release(port);
            slot.Port = null;
        }

        slot.StartedUtc = null;
        SetState(slot, modelId, ModelState.Standby, reason);
        _logger.Info("lifecycle", $"model '{modelId}' unloaded ({reason}); VRAM released");
        ModelUnloaded?.Invoke(this, modelId);
        return true;
    }

    /// <summary>Frees capacity so a new model can be loaded: loaded-model limit, then VRAM pressure.</summary>
    private async Task EnsureCapacityAsync(string incomingModelId, CancellationToken cancellationToken)
    {
        var settings = _settings.Current;

        await EvictUntilAsync(
            incomingModelId,
            () => CountReady() >= settings.Lifecycle.MaxLoadedModels,
            "loaded-model limit",
            cancellationToken).ConfigureAwait(false);

        if (!settings.Lifecycle.VramEvictionEnabled || !_resources.GpuAvailable)
        {
            return;
        }

        var requiredFree = settings.Lifecycle.MinFreeVramMiB * 1024L * 1024L;
        var snapshot = _resources.Current;
        if (snapshot.PrimaryFreeBytes is null || snapshot.PrimaryFreeBytes >= requiredFree)
        {
            return;
        }

        _logger.Warn("resources",
            $"free VRAM {snapshot.PrimaryFreeBytes / (1024.0 * 1024.0):F0} MiB is below the {settings.Lifecycle.MinFreeVramMiB} MiB threshold; evicting idle models");

        await EvictUntilAsync(
            incomingModelId,
            () =>
            {
                var current = _resources.Current;
                return current.PrimaryFreeBytes is { } free && free < requiredFree && HasIdleCandidate(incomingModelId);
            },
            "VRAM pressure",
            cancellationToken,
            refreshResources: true).ConfigureAwait(false);
    }

    private async Task EvictUntilAsync(
        string incomingModelId,
        Func<bool> shouldContinue,
        string reason,
        CancellationToken cancellationToken,
        bool refreshResources = false)
    {
        while (shouldContinue())
        {
            var victim = _slots
                .Where(kv => !string.Equals(kv.Key, incomingModelId, StringComparison.OrdinalIgnoreCase))
                .Where(kv => kv.Value.State == ModelState.Ready && Volatile.Read(ref kv.Value.ActiveRequests) == 0)
                .OrderBy(kv => kv.Value.LastUsedUtc)
                .Select(kv => kv.Key)
                .FirstOrDefault();

            if (victim is null)
            {
                if (reason == "loaded-model limit")
                {
                    throw new ModelCapacityException(
                        incomingModelId,
                        $"The loaded-model limit ({_settings.Current.Lifecycle.MaxLoadedModels}) is reached and no idle model can be evicted.");
                }

                _logger.Warn("resources",
                    "VRAM is low but no idle model can be evicted; attempting the load anyway");
                return;
            }

            _logger.Info("lifecycle", $"evicting model '{victim}' ({reason}, least recently used)");
            await StopInternalAsync(victim, $"evicted: {reason}", cancellationToken).ConfigureAwait(false);

            if (refreshResources)
            {
                await _resources.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool HasIdleCandidate(string incomingModelId) =>
        _slots.Any(kv =>
            !string.Equals(kv.Key, incomingModelId, StringComparison.OrdinalIgnoreCase) &&
            kv.Value.State == ModelState.Ready &&
            Volatile.Read(ref kv.Value.ActiveRequests) == 0);

    private int CountReady() =>
        _slots.Count(kv => kv.Value.State == ModelState.Ready && kv.Value.Process is { IsAlive: true });

    /// <summary>
    /// Outbound proxy settings for the engine itself. The manager never uses a proxy
    /// for its loopback traffic, but an engine may need one to download assets.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? BuildEngineEnvironment(AppSettings settings)
    {
        var network = settings.Network;
        if (string.IsNullOrWhiteSpace(network.HttpProxy) && string.IsNullOrWhiteSpace(network.NoProxy))
        {
            return null;
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(network.HttpProxy))
        {
            environment["HTTP_PROXY"] = network.HttpProxy;
            environment["HTTPS_PROXY"] = network.HttpProxy;
            environment["http_proxy"] = network.HttpProxy;
            environment["https_proxy"] = network.HttpProxy;
        }

        if (!string.IsNullOrWhiteSpace(network.NoProxy))
        {
            environment["NO_PROXY"] = network.NoProxy;
            environment["no_proxy"] = network.NoProxy;
        }

        return environment;
    }

    private IReadOnlyDictionary<string, string> MergeParameters(ModelDefinition model)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var settings = _settings.Current;

        foreach (var (key, value) in settings.ModelParameters.Defaults)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                merged[key] = value;
            }
        }

        foreach (var (key, value) in settings.ModelParameters.ExtraArguments)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                merged[key] = value;
            }
        }

        foreach (var (key, value) in model.Parameters)
        {
            merged[key] = value;
        }

        return merged;
    }

    private ModelRuntimeStatus BuildStatus(ModelDefinition model, ResourceSnapshot snapshot)
    {
        if (!_slots.TryGetValue(model.Id, out var slot))
        {
            return new ModelRuntimeStatus
            {
                ModelId = model.Id,
                DisplayName = model.DisplayName,
                State = ModelState.Standby,
                FilePath = model.FilePath,
                EngineId = model.EngineId,
                EngineName = EngineName(model.EngineId),
            };
        }

        var process = slot.Process;
        var pid = process?.Pid;
        var now = DateTimeOffset.UtcNow;

        return new ModelRuntimeStatus
        {
            ModelId = model.Id,
            DisplayName = model.DisplayName,
            State = slot.State,
            EngineId = model.EngineId,
            EngineName = EngineName(model.EngineId),
            FilePath = model.FilePath,
            Pid = pid,
            Port = process?.Port ?? slot.Port,
            StartedAtUtc = slot.StartedUtc,
            LastUsedAtUtc = slot.LastUsedUtc,
            IdleFor = slot.State == ModelState.Ready ? now - slot.LastUsedUtc : null,
            ActiveRequests = Volatile.Read(ref slot.ActiveRequests),
            VramBytes = pid is { } p ? snapshot.ProcessVram(p) : null,
            WorkingSetBytes = process?.WorkingSetBytes,
            TotalRequests = Interlocked.Read(ref slot.TotalRequests),
            LastStopMode = slot.LastStopMode,
            LastError = slot.LastError,
        };
    }

    private string? EngineName(string engineId) =>
        _settings.Current.Engines.Engines
            .FirstOrDefault(e => string.Equals(e.Id, engineId, StringComparison.OrdinalIgnoreCase))?.Name;

    private ModelDefinition RequireModel(string modelId) =>
        _models.Get(modelId) ?? throw new ModelNotFoundException(modelId);

    private ModelSlot GetSlot(string modelId) => _slots.GetOrAdd(modelId, _ => new ModelSlot());

    private void ReleaseLease(ModelLease lease)
    {
        if (_slots.TryGetValue(lease.ModelId, out var slot))
        {
            var remaining = Interlocked.Decrement(ref slot.ActiveRequests);
            if (remaining < 0)
            {
                Interlocked.Exchange(ref slot.ActiveRequests, 0);
            }

            slot.LastUsedUtc = DateTimeOffset.UtcNow;
        }
    }

    private void SetState(ModelSlot slot, string modelId, ModelState state, string? reason)
    {
        var previous = slot.State;
        slot.State = state;
        if (state == ModelState.Ready && slot.Process is not null)
        {
            slot.Port = slot.Process.Port;
        }

        if (previous != state)
        {
            StateChanged?.Invoke(this, new ModelStateChangedEventArgs
            {
                ModelId = modelId,
                Previous = previous,
                Current = state,
                Reason = reason,
            });
        }
    }

    private sealed class ModelSlot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public ModelState State { get; set; } = ModelState.Standby;

        public IBackendProcess? Process { get; set; }

        public TaskCompletionSource<BackendReadyResult>? Ready { get; set; }

        public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;

        public DateTimeOffset? StartedUtc { get; set; }

        public int ActiveRequests;

        public long TotalRequests;

        public int? Port { get; set; }

        public string? LastError { get; set; }

        public BackendStopMode LastStopMode { get; set; } = BackendStopMode.None;
    }
}
