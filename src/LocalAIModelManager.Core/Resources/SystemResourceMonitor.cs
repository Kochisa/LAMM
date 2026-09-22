using System.Globalization;
using System.Runtime.InteropServices;
using LocalAIModelManager.Core.Logging;

namespace LocalAIModelManager.Core.Resources;

public interface IResourceMonitor : IAsyncDisposable
{
    /// <summary>True when at least the CPU counters are readable.</summary>
    bool IsAvailable { get; }

    /// <summary>True when a GPU with a usable driver query interface was found.</summary>
    bool GpuAvailable { get; }

    string? LastError { get; }

    ResourceSnapshot Current { get; }

    event Action<ResourceSnapshot>? Updated;

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync();

    Task<ResourceSnapshot> RefreshAsync(CancellationToken cancellationToken);

    long? GetProcessVramBytes(int pid);
}

/// <summary>
/// Samples VRAM through <c>nvidia-smi</c> and CPU through the Win32
/// <c>GetSystemTimes</c> counters. No third-party dependency is required and the
/// monitor degrades to "GPU unavailable" instead of failing when there is no
/// NVIDIA driver present.
/// </summary>
public sealed class SystemResourceMonitor : IResourceMonitor
{
    private readonly IAppLogger _logger;
    private readonly TimeSpan _pollInterval;
    private readonly int _gpuIndex;
    private readonly bool _trackPerProcess;
    private readonly string? _nvidiaSmiPath;
    private readonly object _gate = new();

    private CancellationTokenSource? _loopSource;
    private Task? _loopTask;
    private ResourceSnapshot _current = ResourceSnapshot.Unavailable("Not sampled yet.");
    private long _lastIdle;
    private long _lastKernel;
    private long _lastUser;
    private bool _cpuInitialized;

    public SystemResourceMonitor(
        IAppLogger logger,
        TimeSpan pollInterval,
        int gpuIndex = 0,
        bool trackPerProcessVram = true,
        string? nvidiaSmiPath = null)
    {
        _logger = logger;
        _pollInterval = pollInterval;
        _gpuIndex = gpuIndex;
        _trackPerProcess = trackPerProcessVram;
        _nvidiaSmiPath = nvidiaSmiPath ?? LocateNvidiaSmi();
        GpuAvailable = _nvidiaSmiPath is not null;
    }

    public bool IsAvailable => true;

    public bool GpuAvailable { get; }

    public string? LastError { get; private set; }

