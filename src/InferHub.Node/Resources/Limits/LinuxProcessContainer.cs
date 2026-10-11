using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Resources.Limits;

/// <summary>
/// Linux (phase 102): a cgroup v2 child per runtime when this process may make one — <c>cpu.max</c> and
/// <c>memory.max</c>, both live — and CPU affinity either way, re-applied to every thread of every
/// process of the runtime on each poll so a thread started since is caught.
/// </summary>
/// <remarks>
/// <para>
/// <b>A cgroup needs delegation, and most boxes do not give it.</b> A Docker container sees
/// <c>/sys/fs/cgroup</c> read-only unless it was run with a writable cgroup namespace; a systemd
/// service needs <c>Delegate=yes</c>. Without it this says so once and holds only affinity: memory
/// falls to the watchdog and the CPU share is reported as not held, never as held.
/// </para>
/// <para>
/// <b>cgroup v2's one rule decides the order.</b> A cgroup with processes in it cannot hand controllers
/// to children, so the node first moves itself into a leaf (<c>inferhub-node</c>) beside the runtime
/// groups, then turns on <c>cpu</c> and <c>memory</c> for the children. Any step failing leaves the
/// node where it was and the runtime on affinity and the watchdog.
/// </para>
/// </remarks>
internal sealed class LinuxProcessContainer : IProcessContainer
{
    private const string Root = "/sys/fs/cgroup";

    private static readonly object SetupGate = new();
    private static string? delegatedBase;
    private static bool setupTried;

    private readonly string group;
    private readonly ILogger logger;
    private readonly string? cgroup;
    private int? cores;

    public LinuxProcessContainer(string group, ILogger logger)
    {
        this.group = group;
        this.logger = logger;
        cgroup = TryCreateGroup(group, logger);
    }

    public void Add(Process process)
    {
        if (cgroup is not null)
        {
            try
            {
                File.WriteAllText(Path.Combine(cgroup, "cgroup.procs"), process.Id.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not move {Group} process {Pid} into its cgroup; it is held by affinity and the watchdog only.", group, process.Id);
            }
        }

        if (cores is { } count)
        {
            ProcessAffinity.SetForProcess(process.Id, count);
        }
    }

    public IReadOnlyDictionary<string, string> Apply(EngineLimits limits)
    {
        var held = new Dictionary<string, string>();
        cores = limits.Cores;

        if (limits.Cores is not null)
        {
            held[LimitResources.Cores] = "os (sched_setaffinity, re-applied every poll)";
        }

        if (cgroup is null)
        {
            return held;
        }

        // "max 100000" lifts it; a share is that much of every CPU of the box per 100 ms period.
        var period = 100_000L;
        var cpuMax = limits.CpuPercent is { } cpu
            ? $"{Math.Max(1000, period * Environment.ProcessorCount * cpu / 100)} {period}"
            : $"max {period}";

        if (TryWrite("cpu.max", cpuMax) && limits.CpuPercent is not null)
        {
            held[LimitResources.Cpu] = "os (cgroup cpu.max)";
        }

        var memoryMax = limits.MemoryMiB is { } memory
            ? ((long)memory * 1024 * 1024).ToString(CultureInfo.InvariantCulture)
            : "max";

        if (TryWrite("memory.max", memoryMax) && limits.MemoryMiB is not null)
        {
            held[LimitResources.Memory] = "os (cgroup memory.max)";
        }

        return held;
    }

    public IReadOnlyList<int>? ProcessIds()
    {
        if (cgroup is null)
        {
            return null;
        }

        try
        {
            return File.ReadAllLines(Path.Combine(cgroup, "cgroup.procs"))
                .Select(line => int.TryParse(line, out var pid) ? pid : 0)
                .Where(pid => pid > 0)
                .ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Re-applies affinity to every thread of <paramref name="pids"/>: the half a cgroup-less box still has.</summary>
    public void Sweep(IEnumerable<int> pids)
    {
        if (cores is not { } count)
        {
            return;
        }

        foreach (var pid in pids)
        {
            ProcessAffinity.SetForProcess(pid, count);
        }
    }

    public void Dispose()
    {
    }

    private bool TryWrite(string file, string value)
    {
        try
        {
            File.WriteAllText(Path.Combine(cgroup!, file), value);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write {File} for the {Group} cgroup.", file, group);
            return false;
        }
    }

    private static string? TryCreateGroup(string group, ILogger logger)
    {
        var parent = DelegatedBase(logger);

        if (parent is null)
        {
            return null;
        }

        try
        {
            var path = Path.Combine(parent, $"inferhub-{group}");
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create the cgroup for the {Group} limits; they are held by affinity and the watchdog only.", group);
            return null;
        }
    }

    private static string? DelegatedBase(ILogger logger)
    {
        lock (SetupGate)
        {
            if (setupTried)
            {
                return delegatedBase;
            }

            setupTried = true;

            try
            {
                if (!File.Exists(Path.Combine(Root, "cgroup.controllers")))
                {
                    logger.LogInformation("No cgroup v2 hierarchy at {Root}: runtime CPU shares are not held, memory falls to the watchdog.", Root);
                    return null;
                }

                var own = File.ReadAllLines("/proc/self/cgroup")
                    .FirstOrDefault(line => line.StartsWith("0::", StringComparison.Ordinal))?[3..];

                if (own is null)
                {
                    return null;
                }

                var current = Path.Combine(Root, own.TrimStart('/'));
                var controllers = File.ReadAllText(Path.Combine(current, "cgroup.controllers"));

                if (!controllers.Contains("cpu", StringComparison.Ordinal) || !controllers.Contains("memory", StringComparison.Ordinal))
                {
                    logger.LogInformation("This node's cgroup offers '{Controllers}', not cpu and memory: runtime CPU shares are not held, memory falls to the watchdog.", controllers.Trim());
                    return null;
                }

                var leaf = Path.Combine(current, "inferhub-node");
                Directory.CreateDirectory(leaf);
                File.WriteAllText(Path.Combine(leaf, "cgroup.procs"), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(Path.Combine(current, "cgroup.subtree_control"), "+cpu +memory");

                delegatedBase = current;
                logger.LogInformation("Runtime limits use cgroup v2 under {Base}.", current);
                return current;
            }
            catch (Exception ex)
            {
                logger.LogInformation(
                    "This node may not make cgroups ({Reason}): runtime CPU shares are not held, memory falls to the watchdog. A container needs a writable cgroup namespace, a systemd service Delegate=yes.",
                    ex.Message);

                return null;
            }
        }
    }
}

/// <summary>CPU affinity on Linux, per thread — <c>sched_setaffinity</c> names a thread, not a process.</summary>
internal static class ProcessAffinity
{
    public static void SetForProcess(int pid, int cores)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var mask = new byte[128];
        var count = Math.Clamp(cores, 1, Math.Min(Environment.ProcessorCount, mask.Length * 8));

        for (var cpu = 0; cpu < count; cpu++)
        {
            mask[cpu / 8] |= (byte)(1 << (cpu % 8));
        }

        IEnumerable<string> threads;

        try
        {
            threads = Directory.EnumerateDirectories($"/proc/{pid}/task").ToArray();
        }
        catch (Exception)
        {
            return;
        }

        foreach (var thread in threads)
        {
            if (int.TryParse(Path.GetFileName(thread), out var tid))
            {
                try
                {
                    _ = sched_setaffinity(tid, (nint)mask.Length, mask);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setaffinity(int pid, nint cpusetsize, byte[] mask);
}
