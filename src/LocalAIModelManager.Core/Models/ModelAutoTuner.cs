namespace LocalAIModelManager.Core.Models;

public sealed record AutoTuneInput
{
    public required string ModelFilePath { get; init; }

    public bool GpuAvailable { get; init; }

    public long? TotalVramBytes { get; init; }

    public long? FreeVramBytes { get; init; }

    /// <summary>VRAM the manager always tries to keep free (mirrors LifecycleSettings.MinFreeVramMiB).</summary>
    public int MinFreeVramMiB { get; init; } = 1024;

    /// <summary>
    /// Ceiling for this model, as a percentage of total VRAM. It is a safety limit, not a
    /// goal: the tuner aims at <see cref="TargetContextSize"/> and only consults this when
    /// even that does not fit.
    /// </summary>
    public int MaxVramUsagePercent { get; init; } = 70;

    /// <summary>
    /// Context length the tuner aims for. Deliberately modest (Ollama uses a similar
    /// value): because llama.cpp reserves the whole KV cache up front, a generous default
    /// here is what turns a 1.8B model into several GiB of VRAM.
    /// </summary>
    public int TargetContextSize { get; init; } = 8192;

    public int FallbackContextSize { get; init; } = 8192;

    public int FallbackGpuLayers { get; init; } = 99;
}

public sealed record AutoTuneResult
{
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }

    public required IReadOnlyList<string> Explanation { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public GgufMetadata? Metadata { get; init; }

    /// <summary>True when the model metadata or the VRAM numbers were unavailable.</summary>
    public bool UsedFallback { get; init; }

    public string Summary => string.Join("　", Explanation.Take(1));
}

/// <summary>
/// Derives launch parameters from what the model file actually declares plus the VRAM
/// the machine actually has, so importing a model does not turn into manual tuning.
///
/// The governing fact is that llama.cpp reserves the entire KV cache up front for the
/// full context, and that an unset context means "the model's trained context" - which
/// for a long-context model can be dozens of times larger than the weights.
/// </summary>
public static class ModelAutoTuner
{
    private const double GiB = 1024.0 * 1024 * 1024;

    /// <summary>
    /// Graph, logits buffer and CUDA context overhead llama.cpp needs on top of weights
    /// and KV cache. Deliberately an over-estimate so the reported total stays honest.
    /// </summary>
    private const long ComputeReserveBytes = (long)(1.0 * GiB);

    /// <summary>
    /// Multiplier applied to the raw KV size to cover the graph and logits buffers, which
    /// grow with the context even though they are not part of the KV cache itself.
    /// </summary>
    private const double KvBufferFactor = 1.10;

    private const int MinimumContext = 2048;

    private static readonly int[] NiceContexts =
    {
        2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288,
    };

    public static AutoTuneResult Tune(AutoTuneInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var metadata = GgufMetadataReader.TryRead(input.ModelFilePath);
        var explanation = new List<string>();
        var warnings = new List<string>();

        var fallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["--n-gpu-layers"] = input.FallbackGpuLayers.ToString(),
            ["--ctx-size"] = input.FallbackContextSize.ToString(),
        };

        if (metadata is null)
        {
            warnings.Add(Loc.T("autotune.warn.noMetadata"));
            explanation.Add(Loc.T("autotune.fallback.text", input.FallbackContextSize, input.FallbackGpuLayers));
            return new AutoTuneResult
            {
                Parameters = fallback,
                Explanation = explanation,
                Warnings = warnings,
                UsedFallback = true,
            };
        }

        DescribeModel(metadata, explanation);

        var kvPerToken = metadata.KvBytesPerToken();
        if (kvPerToken is not { } kvBytes)
        {
            warnings.Add(Loc.T("autotune.warn.noKvGeometry"));
            explanation.Add(Loc.T("autotune.fallback.context", input.FallbackContextSize, input.FallbackGpuLayers));
            return new AutoTuneResult
            {
                Parameters = fallback,
                Explanation = explanation,
                Warnings = warnings,
                Metadata = metadata,
                UsedFallback = true,
            };
        }

        explanation.Add(Loc.T("autotune.kvPerToken", kvBytes / 1024.0));

        if (!input.GpuAvailable || input.TotalVramBytes is not { } totalVram || totalVram <= 0)
        {
            warnings.Add(Loc.T("autotune.warn.noGpuInfo"));
            explanation.Add(Loc.T("autotune.fallback.context", input.FallbackContextSize, input.FallbackGpuLayers));
            return new AutoTuneResult
            {
                Parameters = fallback,
                Explanation = explanation,
                Warnings = warnings,
                Metadata = metadata,
                UsedFallback = true,
            };
        }

