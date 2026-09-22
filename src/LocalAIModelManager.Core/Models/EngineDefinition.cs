namespace LocalAIModelManager.Core.Models;

/// <summary>Adapter kinds understood by <c>BackendAdapterRegistry</c>.</summary>
public static class BackendAdapterKinds
{
    /// <summary>llama.cpp <c>llama-server</c> (and anything that speaks its CLI/HTTP surface).</summary>
    public const string LlamaCpp = "llamacpp";

    /// <summary>Generic OpenAI-compatible local server (vLLM, LM Studio, text-generation-webui, ...).</summary>
    public const string OpenAiCompatible = "openai-compatible";
}

/// <summary>
/// A replaceable inference engine installation. An engine is *just* an executable
/// plus its adapter kind and CLI conventions, so upgrading llama.cpp means adding a
/// new engine row and pointing models at it - no code change, no reinstall.
/// </summary>
public sealed class EngineDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>See <see cref="BackendAdapterKinds"/>.</summary>
    public string AdapterKind { get; set; } = BackendAdapterKinds.LlamaCpp;

    /// <summary>Absolute path to the engine executable (for example <c>llama-server.exe</c>).</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>Optional working directory; defaults to the executable directory.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Extra CLI flags appended to every launch (flag -> value, empty value = bare flag).
    /// Used for engine specific switches such as <c>--flash-attn</c>.
    /// </summary>
    public Dictionary<string, string> ExtraArguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Relative path used as readiness probe.</summary>
    public string HealthPath { get; set; } = "/health";

    public string ChatCompletionsPath { get; set; } = "/v1/chat/completions";

    public string CompletionsPath { get; set; } = "/v1/completions";

    public string EmbeddingsPath { get; set; } = "/v1/embeddings";

    public string ModelsPath { get; set; } = "/v1/models";

    /// <summary>How long loading may take before the supervisor gives up.</summary>
    public int StartupTimeoutSeconds { get; set; } = 180;

    /// <summary>Grace period for a graceful stop before a forced kill.</summary>
    public int ShutdownGraceSeconds { get; set; } = 10;

    /// <summary>Free-form labels, e.g. "bundled" or "upstream-b4200".</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Cached result of the last <c>--help</c> capability probe.</summary>
    public string? DetectedVersion { get; set; }

    public string? LastProbeError { get; set; }

    public DateTimeOffset? LastProbedAtUtc { get; set; }

    public EngineDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        AdapterKind = AdapterKind,
        ExecutablePath = ExecutablePath,
        WorkingDirectory = WorkingDirectory,
        ExtraArguments = new Dictionary<string, string>(ExtraArguments, StringComparer.OrdinalIgnoreCase),
        HealthPath = HealthPath,
        ChatCompletionsPath = ChatCompletionsPath,
        CompletionsPath = CompletionsPath,
        EmbeddingsPath = EmbeddingsPath,
        ModelsPath = ModelsPath,
        StartupTimeoutSeconds = StartupTimeoutSeconds,
        ShutdownGraceSeconds = ShutdownGraceSeconds,
        Tags = new List<string>(Tags),
        DetectedVersion = DetectedVersion,
        LastProbeError = LastProbeError,
        LastProbedAtUtc = LastProbedAtUtc,
    };

    public void Normalize()
    {
        Id = (Id ?? string.Empty).Trim();
        Name = string.IsNullOrWhiteSpace(Name) ? Id : Name.Trim();
        AdapterKind = string.IsNullOrWhiteSpace(AdapterKind) ? BackendAdapterKinds.LlamaCpp : AdapterKind.Trim();
        ExecutablePath = (ExecutablePath ?? string.Empty).Trim();
        WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory!.Trim();
        HealthPath = string.IsNullOrWhiteSpace(HealthPath) ? "/health" : HealthPath.Trim();
        ChatCompletionsPath = string.IsNullOrWhiteSpace(ChatCompletionsPath) ? "/v1/chat/completions" : ChatCompletionsPath.Trim();
        CompletionsPath = string.IsNullOrWhiteSpace(CompletionsPath) ? "/v1/completions" : CompletionsPath.Trim();
        EmbeddingsPath = string.IsNullOrWhiteSpace(EmbeddingsPath) ? "/v1/embeddings" : EmbeddingsPath.Trim();
        ModelsPath = string.IsNullOrWhiteSpace(ModelsPath) ? "/v1/models" : ModelsPath.Trim();
        StartupTimeoutSeconds = StartupTimeoutSeconds <= 0 ? 180 : StartupTimeoutSeconds;
        ShutdownGraceSeconds = ShutdownGraceSeconds <= 0 ? 10 : ShutdownGraceSeconds;
        Tags ??= new List<string>();
        ExtraArguments ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var cleaned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in ExtraArguments)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                cleaned[key.Trim()] = (value ?? string.Empty).Trim();
            }
        }

        ExtraArguments = cleaned;
    }

    public bool ExecutableExists() => !string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath);
}
