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
            warnings.Add("无法读取模型元数据（不是有效的 GGUF 文件？），已使用保守默认值。");
            explanation.Add($"未能读取模型元数据，采用默认值：--ctx-size {input.FallbackContextSize}、--n-gpu-layers {input.FallbackGpuLayers}。");
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
            warnings.Add("模型元数据缺少层数/KV 头/注意力维度，无法计算 KV cache，已使用保守默认值。");
            explanation.Add($"采用默认值：--ctx-size {input.FallbackContextSize}、--n-gpu-layers {input.FallbackGpuLayers}。");
            return new AutoTuneResult
            {
                Parameters = fallback,
                Explanation = explanation,
                Warnings = warnings,
                Metadata = metadata,
                UsedFallback = true,
            };
        }

        explanation.Add($"KV cache：每 token 约 {kvBytes / 1024.0:F1} KB（f16），按上下文长度一次性预留。");

        if (!input.GpuAvailable || input.TotalVramBytes is not { } totalVram || totalVram <= 0)
        {
            warnings.Add("未检测到可用的 GPU 显存信息，无法按显存自动配比。");
            explanation.Add($"采用默认值：--ctx-size {input.FallbackContextSize}、--n-gpu-layers {input.FallbackGpuLayers}。");
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
            warnings.Add("按显存使用上限扣除计算缓冲后没有剩余空间，已按总显存的 50% 估算可用量。");
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trainedContext = metadata.ContextLength is { } c and > 0 ? c : input.FallbackContextSize;

        // The context length is the primary knob and it is a TARGET, not "whatever fits":
        // llama.cpp reserves the whole KV cache up front, so deriving the context from
        // spare VRAM is what made a 1.8B model fill a 24 GiB card. The ceiling above is
        // only a safety limit for when even the modest target does not fit.
        var targetContext = Math.Clamp(input.TargetContextSize, MinimumContext, Math.Max(MinimumContext, trainedContext));
        var desiredContext = Math.Min(targetContext, trainedContext);

        explanation.Add(
            $"显存：共 {totalVram / GiB:F1} GiB" +
            (input.FreeVramBytes is { } free ? $"（当前空闲 {free / GiB:F1} GiB）" : string.Empty) +
            $"；单模型安全上限 {usagePercent}% = {ceiling / GiB:F1} GiB（仅用于判断是否放得下）。");
        explanation.Add($"目标上下文：{desiredContext}（可在「设置 → 资源」里调整）；模型训练上下文 {trainedContext}。");

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
            explanation.Add(
                $"权重 {weights / GiB:F2} GiB + 目标上下文的 KV cache 约 {kvForDesired / GiB:F2} GiB 都在限额内，" +
                $"因此 --n-gpu-layers {gpuLayers}（全部层）、--ctx-size {context}（不额外吃满显存）。");
        }
        else
        {
            // Even the target does not fit: shrink the context first, then the layer count.
            var affordable = (long)((headroom - weights) / (kvBytes * KvBufferFactor));
            if (affordable >= MinimumContext)
            {
                gpuLayers = ParseLayers(input.FallbackGpuLayers);
                context = PickNiceContext((int)affordable, trainedContext, warnings);
                explanation.Add(
                    $"目标上下文 {desiredContext} 放不下（权重 {weights / GiB:F2} GiB + KV {kvForDesired / GiB:F2} GiB " +
                    $"超过限额 {ceiling / GiB:F1} GiB），已收敛到 --ctx-size {context}；--n-gpu-layers {gpuLayers}（全部层）。");
            }
            else
            {
                var reserveKv = kvBytes * MinimumContext;
                var forWeights = Math.Max(0L, headroom - reserveKv);
                var fraction = weights <= 0 ? 1.0 : Math.Min(1.0, (double)forWeights / weights);
                gpuLayers = (int)Math.Floor(metadata.BlockCount!.Value * fraction);
                context = PickNiceContext(MinimumContext, trainedContext, warnings);

                explanation.Add(
                    $"权重 {weights / GiB:F2} GiB 已超过限额 {ceiling / GiB:F1} GiB，无法整模型放进显存：" +
                    $"按比例卸载 {gpuLayers}/{metadata.BlockCount} 层，其余留在 CPU（速度会明显下降）。");

                if (gpuLayers <= 0)
                {
                    warnings.Add("显存不足以放进任何一层，该模型将完全在 CPU 上运行。");
                    gpuLayers = 0;
                }
                else if (gpuLayers < metadata.BlockCount)
                {
                    warnings.Add($"仅 {gpuLayers}/{metadata.BlockCount} 层能放进显存，其余在 CPU 上；" +
                                 "可用更小的量化版本，或用 --cache-type-k/-v q8_0 压缩 KV cache 腾出显存。");
                }
            }
        }

        parameters["--n-gpu-layers"] = gpuLayers.ToString();
        parameters["--ctx-size"] = context.ToString();

        var estimatedKv = (long)context * kvBytes;
        var estimatedTotal = weights + estimatedKv + ComputeReserveBytes;
        explanation.Add(
            $"结果：--ctx-size {context}（KV 约 {estimatedKv / GiB:F2} GiB）、--n-gpu-layers {gpuLayers}；" +
            $"预计显存占用约 {estimatedTotal / GiB:F2} GiB / {totalVram / GiB:F1} GiB" +
            $"（占显卡 {estimatedTotal * 100.0 / totalVram:F0}%，上限 {usagePercent}%）。");

        if (context < trainedContext)
        {
            var reason = context == desiredContext
                ? $"该模型训练上下文为 {trainedContext}，这里按保守的目标上下文 {context} 配置（不是显存不够，而是刻意留余量）"
                : $"该模型训练上下文为 {trainedContext}，目标上下文放不下，已收敛到 {context}";

            explanation.Add(
                reason +
                "。需要更长上下文时：把「设置 → 资源 → 自动调参的目标上下文」调大，" +
                "或用 --cache-type-k q8_0 与 --cache-type-v q8_0 把 KV 压到约一半（同样显存约可支持 2 倍上下文），" +
                "或勾选 --no-kv-offload 把 KV cache 放到内存。");
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
            parts.Add($"{layers} 层");
        }

        if (metadata.EffectiveHeadCountKv is { } kvHeads)
        {
            parts.Add($"{kvHeads} 个 KV 头");
        }

        if (metadata.ContextLength is { } trained)
        {
            parts.Add($"训练上下文 {trained}");
        }

        parts.Add($"权重 {metadata.FileSizeBytes / GiB:F2} GiB");
        explanation.Add("模型元数据：" + string.Join(" · ", parts) + "。");
    }

    private static int PickNiceContext(int affordable, int trainedContext, List<string> warnings)
    {
        var ceiling = Math.Min(affordable, trainedContext);
        if (ceiling < MinimumContext)
        {
            warnings.Add($"按当前显存只能容纳约 {Math.Max(ceiling, 0)} token 的上下文，已使用最小值 {MinimumContext}。");
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