        var weights = metadata.FileSizeBytes;
        var keepFree = input.MinFreeVramMiB * 1024L * 1024L;
        var usagePercent = Math.Clamp(input.MaxVramUsagePercent, 20, 100);
        var ceiling = Math.Min((long)(totalVram * (usagePercent / 100.0)), totalVram - keepFree);
        var headroom = ceiling - ComputeReserveBytes;
        if (headroom <= 0)
        {
            headroom = (long)(totalVram * 0.5) - ComputeReserveBytes;
            warnings.Add(Loc.T("autotune.warn.noRoom"));
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trainedContext = metadata.ContextLength is { } c and > 0 ? c : input.FallbackContextSize;

        // The context length is the primary knob and it is a TARGET, not "whatever fits":
        // llama.cpp reserves the whole KV cache up front, so deriving the context from
        // spare VRAM is what made a 1.8B model fill a 24 GiB card. The ceiling above is
        // only a safety limit for when even the modest target does not fit.
        var targetContext = Math.Clamp(input.TargetContextSize, MinimumContext, Math.Max(MinimumContext, trainedContext));
        var desiredContext = Math.Min(targetContext, trainedContext);

        explanation.Add(Loc.T(
            "autotune.memory",
            totalVram / GiB,
            input.FreeVramBytes is { } free ? Loc.T("autotune.memoryFree", free / GiB) : string.Empty,
            usagePercent,
            ceiling / GiB));
        explanation.Add(Loc.T("autotune.targetContext", desiredContext, trainedContext));

        var kvForDesired = (long)(desiredContext * kvBytes * KvBufferFactor);
        var minimumKv = kvBytes * MinimumContext;

        int context;
        int gpuLayers;

        if (weights + kvForDesired + ComputeReserveBytes <= ceiling)
        {
            // The modest target fits comfortably - keep the model fully on the GPU and do
            // NOT spend the leftover VRAM on a longer context.
            gpuLayers = ParseLayers(input.FallbackGpuLayers);
            context = desiredContext;
            explanation.Add(Loc.T("autotune.fits", weights / GiB, kvForDesired / GiB, gpuLayers, context));
        }
        else
        {
            // Even the target does not fit: shrink the context first, then the layer count.
            var affordable = (long)((headroom - weights) / (kvBytes * KvBufferFactor));
            if (affordable >= MinimumContext)
            {
                gpuLayers = ParseLayers(input.FallbackGpuLayers);
                context = PickNiceContext((int)affordable, trainedContext, warnings);
                explanation.Add(Loc.T(
                    "autotune.shrunk",
                    desiredContext, weights / GiB, kvForDesired / GiB, ceiling / GiB, context, gpuLayers));
            }
            else
            {
                var reserveKv = kvBytes * MinimumContext;
                var forWeights = Math.Max(0L, headroom - reserveKv);
                var fraction = weights <= 0 ? 1.0 : Math.Min(1.0, (double)forWeights / weights);
                gpuLayers = (int)Math.Floor(metadata.BlockCount!.Value * fraction);
                context = PickNiceContext(MinimumContext, trainedContext, warnings);

                explanation.Add(Loc.T(
                    "autotune.partialOffload",
                    weights / GiB, ceiling / GiB, gpuLayers, metadata.BlockCount));

                if (gpuLayers <= 0)
                {
                    warnings.Add(Loc.T("autotune.warn.noLayers"));
                    gpuLayers = 0;
                }
                else if (gpuLayers < metadata.BlockCount)
                {
                    warnings.Add(Loc.T("autotune.warn.partialLayers", gpuLayers, metadata.BlockCount));
                }
            }
        }

        parameters["--n-gpu-layers"] = gpuLayers.ToString();
        parameters["--ctx-size"] = context.ToString();

        var estimatedKv = (long)context * kvBytes;
        var estimatedTotal = weights + estimatedKv + ComputeReserveBytes;
        explanation.Add(Loc.T(
            "autotune.result",
            context,
            (estimatedKv / GiB).ToString("F2"),
            gpuLayers,
            (estimatedTotal / GiB).ToString("F2"),
            (totalVram / GiB).ToString("F1"),
            (estimatedTotal * 100.0 / totalVram).ToString("F0"),
            usagePercent));

        if (context < trainedContext)
        {
            var reason = context == desiredContext
                ? Loc.T("autotune.reasonTarget", trainedContext, context)
                : Loc.T("autotune.reasonShrunk", trainedContext, context);

            explanation.Add(reason + Loc.T("autotune.longContextHint"));
        }

        return new AutoTuneResult
        {
            Parameters = parameters,
            Explanation = explanation,
            Warnings = warnings,
            Metadata = metadata,
        };
    }

    private static int ParseLayers(int fallback) => fallback > 0 ? fallback : 99;

    private static void DescribeModel(GgufMetadata metadata, List<string> explanation)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(metadata.Architecture))
        {
            parts.Add(metadata.Architecture!);
        }

        if (metadata.BlockCount is { } layers)
        {
            parts.Add(Loc.T("autotune.meta.layers", layers));
        }

        if (metadata.EffectiveHeadCountKv is { } kvHeads)
        {
            parts.Add(Loc.T("autotune.meta.kvHeads", kvHeads));
        }

        if (metadata.ContextLength is { } trained)
        {
            parts.Add(Loc.T("autotune.meta.trainedContext", trained));
        }

        parts.Add(Loc.T("autotune.meta.weights", (metadata.FileSizeBytes / GiB).ToString("F2")));
        explanation.Add(Loc.T("autotune.modelMetadata", string.Join(" · ", parts)));
    }

    private static int PickNiceContext(int affordable, int trainedContext, List<string> warnings)
    {
        var ceiling = Math.Min(affordable, trainedContext);
        if (ceiling < MinimumContext)
        {
            warnings.Add(Loc.T("autotune.warn.contextFloor", Math.Max(ceiling, 0), MinimumContext));
            return MinimumContext;
        }

        var picked = MinimumContext;
        foreach (var candidate in NiceContexts)
        {
            if (candidate <= ceiling)
            {
                picked = candidate;
            }
            else
            {
                break;
            }
        }

        return picked;
    }
}
