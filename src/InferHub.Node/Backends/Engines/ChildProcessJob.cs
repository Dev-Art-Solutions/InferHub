using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InferHub.Node.Backends;

/// <summary>
/// On Windows, ties every launched engine to this node's lifetime (phase 95, D3): one Job Object
/// with <c>KILL_ON_JOB_CLOSE</c>, held for the life of the process, so a node that is killed rather
/// than stopped takes its engines with it instead of leaving a <c>llama-server</c> holding the port
/// and gigabytes of RAM. Found by killing a live node in the phase's own check.
/// </summary>
/// <remarks>
/// A graceful stop already kills the tree. This is only for the stop that never ran. On Linux a
/// container's engines die with its PID namespace; a bare-metal Linux node killed with <c>-9</c>
/// still orphans them, and the release notes say so.
/// </remarks>
internal static class ChildProcessJob
{
    private static readonly Lazy<IntPtr> Job = new(Create);

    public static void Assign(Process process)
    {
        if (!OperatingSystem.IsWindows() || Job.Value == IntPtr.Zero)
        {
            return;
        }

        if (!AssignProcessToJobObject(Job.Value, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "could not tie the engine to the node's lifetime");
        }
    }

    private static IntPtr Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            return IntPtr.Zero;
        }

        var job = CreateJobObject(IntPtr.Zero, null);

        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose }
        };

        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(length);

        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            return SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, pointer, (uint)length)
                ? job
                : IntPtr.Zero;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private const int JobObjectExtendedLimitInformationClass = 9;

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
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
