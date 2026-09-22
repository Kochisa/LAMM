using LocalAIModelManager.Core.Models;
using LocalAIModelManager.Core.Processes;

namespace LocalAIModelManager.Core.Backends;

/// <summary>
/// Maps an engine's adapter kind to the adapter implementation. This is the only
/// place that knows which adapter kinds exist, so a new backend is one registration.
/// </summary>
public sealed class BackendAdapterRegistry
{
    private readonly Dictionary<string, IBackendAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);

    public BackendAdapterRegistry(IEnumerable<IBackendAdapter> adapters)
    {
        foreach (var adapter in adapters)
        {
            _adapters[adapter.Kind] = adapter;
        }

        if (_adapters.Count == 0)
        {
            throw new ArgumentException("At least one backend adapter must be registered.", nameof(adapters));
        }
    }

    public IReadOnlyList<IBackendAdapter> All =>
        _adapters.Values.OrderBy(a => a.Kind, StringComparer.OrdinalIgnoreCase).ToList();

    public IBackendAdapter? TryGet(string? kind) =>
        !string.IsNullOrWhiteSpace(kind) && _adapters.TryGetValue(kind, out var adapter) ? adapter : null;

    /// <summary>Resolves an adapter, falling back to the first registration when the kind is empty.</summary>
    public IBackendAdapter Get(string? kind)
    {
        var adapter = TryGet(kind);
        if (adapter is not null)
        {
            return adapter;
        }

        if (string.IsNullOrWhiteSpace(kind))
        {
            return _adapters.Values.First();
        }

        throw new NotSupportedException(
            $"Unknown backend adapter '{kind}'. Registered adapters: {string.Join(", ", _adapters.Keys)}.");
    }

    public EngineCapabilities Inspect(EngineDefinition engine, bool forceRefresh = false) =>
        Get(engine.AdapterKind).Inspect(engine, forceRefresh);

    /// <summary>Default composition: llama.cpp plus the generic OpenAI-compatible adapter.</summary>
    public static BackendAdapterRegistry CreateDefault(BackendJobObject? job = null) => new(new IBackendAdapter[]
    {
        new LlamaCppBackendAdapter(job),
        new GenericOpenAiCompatibleAdapter(job),
    });
}
