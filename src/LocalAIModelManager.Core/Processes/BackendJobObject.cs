using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LocalAIModelManager.Core.Processes;

/// <summary>
/// Owns a Windows Job Object configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>.
/// Every backend child process is assigned to it, so if the manager ever dies
/// (crash, forced logoff, task kill) the kernel tears the engines down too - this is
/// the hard guarantee against orphaned llama-server processes.
/// </summary>
public sealed class BackendJobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly object _gate = new();
    private IntPtr _handle;
    private bool _disposed;

    public BackendJobObject()
    {
        try
        {
            _handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero)
            {
                LastError = $"CreateJobObject failed (win32 error {Marshal.GetLastWin32Error()}).";
                IsAvailable = false;
                return;
            }

            var info = new JobObjectExtendedLimitInformationNative
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };

            var size = Marshal.SizeOf<JobObjectExtendedLimitInformationNative>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, pointer, fDeleteOld: false);
                if (!NativeMethods.SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, pointer, (uint)size))
                {
                    LastError = $"SetInformationJobObject failed (win32 error {Marshal.GetLastWin32Error()}).";
                    IsAvailable = false;
                    return;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }

            IsAvailable = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Non-Windows or a restricted host: process tracking still works, we
            // just lose the kernel-level orphan guarantee.
            IsAvailable = false;
            LastError = ex.Message;
        }
    }

    public bool IsAvailable { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Assigns a freshly started child to the job. Returns false when the job is unusable.</summary>
    public bool TryAssign(Process process)
    {
        if (!IsAvailable || _handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return NativeMethods.AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>Terminates every process still assigned to the job.</summary>
    public void TerminateAll()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.TerminateJobObject(_handle, 0);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_handle != IntPtr.Zero)
            {
                // Closing the last handle to a KILL_ON_JOB_CLOSE job also kills its
                // remaining processes, which is exactly what we want on exit.
                NativeMethods.TerminateJobObject(_handle, 0);
                NativeMethods.CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationNative
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
