using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NLink.Infra.Nkn;

internal sealed class WindowsKillOnCloseProcessJob : IDisposable
{
    private SafeJobHandle? handle;

    private WindowsKillOnCloseProcessJob(SafeJobHandle handle)
    {
        this.handle = handle;
    }

    public static WindowsKillOnCloseProcessJob? TryAttach(Process process, Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        SafeJobHandle? job = null;
        IntPtr infoPtr = IntPtr.Zero;
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                log?.Invoke($"Bridge lifetime guard unavailable (create_job_failed error={error})");
                job.Dispose();
                return null;
            }

            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };

            var infoSize = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            infoPtr = Marshal.AllocHGlobal(infoSize);
            Marshal.StructureToPtr(limits, infoPtr, fDeleteOld: false);

            if (!SetInformationJobObject(job, JobObjectInfoType.ExtendedLimitInformation, infoPtr, (uint)infoSize))
            {
                var error = Marshal.GetLastWin32Error();
                log?.Invoke($"Bridge lifetime guard unavailable (set_job_limits_failed error={error})");
                job.Dispose();
                return null;
            }

            if (!AssignProcessToJobObject(job, process.Handle))
            {
                var error = Marshal.GetLastWin32Error();
                log?.Invoke($"Bridge lifetime guard unavailable (assign_process_failed error={error})");
                job.Dispose();
                return null;
            }

            return new WindowsKillOnCloseProcessJob(job);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            log?.Invoke($"Bridge lifetime guard unavailable ({ex.GetType().Name})");
            try
            {
                job?.Dispose();
            }
            catch
            {
                // Best-effort cleanup only.
            }

            return null;
        }
        finally
        {
            if (infoPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(infoPtr);
            }
        }
    }

    public void Dispose()
    {
        var toDispose = Interlocked.Exchange(ref handle, null);
        if (toDispose is null)
        {
            return;
        }

        toDispose.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeJobHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle hJob,
        JobObjectInfoType infoType,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr processHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9,
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            return CloseHandle(handle);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
