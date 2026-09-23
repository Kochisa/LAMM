using LocalAIModelManager.Core.Configuration;
using LocalAIModelManager.Core.Models;
using LocalAIModelManager.Core.Resources;

namespace LocalAIModelManager.Core.Runtime;

public sealed record GatewayStatusInfo
{
    public bool IsRunning { get; init; }

    public string? Host { get; init; }

    public int? Port { get; init; }

    public string? BaseUrl { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public long TotalRequests { get; init; }

    public long ActiveRequests { get; init; }

    public bool ApiKeyEnabled { get; init; }

    public bool LanAccessEnabled { get; init; }

    public string? LastError { get; init; }
}

public sealed record EngineStatusInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string AdapterKind { get; init; }

    public required string ExecutablePath { get; init; }

    public bool ExecutableExists { get; init; }

    public bool IsAvailable { get; init; }

    public string? Version { get; init; }

    public string? Error { get; init; }

    public DateTimeOffset? LastProbedAtUtc { get; init; }

    public int SupportedParameterCount { get; init; }

    public int ModelCount { get; init; }

    public bool IsSelected { get; init; }

    /// <summary>Compute devices the engine itself reports (<c>--list-devices</c>).</summary>
    public IReadOnlyList<string> AvailableDevices { get; init; } = Array.Empty<string>();

    /// <summary><c>null</c> when the engine build cannot answer the device probe.</summary>
    public bool? HasGpuDevice { get; init; }

    public string GpuDevicesText => HasGpuDevice switch
    {
        true when AvailableDevices.Count > 0 => string.Join("；", AvailableDevices),
        true => "已检测到 GPU 设备",
        false => "未检测到任何 GPU 设备（将回退到 CPU）",
        _ => "未知（该构建不支持 --list-devices）",
    };
}

/// <summary>Everything the Runtime Status page renders, in one immutable snapshot.</summary>
public sealed record RuntimeStatusSnapshot
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public required GatewayStatusInfo Gateway { get; init; }

    public IReadOnlyList<ModelRuntimeStatus> Models { get; init; } = Array.Empty<ModelRuntimeStatus>();

    public IReadOnlyList<BackendProcessInfo> Processes { get; init; } = Array.Empty<BackendProcessInfo>();

    public IReadOnlyList<BackendProcessInfo> ExitedProcesses { get; init; } = Array.Empty<BackendProcessInfo>();

    public IReadOnlyList<EngineStatusInfo> Engines { get; init; } = Array.Empty<EngineStatusInfo>();

    public ResourceSnapshot Resources { get; init; } = ResourceSnapshot.Unavailable("Not sampled.");

    public IReadOnlyList<ValidationIssue> ValidationIssues { get; init; } = Array.Empty<ValidationIssue>();

    public int LoadedModelCount { get; init; }

    public int StandbyModelCount { get; init; }

    public long LogEntryCount { get; init; }

    public string? ConfigDirectory { get; init; }
}
