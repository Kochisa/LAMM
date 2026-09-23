using LocalAIModelManager.Core.Models;

namespace LocalAIModelManager.Core.Backends;

/// <summary>Input for <see cref="IBackendAdapter.BuildLaunchPlan"/>.</summary>
public sealed record LaunchRequest
{
    public required ModelDefinition Model { get; init; }

    public required EngineDefinition Engine { get; init; }

    /// <summary>
    /// Merged parameter set: global user defaults overlaid with per-model overrides.
    /// Only keys a human configured are present - an unset parameter is never invented.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }

    /// <summary>
    /// Advanced CLI lines typed by hand (global + per model). Passed through verbatim,
    /// in order, after the structured parameters.
    /// </summary>
    public IReadOnlyList<string> AdditionalArguments { get; init; } = Array.Empty<string>();

    public required int Port { get; init; }

    /// <summary>Loopback address the engine must bind to. Never routable.</summary>
    public required string BindHost { get; init; }

    public EngineCapabilities? Capabilities { get; init; }
}

/// <summary>Fully resolved command line for one engine instance.</summary>
public sealed record LaunchPlan
{
    public required string ExecutablePath { get; init; }

    public required string WorkingDirectory { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required string BindHost { get; init; }

    public required int Port { get; init; }

    public required string HealthPath { get; init; }

    public string? ModelId { get; init; }

    public string? EngineId { get; init; }

    /// <summary>Non-fatal notes: parameters dropped because the engine does not support them.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public string FunctionallyEquivalentBaseUrl => $"http://{BindHost}:{Port}";

    public string CommandLine => ExecutablePath + " " + string.Join(' ', Arguments.Select(Quote));

    private static string Quote(string value) =>
        value.Contains(' ') ? "\"" + value + "\"" : value;
}

/// <summary>Address of one running engine instance.</summary>
public sealed record BackendEndpoint
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public required string HealthPath { get; init; }

    public string BaseUrl => $"http://{Host}:{Port}";

    public string HealthUrl => BaseUrl + (HealthPath.StartsWith('/') ? HealthPath : "/" + HealthPath);
}

public sealed record BackendReadyResult
{
    public required bool Ready { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public string? Message { get; init; }

    public static BackendReadyResult Ok(TimeSpan elapsed) => new() { Ready = true, Elapsed = elapsed };

    public static BackendReadyResult Fail(TimeSpan elapsed, string message) =>
        new() { Ready = false, Elapsed = elapsed, Message = message };
}
