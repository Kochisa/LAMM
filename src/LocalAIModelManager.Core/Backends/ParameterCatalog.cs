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
        Context, Gpu, Cpu, Batching, KvCache, Speculative, Server, Detected,
    };
}

/// <summary>
/// The handful of parameters the user interface exposes directly. Everything else is
/// reachable through the "Other" free-form argument box instead of dozens of controls,
/// which keeps the page readable and keeps obscure flags out of the way.
/// </summary>
public static class EssentialParameters
{
    public const string ContextSize = "--ctx-size";
    public const string GpuLayers = "--n-gpu-layers";
    public const string Threads = "--threads";
    public const string BatchSize = "--batch-size";
    public const string UBatchSize = "--ubatch-size";
    public const string CacheTypeK = "--cache-type-k";
    public const string CacheTypeV = "--cache-type-v";

    /// <summary>Order in which they appear in the UI.</summary>
    public static readonly IReadOnlyList<string> Ordered = new[]
    {
        ContextSize, GpuLayers, Threads, BatchSize, UBatchSize, CacheTypeK, CacheTypeV,
    };

    public static bool IsEssential(string key) =>
        Ordered.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
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
            Kind = ParameterKind.Integer, Aliases = new[] { "-c" },
            ValueHint = "<n>",
            Description = "Prompt context in tokens. Unset means llama.cpp decides - and it then uses "
                        + "the model's trained context, which can be enormous: the KV cache is reserved "
                        + "up front for the full context (about 64 KB per token for a 32-layer / "
                        + "4-KV-head model, so 256K context = 16 GiB). Set it explicitly to bound VRAM.",
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
            ValueHint = "<type>",
            Description = "Quantizing the KV cache cuts its VRAM roughly in half (q8_0) or more (q4_0). "
                        + "The KV cache is often far larger than the model itself.",
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
            Kind = ParameterKind.Boolean,
            Description = "Enables the flash attention kernel. Required by some builds before the V "
                        + "cache may be quantized with --cache-type-v.",
        },
        new()
        {
            Key = "--no-kv-offload", DisplayName = "Keep KV cache on CPU", Category = ParameterCategories.KvCache,
            Kind = ParameterKind.Boolean,
            Description = "Moves the KV cache to system RAM instead of VRAM. Great for small models "
                        + "with a huge context; generation gets slower.",
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
            Kind = ParameterKind.Integer, Aliases = new[] { "-ngl" },
            ValueHint = "<n|auto|all>",
            Description = "Transformer layers offloaded to the GPU. Unset means llama.cpp's own default, "
                        + "which is 0 - pure CPU. Set 99 (or 'all') to offload everything, or 0 to be explicit.",
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
        // Multi-token prediction is self-speculation: the model drafts several tokens
        // itself and verifies them in one pass. The draft-model flags below are the
        // older, separate-model route to the same speedup. Both families get their own
        // section in the UI, and any additional flag a build advertises whose name
        // contains mtp/draft/speculative is listed there too (see IsSpeculative), so a
        // newer llama.cpp does not need a code change.
        new()
        {
            Key = "--mtp", DisplayName = "Multi-token prediction (MTP)", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.Boolean,
            Description = "Self-speculative decoding: the model proposes several tokens per step and "
                        + "verifies them in one forward pass. Needs a model that carries MTP weights. "
                        + "Takes extra VRAM for the MTP layers; costs nothing when the build or the "
                        + "model does not support it, because the flag is then simply not sent.",
        },
        new()
        {
            Key = "--model-draft", DisplayName = "Draft model path", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.String, Aliases = new[] { "-md" }, ValueHint = "<file>",
            Description = "Speculative decoding with a separate, smaller draft model. Needs its own "
                        + "GGUF file on disk; the manager never picks one for you.",
        },
        new()
        {
            Key = "--draft-max", DisplayName = "Max draft tokens", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.Integer, Aliases = new[] { "--draft" }, ValueHint = "<n>",
            Description = "Upper bound on how many tokens are drafted per step. Higher drafts more "
                        + "aggressively; wasted drafts only cost time, not correctness.",
        },
        new()
        {
            Key = "--draft-min", DisplayName = "Min draft tokens", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.Integer, ValueHint = "<n>",
        },
        new()
        {
            Key = "--draft-p-min", DisplayName = "Min draft probability", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.Float, ValueHint = "<0..1>",
        },
        new()
        {
            Key = "--n-gpu-layers-draft", DisplayName = "GPU layers for draft model", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.Integer, Aliases = new[] { "-ngld" }, ValueHint = "<n>",
        },
        new()
        {
            Key = "--ctx-size-draft", DisplayName = "Draft model context size", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.Integer, Aliases = new[] { "-cd" }, ValueHint = "<n>",
        },
        new()
        {
            Key = "--cache-type-k-draft", DisplayName = "Draft KV cache type (K)", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.Enum, AllowedValues = KvCacheTypes,
        },
        new()
        {
            Key = "--cache-type-v-draft", DisplayName = "Draft KV cache type (V)", Category = ParameterCategories.Speculative,
            Kind = ParameterKind.Enum, AllowedValues = KvCacheTypes,
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

    /// <summary>True when the descriptor belongs to the MTP / speculative-decoding family.</summary>
    public static bool IsSpeculative(ParameterDescriptor descriptor) =>
        string.Equals(descriptor.Category, ParameterCategories.Speculative, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="flag"/> belongs to the MTP / speculative-decoding family.
    ///
    /// Catalog membership is checked first, so the short spellings (<c>-md</c>, <c>-ngld</c>,
    /// <c>-cd</c>) are classified correctly. Everything else is matched by name, which is how
    /// a build that ships MTP flags this catalog has never seen still gets them written out
    /// in the dedicated section instead of being buried in the free-form "Other" box.
    /// </summary>
    public static bool IsSpeculative(string flag)
    {
        if (string.IsNullOrWhiteSpace(flag))
        {
            return false;
        }

        if (Find(flag) is { } known)
        {
            return IsSpeculative(known);
        }

        var name = flag.TrimStart('-');
        return SpeculativeKeywords.Any(keyword => name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Name fragments that identify speculative decoding in llama.cpp style flags:
    /// MTP itself, the draft-model options it grew out of, and the alternative
    /// speculative schemes some builds carry.
    /// </summary>
    private static readonly string[] SpeculativeKeywords =
    {
        "mtp", "draft", "speculat", "eagle", "medusa", "lookahead", "ngram",
    };

    /// <summary>
    /// Every MTP / speculative-decoding flag that can be offered for
    /// <paramref name="capabilities"/>, in three tiers:
    ///
    /// 1. catalog entries the build actually advertises - editable;
    /// 2. flags the build advertises that the catalog has never seen, matched by name
    ///    (any <c>--mtp-*</c>, <c>--draft-*</c>, <c>--speculative-*</c>, ... switch), so a
    ///    newer llama.cpp writes its own new MTP options out without a code change;
    /// 3. known family members this build does not advertise, kept visible but
    ///    unsupported, so "MTP is missing" reads as "this build does not have it" rather
    ///    than as a missing feature of the manager.
    /// </summary>
    public static IReadOnlyList<ParameterDescriptor> SpeculativeFor(EngineCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var result = new List<ParameterDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in capabilities.Parameters.Where(d => !d.DetectedOnly))
        {
            if (IsSpeculative(descriptor) && seen.Add(descriptor.Key))
            {
                result.Add(descriptor);
            }
        }

        foreach (var descriptor in capabilities.Parameters.Where(d => d.DetectedOnly))
        {
            if (IsSpeculative(descriptor.Key) && seen.Add(descriptor.Key))
            {
                result.Add(descriptor);
            }
        }

        if (capabilities.IsAvailable)
        {
            foreach (var descriptor in Known.Where(IsSpeculative))
            {
                if (!capabilities.Supports(descriptor.Key) && seen.Add(descriptor.Key))
                {
                    result.Add(descriptor);
                }
            }
        }

        return result;
    }
}
