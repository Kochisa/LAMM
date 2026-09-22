namespace LocalAIModelManager.Core.Backends;

public enum ParameterKind
{
    String,
    Integer,
    Float,
    Boolean,
    Enum,
}

/// <summary>
/// One tunable CLI parameter of an inference engine. Descriptors are never trusted
/// blindly: <see cref="EngineCapabilities"/> only exposes the ones the installed
/// engine actually advertises in its <c>--help</c> output.
/// </summary>
public sealed record ParameterDescriptor
{
    /// <summary>Canonical long flag, e.g. <c>--ctx-size</c>.</summary>
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Grouping used by the Model Parameters page.</summary>
    public required string Category { get; init; }

    public ParameterKind Kind { get; init; } = ParameterKind.String;

    public string? DefaultValue { get; init; }

    public IReadOnlyList<string> AllowedValues { get; init; } = Array.Empty<string>();

    public string? Description { get; init; }

    /// <summary>Alternate spellings (short flags) that also prove support, e.g. <c>-c</c> or <c>-ngl</c>.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();

    /// <summary>Advanced parameters are collapsed behind a toggle in the UI.</summary>
    public bool Advanced { get; init; }

    /// <summary>Value placeholder shown next to the flag.</summary>
    public string? ValueHint { get; init; }

    /// <summary>True when this descriptor was discovered from help output but is unknown to the catalog.</summary>
    public bool DetectedOnly { get; init; }

    public bool AcceptsValue => Kind != ParameterKind.Boolean;
}

/// <summary>Categories rendered as sections on the Model Parameters page.</summary>
public static class ParameterCategories
{
    public const string Context = "Context";
    public const string KvCache = "KV Cache";
    public const string Gpu = "GPU";
    public const string Cpu = "CPU";
    public const string Batching = "Batching";
    public const string Speculative = "Speculative / MTP";
    public const string Server = "Server";
    public const string Detected = "Detected (uncatalogued)";

    public static readonly IReadOnlyList<string> Ordered = new[]
    {
        Context, KvCache, Gpu, Cpu, Batching, Speculative, Server, Detected,
    };
}

/// <summary>
/// Static knowledge about llama.cpp style flags. Support is always confirmed
/// against the installed binary before anything is offered to the user.
/// </summary>
public static class ParameterCatalog
{
    private static readonly string[] KvCacheTypes =
    {
        "f32", "f16", "bf16", "q8_0", "q4_0", "q4_1", "q5_0", "q5_1", "iq4_nl",
    };

