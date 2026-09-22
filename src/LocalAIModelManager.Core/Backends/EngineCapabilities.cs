namespace LocalAIModelManager.Core.Backends;

/// <summary>
/// Result of probing an installed engine binary (<c>llama-server --help</c>).
/// Nothing about the parameter set is assumed to be stable: the whole descriptor
/// list is derived from the binary that is actually installed.
/// </summary>
public sealed record EngineCapabilities
{
    public required string EngineId { get; init; }

    /// <summary>Executable the probe ran against, captured for staleness detection.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>File identity of the probed binary (length + write time) used to invalidate the cache.</summary>
    public string? ExecutableFingerprint { get; init; }

    public string? Version { get; init; }

    public bool IsAvailable { get; init; }

    public string? Error { get; init; }

    public DateTimeOffset ProbedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string? RawHelpText { get; init; }

    /// <summary>Parameters supported by this build, catalog order preserved.</summary>
    public IReadOnlyList<ParameterDescriptor> Parameters { get; init; } = Array.Empty<ParameterDescriptor>();

    public IReadOnlySet<string> SupportedFlags { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> DetectedFlags { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool Supports(string flag)
    {
        if (SupportedFlags.Contains(flag))
        {
            return true;
        }

        var descriptor = ParameterCatalog.Find(flag);
        return descriptor is not null &&
               (SupportedFlags.Contains(descriptor.Key) || descriptor.Aliases.Any(SupportedFlags.Contains));
    }

    public ParameterDescriptor? Find(string flag) =>
        Parameters.FirstOrDefault(p => string.Equals(p.Key, flag, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ParameterDescriptor> ByCategory(string category) =>
        Parameters.Where(p => string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase));

    public static EngineCapabilities Unavailable(string engineId, string? executablePath, string error) => new()
    {
        EngineId = engineId,
        ExecutablePath = executablePath,
        IsAvailable = false,
        Error = error,
        SupportedFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        DetectedFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    };
}
