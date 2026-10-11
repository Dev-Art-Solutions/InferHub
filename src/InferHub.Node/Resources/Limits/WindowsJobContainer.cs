using System.Diagnostics;
using System.Runtime.InteropServices;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Resources.Limits;

/// <summary>
/// One Windows Job Object per runtime (phase 102): CPU rate hard cap, affinity and a job-wide memory
/// limit, all three changeable live on processes already inside — unlike phase 82's
/// <see cref="HardCpuCap"/>, which set its rate once, this one is re-set every time the limits move.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nested, not instead.</b> An engine is first put in <see cref="Backends.ChildProcessJob"/> (95 D3,
/// kill on close) and then in this job; Windows 8 and later nest the second inside the first, so the
/// node's death still takes the engine with it and these limits still hold. Children a process starts
/// after joining are in the job by themselves — Ollama's runners, colibri's engine under its Python
/// launcher.
/// </para>
/// <para>
/// <b>The memory limit is the OS's</b>: an allocation past it fails inside the engine, which usually
/// ends it. That is what "cannot use more" means, and it is why the watchdog does not also watch memory
/// here.
/// </para>
/// </remarks>
internal sealed class WindowsJobContainer : IProcessContainer
{
    private readonly string group;
    private readonly ILogger logger;
    private nint job;

    private WindowsJobContainer(string group, nint job, ILogger logger)
    {
        this.group = group;
        this.job = job;
        this.logger = logger;
    }

    public static WindowsJobContainer? TryCreate(string group, ILogger logger)
    {
        try
        {
            var handle = CreateJobObjectW(0, null);

            if (handle != 0)
            {
                return new WindowsJobContainer(group, handle, logger);
            }

            logger.LogWarning("Could not create a Job Object for the {Group} limits (error {Error}); its limits fall to the watchdog.", group, Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create a Job Object for the {Group} limits; its limits fall to the watchdog.", group);
        }

        return null;
    }

    public void Add(Process process)
    {
        try
        {
            if (!AssignProcessToJobObject(job, process.Handle))
            {
                logger.LogWarning(
                    "Could not put {Group} process {Pid} under its limits (error {Error}); it runs unlimited until it is next started.",
                    group, process.Id, Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not put {Group} process {Pid} under its limits.", group, SafeId(process));
        }
    }

    public IReadOnlyDictionary<string, string> Apply(EngineLimits limits)
    {
        var held = new Dictionary<string, string>();

        var extended = new JobObjectExtendedLimitInformation();

        if (limits.MemoryMiB is { } memory)
        {
            extended.BasicLimitInformation.LimitFlags |= JobObjectLimitJobMemory;
            extended.JobMemoryLimit = (UIntPtr)((ulong)memory * 1024 * 1024);
        }

        if (limits.Cores is { } cores)
        {
            extended.BasicLimitInformation.LimitFlags |= JobObjectLimitAffinity;
            extended.BasicLimitInformation.Affinity = (UIntPtr)AffinityMask(cores);
        }

        if (SetExtended(extended))
        {
            if (limits.MemoryMiB is not null)
            {
                held[LimitResources.Memory] = "os (Job Object memory limit)";
            }

            if (limits.Cores is not null)
            {
                held[LimitResources.Cores] = "os (Job Object affinity)";
            }
        }

        var rate = new JobObjectCpuRateControlInformation();

        if (limits.CpuPercent is { } cpu)
        {
            rate.ControlFlags = JobObjectCpuRateControlEnable | JobObjectCpuRateControlHardCap;
            // Units of 1/100 of a percent of the whole machine.
            rate.CpuRate = (uint)(Math.Clamp(cpu, 1, 100) * 100);
        }

        if (SetRate(rate) && limits.CpuPercent is not null)
        {
            held[LimitResources.Cpu] = "os (Job Object CPU hard cap)";
        }

        return held;
    }

    public IReadOnlyList<int>? ProcessIds()
    {
        const int capacity = 1024;
        var size = 8 + (IntPtr.Size * capacity);
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            if (!QueryInformationJobObject(job, JobObjectBasicProcessIdListClass, buffer, (uint)size, out _))
            {
                return null;
            }

            var count = Math.Min(Marshal.ReadInt32(buffer, 4), capacity);
            var ids = new int[count];

            for (var i = 0; i < count; i++)
            {
                ids[i] = (int)Marshal.ReadIntPtr(buffer, 8 + (i * IntPtr.Size));
            }

            return ids;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref job, 0);

        if (handle != 0)
        {
            // No KILL_ON_JOB_CLOSE here: whether the node's death ends an engine is 95 D3's decision,
            // made by the outer job, not this one.
            CloseHandle(handle);
        }
    }

    /// <summary>The first <paramref name="cores"/> logical processors of this machine (one processor group, 64 at most).</summary>
    internal static ulong AffinityMask(int cores)
    {
        var count = Math.Clamp(cores, 1, Math.Min(Environment.ProcessorCount, 64));
        return count >= 64 ? ulong.MaxValue : (1UL << count) - 1;
    }

    private bool SetExtended(JobObjectExtendedLimitInformation info)
    {
        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(length);

        try
        {
            Marshal.StructureToPtr(info, pointer, false);

            if (SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, pointer, (uint)length))
            {
                return true;
            }

            logger.LogWarning("Could not set the {Group} memory/affinity limits on its Job Object (error {Error}).", group, Marshal.GetLastWin32Error());
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private bool SetRate(JobObjectCpuRateControlInformation info)
    {
        var length = Marshal.SizeOf<JobObjectCpuRateControlInformation>();
        var pointer = Marshal.AllocHGlobal(length);

        try
        {
            Marshal.StructureToPtr(info, pointer, false);

            if (SetInformationJobObject(job, JobObjectCpuRateControlInformationClass, pointer, (uint)length))
            {
                return true;
            }

            logger.LogWarning("Could not set the {Group} CPU limit on its Job Object (error {Error}).", group, Marshal.GetLastWin32Error());
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static int SafeId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private const int JobObjectBasicProcessIdListClass = 3;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const int JobObjectCpuRateControlInformationClass = 15;
    private const uint JobObjectLimitAffinity = 0x10;
    private const uint JobObjectLimitJobMemory = 0x200;
    private const uint JobObjectCpuRateControlEnable = 0x1;
    private const uint JobObjectCpuRateControlHardCap = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectCpuRateControlInformation
    {
        public uint ControlFlags;
        public uint CpuRate;
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
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint hJob, int infoClass, nint info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(nint hJob, int infoClass, nint info, uint length, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