    public static IReadOnlyList<ParameterDescriptor> Known { get; } = new List<ParameterDescriptor>
    {
        // ---- Context ---------------------------------------------------------
        new()
        {
            Key = "--ctx-size", DisplayName = "Context length", Category = ParameterCategories.Context,
            Kind = ParameterKind.Integer, DefaultValue = "4096", Aliases = new[] { "-c" },
            ValueHint = "<n>", Description = "Size of the prompt context in tokens.",
        },
        new()
        {
            Key = "--n-predict", DisplayName = "Max tokens to predict", Category = ParameterCategories.Context,
            Kind = ParameterKind.Integer, DefaultValue = "-1", Aliases = new[] { "-n" },
            ValueHint = "<n>", Description = "-1 means unlimited / until context is full.",
        },
        new()
        {
            Key = "--keep", DisplayName = "Tokens to keep on context shift", Category = ParameterCategories.Context,
            Kind = ParameterKind.Integer, ValueHint = "<n>", Advanced = true,
        },
        new()
        {
            Key = "--context-shift", DisplayName = "Context shift (infinite text)", Category = ParameterCategories.Context,
            Kind = ParameterKind.Boolean, Advanced = true,
        },

        // ---- KV cache --------------------------------------------------------
        new()
        {
            Key = "--cache-type-k", DisplayName = "KV cache type (K)", Category = ParameterCategories.KvCache,
            Kind = ParameterKind.Enum, DefaultValue = "f16", AllowedValues = KvCacheTypes,
            ValueHint = "<type>", Description = "Quantizing K reduces VRAM at some quality cost.",
        },
        new()
        {
            Key = "--cache-type-v", DisplayName = "KV cache type (V)", Category = ParameterCategories.KvCache,
            Kind = ParameterKind.Enum, DefaultValue = "f16", AllowedValues = KvCacheTypes,
            ValueHint = "<type>", Description = "V cache quantization requires flash attention.",
        },
        new()
        {
            Key = "--flash-attn", DisplayName = "Flash attention", Category = ParameterCategories.KvCache,
            Kind = ParameterKind.Boolean, Description = "Enables the flash attention kernel.",
        },
        new()
        {
            Key = "--no-kv-offload", DisplayName = "Keep KV cache on CPU", Category = ParameterCategories.KvCache,
            Kind = ParameterKind.Boolean, Advanced = true,
        },
        new()
        {
            Key = "--defrag-thold", DisplayName = "KV cache defrag threshold", Category = ParameterCategories.KvCache,
            Kind = ParameterKind.Float, ValueHint = "<0..1>", Advanced = true,
        },

        // ---- GPU -------------------------------------------------------------
        new()
        {
            Key = "--n-gpu-layers", DisplayName = "GPU layers (offload)", Category = ParameterCategories.Gpu,
            Kind = ParameterKind.Integer, DefaultValue = "0", Aliases = new[] { "-ngl" },
            ValueHint = "<n|auto|all>", Description = "Number of transformer layers offloaded to the GPU.",
        },
        new()
        {
            Key = "--split-mode", DisplayName = "Multi-GPU split mode", Category = ParameterCategories.Gpu,
            Kind = ParameterKind.Enum, DefaultValue = "layer", AllowedValues = new[] { "none", "layer", "row" },
        },
        new()
        {
            Key = "--tensor-split", DisplayName = "Tensor split across GPUs", Category = ParameterCategories.Gpu,
            Kind = ParameterKind.String, Aliases = new[] { "-ts" }, ValueHint = "3,1", Advanced = true,
        },
        new()
        {
            Key = "--main-gpu", DisplayName = "Main GPU index", Category = ParameterCategories.Gpu,
            Kind = ParameterKind.Integer, DefaultValue = "0", Aliases = new[] { "-mg" }, Advanced = true,
        },
        new()
        {
            Key = "--no-mmap", DisplayName = "Disable mmap", Category = ParameterCategories.Gpu,
            Kind = ParameterKind.Boolean, Advanced = true,
        },
        new()
        {
            Key = "--mlock", DisplayName = "Lock model in RAM", Category = ParameterCategories.Gpu,
            Kind = ParameterKind.Boolean, Advanced = true,
        },

        // ---- CPU -------------------------------------------------------------
        new()
        {
            Key = "--threads", DisplayName = "CPU threads", Category = ParameterCategories.Cpu,
            Kind = ParameterKind.Integer, Aliases = new[] { "-t" }, ValueHint = "<n>",
            Description = "Threads used for generation.",
        },
        new()
        {
            Key = "--threads-batch", DisplayName = "CPU threads (batch / prompt)", Category = ParameterCategories.Cpu,
            Kind = ParameterKind.Integer, Aliases = new[] { "-tb" }, ValueHint = "<n>",
        },
        new()
        {
            Key = "--cpu-mask", DisplayName = "CPU affinity mask", Category = ParameterCategories.Cpu,
            Kind = ParameterKind.String, Aliases = new[] { "-C" }, Advanced = true,
        },
        new()
        {
            Key = "--cpu-strict", DisplayName = "Strict CPU placement", Category = ParameterCategories.Cpu,
            Kind = ParameterKind.Boolean, Advanced = true,
        },
        new()
        {
            Key = "--numa", DisplayName = "NUMA strategy", Category = ParameterCategories.Cpu,
            Kind = ParameterKind.Enum, AllowedValues = new[] { "distribute", "isolate", "numactl" }, Advanced = true,
        },
        new()
        {
            Key = "--prio", DisplayName = "Process priority", Category = ParameterCategories.Cpu,
            Kind = ParameterKind.Integer, ValueHint = "<-1..2>", Advanced = true,
        },

        // ---- Batching --------------------------------------------------------
        new()
        {
            Key = "--batch-size", DisplayName = "Logical batch size", Category = ParameterCategories.Batching,
            Kind = ParameterKind.Integer, DefaultValue = "2048", Aliases = new[] { "-b" }, ValueHint = "<n>",
        },
        new()
        {
            Key = "--ubatch-size", DisplayName = "Physical (U) batch size", Category = ParameterCategories.Batching,
            Kind = ParameterKind.Integer, DefaultValue = "512", Aliases = new[] { "-ub" }, ValueHint = "<n>",
            Description = "Smaller micro-batches reduce peak VRAM during prompt processing.",
        },
        new()
        {
            Key = "--parallel", DisplayName = "Parallel sequences", Category = ParameterCategories.Batching,
            Kind = ParameterKind.Integer, DefaultValue = "1", Aliases = new[] { "-np" }, ValueHint = "<n>",
        },
        new()
        {
            Key = "--cont-batching", DisplayName = "Continuous batching", Category = ParameterCategories.Batching,
            Kind = ParameterKind.Boolean,
        },
        new()
        {
            Key = "--no-cont-batching", DisplayName = "Disable continuous batching", Category = ParameterCategories.Batching,
            Kind = ParameterKind.Boolean, Advanced = true,
        },

        // ---- Speculative decoding / MTP --------------------------------------
        new()
        {
            Key = "--model-draft", DisplayName = "Draft model path", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.String, Aliases = new[] { "-md" }, ValueHint = "<file>",
            Description = "Enables speculative decoding with a smaller draft model.",
        },
        new()
        {
            Key = "--draft-max", DisplayName = "Max draft tokens", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.Integer, Aliases = new[] { "--draft" }, ValueHint = "<n>", Advanced = true,
        },
        new()
        {
            Key = "--draft-min", DisplayName = "Min draft tokens", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.Integer, ValueHint = "<n>", Advanced = true,
        },
        new()
        {
            Key = "--draft-p-min", DisplayName = "Min draft probability", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.Float, ValueHint = "<0..1>", Advanced = true,
        },
        new()
        {
            Key = "--n-gpu-layers-draft", DisplayName = "GPU layers for draft model", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.Integer, Aliases = new[] { "-ngld" }, Advanced = true,
        },
        new()
        {
            Key = "--ctx-size-draft", DisplayName = "Draft model context size", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.Integer, Aliases = new[] { "-cd" }, Advanced = true,
        },
        new()
        {
            Key = "--cache-type-k-draft", DisplayName = "Draft KV cache type (K)", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.Enum, AllowedValues = KvCacheTypes, Advanced = true,
        },
        new()
        {
            Key = "--cache-type-v-draft", DisplayName = "Draft KV cache type (V)", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.Enum, AllowedValues = KvCacheTypes, Advanced = true,
        },
        // Multi-token prediction is only offered when the installed build advertises it.
        new()
        {
            Key = "--mtp", DisplayName = "Multi-token prediction (MTP)", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.Boolean,
            Description = "Only shown when the selected llama.cpp build supports MTP.",
        },

        // ---- Server ----------------------------------------------------------
        new()
        {
            Key = "--alias", DisplayName = "Model alias reported by the engine", Category = ParameterCategories.Server,
            Kind = ParameterKind.String, ValueHint = "<name>",
        },
        new()
        {
            Key = "--jinja", DisplayName = "Use the model chat template (jinja)", Category = ParameterCategories.Server,
            Kind = ParameterKind.Boolean,
        },
        new()
        {
            Key = "--embedding", DisplayName = "Embedding mode", Category = ParameterCategories.Server,
            Kind = ParameterKind.Boolean, Advanced = true,
        },
        new()
        {
            Key = "--reasoning-format", DisplayName = "Reasoning output format", Category = ParameterCategories.Server,
            Kind = ParameterKind.Enum, AllowedValues = new[] { "none", "deepseek", "auto" }, Advanced = true,
        },
        new()
        {
            Key = "--timeout", DisplayName = "Read timeout (seconds)", Category = ParameterCategories.Server,
            Kind = ParameterKind.Integer, ValueHint = "<s>", Advanced = true,
        },
        new()
        {
            Key = "--threads-http", DisplayName = "HTTP worker threads", Category = ParameterCategories.Server,
            Kind = ParameterKind.Integer, Advanced = true,
        },
        new()
        {
            Key = "--verbose", DisplayName = "Verbose engine logging", Category = ParameterCategories.Server,
            Kind = ParameterKind.Boolean, Advanced = true,
        },
        new()
        {
            Key = "--no-webui", DisplayName = "Disable the engine built-in web UI", Category = ParameterCategories.Server,
            Kind = ParameterKind.Boolean, Advanced = true,
            Description = "Recommended: the manager is the only intended entry point.",
        },
        new()
        {
            Key = "--chat-template", DisplayName = "Chat template override", Category = ParameterCategories.Server,
            Kind = ParameterKind.String, ValueHint = "<template>", Advanced = true,
        },
    };

    /// <summary>Canonical flags that the manager always supplies itself.</summary>
    public static readonly IReadOnlySet<string> ManagedFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--model", "-m", "--host", "--port", "--alias", "--api-key", "--no-webui",
    };

    public static ParameterDescriptor? Find(string flag) =>
        Known.FirstOrDefault(k =>
            string.Equals(k.Key, flag, StringComparison.OrdinalIgnoreCase) ||
            k.Aliases.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)));
}
