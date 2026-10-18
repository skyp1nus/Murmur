using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Murmur.Intelligence.Llama;

/// <summary>Minimal OS interop for supervising the llama-server child process.</summary>
internal static partial class NativeMethods
{
    internal const int SigTerm = 15;

    /// <summary>POSIX <c>kill(2)</c>; used to ask the child for a graceful shutdown.</summary>
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [UnsupportedOSPlatform("windows")]
    internal static partial int Kill(int pid, int signal);

    internal const uint JobObjectLimitKillOnJobClose = 0x00002000;
    internal const int JobObjectExtendedLimitInformationClass = 9;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [SupportedOSPlatform("windows")]
    internal static partial nint CreateJobObject(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(nint job, int infoClass, ref JobObjectExtendedLimitInformation info, uint infoLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    /// <summary>JOBOBJECT_BASIC_LIMIT_INFORMATION (64 bytes on x64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformation
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

    /// <summary>IO_COUNTERS (48 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    /// <summary>JOBOBJECT_EXTENDED_LIMIT_INFORMATION (144 bytes on x64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}

/// <summary>
/// A Windows Job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: when the last handle closes — including when
/// the app process dies — Windows kills every process in the job.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class KillOnCloseJob : IDisposable
{
    private nint _handle;

    private KillOnCloseJob(nint handle) => _handle = handle;

    /// <summary>Creates the job and assigns <paramref name="processHandle"/> to it; <c>null</c> if Windows refused.</summary>
    public static KillOnCloseJob? TryAssign(nint processHandle)
    {
        var job = NativeMethods.CreateJobObject(0, null);
        if (job == 0)
        {
            return null;
        }

        var info = new NativeMethods.JobObjectExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose;
        var size = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>();
        if (!NativeMethods.SetInformationJobObject(job, NativeMethods.JobObjectExtendedLimitInformationClass, ref info, size)
            || !NativeMethods.AssignProcessToJobObject(job, processHandle))
        {
            NativeMethods.CloseHandle(job);
            return null;
        }

        return new KillOnCloseJob(job);
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
