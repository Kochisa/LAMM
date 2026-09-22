using LocalAIModelManager.Core.Models;
using LocalAIModelManager.Core.Processes;

namespace LocalAIModelManager.Core.Backends;

/// <summary>
/// The single seam between the manager core and an inference engine.
/// Adding a new backend (vLLM, TensorRT-LLM, a remote OpenAI-compatible server,
/// a future llama.cpp replacement) means implementing this interface and
/// registering it - no UI or lifecycle code changes.
/// </summary>
public interface IBackendAdapter
{
    /// <summary>Stable identifier, see <see cref="BackendAdapterKinds"/>.</summary>
    string Kind { get; }

    string DisplayName { get; }

    /// <summary>File name used when auto-discovering a bundled engine.</summary>
    string DefaultExecutableFileName { get; }

    /// <summary>
    /// Probes the installed executable and returns the parameter set it really
    /// supports. Implementations must cache until the binary changes.
    /// </summary>
    EngineCapabilities Inspect(EngineDefinition engine, bool forceRefresh = false);

    /// <summary>Translates model + engine + parameters into a concrete command line.</summary>
    LaunchPlan BuildLaunchPlan(LaunchRequest request);

    /// <summary>Starts the engine process. The adapter owns process creation semantics.</summary>
    IBackendProcess StartProcess(LaunchPlan plan, BackendProcessContext context);

    /// <summary>Blocks until the engine reports ready (or the timeout elapses).</summary>
    Task<BackendReadyResult> WaitUntilReadyAsync(
        IBackendProcess process,
        LaunchPlan plan,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>Best-effort graceful shutdown of a running engine.</summary>
    Task<bool> RequestGracefulShutdownAsync(IBackendProcess process, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Validates a launch plan without starting anything (Models page "Test").</summary>
    IReadOnlyList<string> ValidateLaunch(LaunchPlan plan, EngineCapabilities capabilities);
}
