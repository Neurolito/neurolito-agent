using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tharga.Neurolito.Agent.Features.LlamaServer;

/// <summary>
/// Ties llama-server to the agent on Windows: the processes in this job end when the agent's process ends, however it
/// ends. Without it, an agent that is killed leaves llama-server running and holding the GPU's memory until the
/// agent starts again and finds it (<see cref="LlamaServerHost"/> also does that, for other platforms).
/// </summary>
internal static class ChildProcessJob
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private static readonly Lazy<SafeFileHandle> Job = new(Create);

    /// <summary>Adds <paramref name="process"/> to the job. False when it could not be, or not on Windows.</summary>
    public static bool Add(Process process)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var job = Job.Value;
        return job is { IsInvalid: false } && AssignProcessToJobObject(job, process.Handle);
    }

    private static SafeFileHandle Create()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) return null;

        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = KillOnJobClose } };
        var size = Marshal.SizeOf<ExtendedLimit>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            if (!SetInformationJobObject(job, ExtendedLimitInformation, pointer, (uint)size))
            {
                job.Dispose();
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        //NOTE: Never closed: the handle is what keeps the children alive, and closing it with the process is the point.
        return job;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
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
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
