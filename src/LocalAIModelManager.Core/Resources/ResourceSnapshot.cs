namespace LocalAIModelManager.Core.Resources;

public sealed record GpuSnapshot
{
    public required int Index { get; init; }

    public required string Name { get; init; }

    public long TotalBytes { get; init; }

    public long UsedBytes { get; init; }

    public long FreeBytes { get; init; }

    public double UtilizationPercent { get; init; }

    public double TemperatureCelsius { get; init; }

    public double? PowerWatts { get; init; }

    public double UsedPercent => TotalBytes <= 0 ? 0 : (double)UsedBytes / TotalBytes * 100.0;
}

public sealed record ResourceSnapshot
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool GpuAvailable { get; init; }

    public string? GpuError { get; init; }

    public IReadOnlyList<GpuSnapshot> Gpus { get; init; } = Array.Empty<GpuSnapshot>();

    public double? SystemCpuPercent { get; init; }

    public long? TotalMemoryBytes { get; init; }

    public long? AvailableMemoryBytes { get; init; }

    /// <summary>VRAM in bytes attributed to each backend child process (pid -> bytes).</summary>
    public IReadOnlyDictionary<int, long> ProcessVramBytes { get; init; } = new Dictionary<int, long>();

    public int GpuIndex { get; init; }

    public GpuSnapshot? PrimaryGpu =>
        Gpus.FirstOrDefault(g => g.Index == GpuIndex) ?? Gpus.FirstOrDefault();

    public long? PrimaryFreeBytes => PrimaryGpu?.FreeBytes;

    public long? PrimaryTotalBytes => PrimaryGpu?.TotalBytes;

    public long? ProcessVram(int pid) =>
        ProcessVramBytes.TryGetValue(pid, out var value) ? value : null;

    public static ResourceSnapshot Unavailable(string error) => new()
    {
        GpuAvailable = false,
        GpuError = error,
    };
}
