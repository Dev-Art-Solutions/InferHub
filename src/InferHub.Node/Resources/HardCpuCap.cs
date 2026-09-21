using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InferHub.Node.Resources;

/// <summary>
/// A real OS-level CPU rate cap on this node's tool-worker child processes (phase-82 D6) — the
/// second, harder mechanism beside the soft placement-withdrawal <see cref="ResourceMonitor"/>
/// enforces. Windows only today, via a Job Object; process-tree CPU rate limiting has no equivalent
/// primitive on Linux this codebase can reach without a new dependency (cgroups v2 is a filesystem
/// this process may not have permission to write to, and getting that wrong silently is worse than
/// not offering the mechanism there yet).
/// </summary>
/// <remarks>
/// <para>
/// <b>One job object, shared by every tool worker this node ever spawns</b> — not one per worker.
/// The cap is a machine-wide percentage the operator wrote down once
/// (<c>Node:ResourceLimits:HardCpuCapPercent</c>); putting every child in the same job is what makes
/// that number mean "all of them together", the same way a single <c>Node:Vram:BudgetMiB</c> is one
/// number for every recipe on the card rather than one per recipe.
/// </para>
/// <para>
/// <b>Never a startup failure</b> — a hand-rolled Job Object call failing (an unsupported Windows
/// build, a sandboxed process with no permission to create one) is logged once and the node keeps
/// running with the soft cap alone, the same "diagnostic information on a path that must never be
/// the reason a node fails to start" posture <see cref="Backends.CudaDeviceProbe"/> takes.
/// </para>
/// </remarks>
internal static class HardCpuCap
{
    private static readonly object Gate = new();
    private static nint jobHandle;
    private static bool attempted;
    private static ILogger? logger;

    /// <summary>Idempotent — later calls after the first are a no-op. Never throws.</summary>
    public static void Configure(int? hardCapPercent, ILogger log)
    {
        logger = log;

        if (hardCapPercent is not { } percent)
        {
            return;
        }

        lock (Gate)
        {
            if (attempted)
            {
                return;
            }

            attempted = true;

            if (!OperatingSystem.IsWindows())
            {
                logger.LogWarning(
                    "Node:ResourceLimits:HardCpuCapPercent is set but this is not Windows; the hard CPU cap is a Windows Job Object today and has no effect here. The soft cap (MaxCpuPercent/MaxGpuPercent), if set, still applies.");
                return;
            }

            try
            {
                CreateAndConfigureJob(percent);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not set up the Windows Job Object for Node:ResourceLimits:HardCpuCapPercent; the hard CPU cap will not apply.");
                jobHandle = 0;
            }
        }
    }

    /// <summary>Assigns a freshly started child process to the capped job, if one is configured. Never throws.</summary>
    public static void TryAssign(Process process)
    {
        if (jobHandle == 0)
        {
            return;
        }

        try
        {
            if (!AssignProcessToJobObject(jobHandle, process.Handle))
            {
                logger?.LogDebug("Could not assign process {Pid} to the CPU-capped job object (error {Error}).", process.Id, Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not assign process {Pid} to the CPU-capped job object.", process.Id);
        }
    }

    private static void CreateAndConfigureJob(int percent)
    {
        var handle = CreateJobObjectW(0, null);

        if (handle == 0)
        {
            logger!.LogWarning("CreateJobObject failed (error {Error}); the hard CPU cap will not apply.", Marshal.GetLastWin32Error());
            return;
        }

        var info = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = JobObjectCpuRateControlEnable | JobObjectCpuRateControlHardCap,
            // Units of 1/100 of a percent — 100% of the machine is 10000.
            CpuRate = (uint)(Math.Clamp(percent, 1, 100) * 100)
        };

        if (!SetInformationJobObject(
                handle,
                JobObjectCpuRateControlInformation,
                ref info,
                (uint)Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>()))
        {
            logger!.LogWarning("SetInformationJobObject failed (error {Error}); the hard CPU cap will not apply.", Marshal.GetLastWin32Error());
            CloseHandle(handle);
            return;
        }

        jobHandle = handle;

        logger!.LogInformation(
            "Node:ResourceLimits:HardCpuCapPercent is on: this node's tool-worker child processes are capped at {Percent}% of this machine's total CPU via a Windows Job Object.",
            percent);
    }

    private const int JobObjectCpuRateControlInformation = 15;
    private const uint JobObjectCpuRateControlEnable = 0x1;
    private const uint JobObjectCpuRateControlHardCap = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        nint hJob, int jobObjectInfoClass, ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