    public ResourceSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event Action<ResourceSnapshot>? Updated;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loopTask is not null)
        {
            return Task.CompletedTask;
        }

        _loopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => LoopAsync(_loopSource.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_loopSource is not null)
        {
            await _loopSource.CancelAsync().ConfigureAwait(false);
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _loopSource?.Dispose();
        _loopSource = null;
        _loopTask = null;
    }

    public async Task<ResourceSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        var gpus = new List<GpuSnapshot>();
        var processVram = new Dictionary<int, long>();
        string? gpuError = null;

        if (_nvidiaSmiPath is not null)
        {
            try
            {
                var query = await RunNvidiaSmiAsync(
                    "--query-gpu=index,name,memory.total,memory.used,memory.free,utilization.gpu,temperature.gpu,power.draw",
                    cancellationToken).ConfigureAwait(false);

                foreach (var line in SplitCsvLines(query))
                {
                    var gpu = ParseGpuLine(line);
                    if (gpu is not null)
                    {
                        gpus.Add(gpu);
                    }
                }

                if (_trackPerProcess)
                {
                    var apps = await RunNvidiaSmiAsync(
                        "--query-compute-apps=pid,used_gpu_memory",
                        cancellationToken).ConfigureAwait(false);

                    foreach (var line in SplitCsvLines(apps))
                    {
                        var parts = Split(line);
                        if (parts.Length >= 2 &&
                            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) &&
                            TryParseMiB(parts[1], out var mib))
                        {
                            processVram[pid] = mib * 1024L * 1024L;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                gpuError = ex.Message;
                LastError = ex.Message;
            }
        }
        else
        {
            gpuError = "nvidia-smi was not found; VRAM metrics are unavailable.";
        }

        var (totalMemory, availableMemory) = GetMemoryStatus();
        var snapshot = new ResourceSnapshot
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            GpuAvailable = gpus.Count > 0,
            GpuError = gpus.Count > 0 ? null : gpuError,
            Gpus = gpus,
            GpuIndex = _gpuIndex,
            SystemCpuPercent = SampleCpuPercent(),
            TotalMemoryBytes = totalMemory,
            AvailableMemoryBytes = availableMemory,
            ProcessVramBytes = processVram,
        };

        lock (_gate)
        {
            _current = snapshot;
        }

        Updated?.Invoke(snapshot);
        return snapshot;
    }

    public long? GetProcessVramBytes(int pid)
    {
        var snapshot = Current;
        return snapshot.ProcessVram(pid);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn("resources", "resource monitor loop stopped unexpectedly", ex);
        }
    }

    private async Task<string> RunNvidiaSmiAsync(string queryArguments, CancellationToken cancellationToken)
    {
        if (_nvidiaSmiPath is null)
        {
            return string.Empty;
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _nvidiaSmiPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                queryArguments,
                "--format=csv,noheader,nounits",
            },
        };

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return stdout;
    }

    private static GpuSnapshot? ParseGpuLine(string line)
    {
        var parts = Split(line);
        if (parts.Length < 6)
        {
            return null;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            return null;
        }

        return new GpuSnapshot
        {
            Index = index,
            Name = parts[1],
            TotalBytes = TryParseMiB(parts[2], out var total) ? total * 1024L * 1024L : 0,
            UsedBytes = TryParseMiB(parts[3], out var used) ? used * 1024L * 1024L : 0,
            FreeBytes = TryParseMiB(parts[4], out var free) ? free * 1024L * 1024L : 0,
            UtilizationPercent = TryParseDouble(parts[5], out var util) ? util : 0,
            TemperatureCelsius = parts.Length > 6 && TryParseDouble(parts[6], out var temp) ? temp : 0,
            PowerWatts = parts.Length > 7 && TryParseDouble(parts[7], out var power) ? power : null,
        };
    }

    private static IEnumerable<string> SplitCsvLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l));

    private static string[] Split(string line) =>
        line.Split(',', StringSplitOptions.TrimEntries);

    private static bool TryParseMiB(string value, out long mib)
    {
        mib = 0;
        if (string.IsNullOrWhiteSpace(value) || value.Contains("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out mib))
        {
            return true;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var asDouble))
        {
            mib = (long)asDouble;
            return true;
        }

        return false;
    }

    private static bool TryParseDouble(string value, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value) || value.Contains("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private double? SampleCpuPercent()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        if (!_cpuInitialized)
        {
            _cpuInitialized = true;
            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
            return null;
        }

        var idleDelta = idle - _lastIdle;
        var kernelDelta = kernel - _lastKernel;
        var userDelta = user - _lastUser;
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;

        var total = kernelDelta + userDelta;
        if (total <= 0)
        {
            return null;
        }

        var busy = total - idleDelta;
        return Math.Clamp((double)busy / total * 100.0, 0, 100);
    }

    private static (long? Total, long? Available) GetMemoryStatus()
    {
        try
        {
            var status = new NativeMethods.MemoryStatusEx();
            if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            {
                return (null, null);
            }

            return ((long)status.TotalPhys, (long)status.AvailPhys);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return (null, null);
        }
    }

    private static string? LocateNvidiaSmi()
    {
        var system = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (File.Exists(system))
        {
            return system;
        }

        var programFiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        if (File.Exists(programFiles))
        {
            return programFiles;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "nvidia-smi.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;

        public MemoryStatusEx()
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
