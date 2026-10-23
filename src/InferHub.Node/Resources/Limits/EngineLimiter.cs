using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Resources.Limits;

/// <summary>
/// Holds each runtime on this node to its hard limits (phase 102) — the node's own
/// <c>Node:ResourceLimits:Engines</c>, narrowed further by whatever the coordinator's profile asked for.
/// </summary>
public interface IEngineLimiter
{
    /// <summary>The node's own ceilings, read fresh — what a profile is clamped against.</summary>
    IReadOnlyDictionary<string, EngineLimits> NodeCeilings { get; }

    /// <summary>The profile's limits after the clamp; null when the profile says nothing about limits.</summary>
    void SetHubLimits(IReadOnlyDictionary<string, EngineLimits>? limits);

    /// <summary>What the limits are, how each is held and what each runtime uses — for the hub and the local page.</summary>
    NodeLimitsState State(string nodeId);

    /// <summary>Raised when a limit, how it is held, a breach or the process count changed — not on every reading.</summary>
    event Action? Changed;
}

/// <summary>
/// The one limiter (phase 102). Launch sites reach it through <see cref="PrepareLaunch"/> and
/// <see cref="Adopt"/>, statics like <see cref="Backends.ChildProcessJob"/> and <see cref="HardCpuCap"/>,
/// because what they feed — a Job Object, a cgroup — belongs to the process, not to a scope.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three ways a limit is held, and the report says which</b> (D3). <em>os</em>: the kernel refuses
/// more — a Job Object or cgroup for CPU share and memory, affinity for cores. <em>launch</em>: the GPU
/// list is the environment a runtime starts with, so a change restarts it. <em>watchdog</em>: GPU
/// memory, which no OS can cap, and memory on a Linux box with no cgroup — read every poll, and the
/// runtime is stopped above it; its own supervisor starts it again. A limit nothing can hold here is
/// reported as <c>none: why</c>, never as held.
/// </para>
/// <para>
/// <b>Only processes this node started</b>: an Ollama somebody else runs is somebody else's (36 D1).
/// </para>
/// </remarks>
public sealed class EngineLimiter : IEngineLimiter, IHostedService, IDisposable
{
    /// <summary>
    /// Every runtime reads its own: CUDA (Ollama, Strata, llama.cpp's CUDA build), ROCm, and llama.cpp's
    /// Vulkan build — which ignores the CUDA one, found on this project's own box (102).
    /// </summary>
    private static readonly string[] GpuVariables = ["CUDA_VISIBLE_DEVICES", "HIP_VISIBLE_DEVICES", "ROCR_VISIBLE_DEVICES", "GGML_VK_VISIBLE_DEVICES"];
    private const string Unrestricted = "*";

    private static volatile EngineLimiter? installed;

    private readonly IOptionsMonitor<NodeOptions> nodeOptions;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly IProcessContainerFactory containers;
    private readonly IProcessInspector inspector;
    private readonly Action<Process?, int> kill;
    private readonly ConcurrentDictionary<string, Group> groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<ProcessStartInfo, string> launchGpus = new();
    private readonly ConditionalWeakTable<ProcessStartInfo, Telling> launchTold = new();
    private static readonly ConcurrentDictionary<string, IEngineLimitTeller> Tellers = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyDictionary<string, EngineLimits> hub = new Dictionary<string, EngineLimits>();
    private CancellationTokenSource? lifetime;
    private readonly object pollGate = new();
    private string signature = string.Empty;

    public EngineLimiter(
        IOptionsMonitor<NodeOptions> nodeOptions,
        TimeProvider time,
        ILogger<EngineLimiter> logger,
        IProcessContainerFactory? containers = null,
        IProcessInspector? inspector = null,
        Action<Process?, int>? kill = null)
    {
        this.nodeOptions = nodeOptions;
        this.time = time;
        this.logger = logger;
        this.containers = containers ?? new PlatformProcessContainerFactory(logger);
        this.inspector = inspector ?? new SystemProcessInspector();
        this.kill = kill ?? KillTree;
    }

    public event Action? Changed;

    /// <summary>Called by a launch site before <c>Process.Start</c>: the GPU list goes into the environment. Never throws.</summary>
    public static void PrepareLaunch(string group, ProcessStartInfo info) => installed?.Prepare(group, info);

    /// <summary>Called by a launch site right after <c>Process.Start</c>: the process goes under its runtime's limits. Never throws.</summary>
    public static void Adopt(string group, Process process) => installed?.Attach(group, process);

    /// <summary>
    /// Phase 111: <paramref name="teller"/> tells <paramref name="group"/>'s launches their limits in the
    /// runtime's own words. The composition root registers one per runtime that has one; a group with
    /// none is held from outside only, exactly as in 102.
    /// </summary>
    public static void RegisterTeller(string group, IEngineLimitTeller teller) =>
        Tellers[EngineLimitGroups.Normalize(group) ?? group] = teller;

    /// <summary>Whether a core limit on <paramref name="group"/> may be its job's affinity (111, <see cref="IEngineLimitTeller.CoresOnTheJob"/>).</summary>
    internal static bool CoresOnTheJob(string group) =>
        !Tellers.TryGetValue(EngineLimitGroups.Normalize(group) ?? group, out var teller) || teller.CoresOnTheJob;

    /// <summary>The limits in force for <paramref name="group"/> now, for a launch site that must decide something before it starts (none when no limiter runs).</summary>
    public static EngineLimits LimitsFor(string group) => installed?.Effective(group) ?? EngineLimits.None;

    public IReadOnlyDictionary<string, EngineLimits> NodeCeilings =>
        nodeOptions.CurrentValue.ResourceLimits.Engines
            .Where(pair => EngineLimitGroups.Normalize(pair.Key) is not null && pair.Value is not null)
            .GroupBy(pair => EngineLimitGroups.Normalize(pair.Key)!)
            .ToDictionary(g => g.Key, g => g.Last().Value.ToContract(), StringComparer.OrdinalIgnoreCase);

    public void SetHubLimits(IReadOnlyDictionary<string, EngineLimits>? limits)
    {
        hub = limits is null
            ? new Dictionary<string, EngineLimits>()
            : new Dictionary<string, EngineLimits>(limits, StringComparer.OrdinalIgnoreCase);

        Poll();
    }

    /// <summary>The limits in force for <paramref name="group"/>: per field the smaller of the node's and the hub's.</summary>
    public EngineLimits Effective(string group) =>
        EngineLimitMath.Effective(NodeCeilings.GetValueOrDefault(group), hub.GetValueOrDefault(group));

    public void Prepare(string group, ProcessStartInfo info)
    {
        Tell(group, info);

        try
        {
            var gpus = Effective(group).Gpus;

            if (gpus is null)
            {
                launchGpus.AddOrUpdate(info, Unrestricted);
                return;
            }

            // "-1" rather than empty: CUDA reads both as no device, and Ollama documents "-1" for CPU only.
            var value = gpus.Count == 0 ? "-1" : string.Join(',', gpus);
            foreach (var variable in GpuVariables)
            {
                info.Environment[variable] = value;
            }
            launchGpus.AddOrUpdate(info, value);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not set the {Group} GPU list on its launch.", group);
        }
    }

    /// <summary>111 D2: the runtime's teller changes the launch; what it said is kept with the start info, for <see cref="Attach"/>.</summary>
    private void Tell(string group, ProcessStartInfo info)
    {
        var name = EngineLimitGroups.Normalize(group) ?? group;

        if (!Tellers.TryGetValue(name, out var teller))
        {
            return;
        }

        try
        {
            var told = teller.Told(Effective(name));
            var lines = told.IsEmpty ? [] : teller.Tell(told, info);
            launchTold.AddOrUpdate(info, new Telling(Describe(told), lines));
        }
        catch (Exception ex)
        {
            // Holding does not depend on telling (111 D1): the runtime starts, held from outside as in 102.
            logger.LogWarning(ex, "Could not tell {Group} its limits on its launch; it is held to them from outside only.", name);
        }
    }

    public void Attach(string group, Process process)
    {
        try
        {
            string? gpus = null;
            Telling? told = null;

            try
            {
                gpus = launchGpus.TryGetValue(process.StartInfo, out var value) ? value : null;
                told = launchTold.TryGetValue(process.StartInfo, out var telling) ? telling : null;
            }
            catch (InvalidOperationException)
            {
            }

            Track(group, process.Id, process, gpus ?? Unrestricted, told);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not put a {Group} process under its limits.", group);
        }
    }

    /// <summary>The seam the tests drive: a process by pid, with the GPU list it was started with.</summary>
    internal void Track(string group, int pid, Process? process, string launchedWith, Telling? told = null)
    {
        var name = EngineLimitGroups.Normalize(group) ?? group;
        var entry = groups.GetOrAdd(name, key => new Group(key));

        lock (entry)
        {
            entry.Roots.RemoveAll(root => root.Pid == pid);
            entry.Roots.Add(new Root(pid, process, launchedWith, told));

            if (entry.Container is null && !Effective(name).IsEmpty)
            {
                entry.Container = containers.Create(name);
                entry.Applied = null;
            }

            if (process is not null)
            {
                entry.Container?.Add(process);
            }
        }

        Poll();
    }

    public NodeLimitsState State(string nodeId)
    {
        var ceilings = NodeCeilings;
        var requested = hub;
        var names = ceilings.Keys
            .Concat(requested.Keys)
            .Concat(groups.Keys)
            .Select(name => EngineLimitGroups.Normalize(name) ?? name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => EngineLimitGroups.All.ToList().IndexOf(name))
            .ToArray();

        var rows = new List<EngineLimitsGroupState>();

        foreach (var name in names)
        {
            var effective = EngineLimitMath.Effective(ceilings.GetValueOrDefault(name), requested.GetValueOrDefault(name));
            groups.TryGetValue(name, out var entry);

            if (entry is null && effective.IsEmpty)
            {
                continue;
            }

            lock (entry ?? new object())
            {
                rows.Add(new EngineLimitsGroupState(
                    name,
                    ceilings.GetValueOrDefault(name),
                    requested.GetValueOrDefault(name),
                    effective,
                    entry?.Enforcement ?? Enforcement(effective, null),
                    entry?.Roots.Count(root => root.Alive) ?? 0,
                    entry?.CpuPercent,
                    entry?.MemoryBytes is { } memory ? memory / (1024 * 1024) : null,
                    entry?.VramBytes is { } vram ? vram / (1024 * 1024) : null,
                    entry?.Breaches ?? 0,
                    entry?.LastBreach,
                    Told(entry)));
            }
        }

        return new NodeLimitsState(nodeId, rows, time.GetUtcNow());
    }

    /// <summary>Makes <paramref name="limiter"/> the one the launch sites reach. The composition root calls it; tests do not.</summary>
    public static void Install(EngineLimiter limiter) => installed = limiter;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime = new CancellationTokenSource();

        var ceilings = NodeCeilings;

        if (ceilings.Count > 0)
        {
            logger.LogInformation(
                "Runtime limits are on: {Limits}. A coordinator may lower them and cannot raise them.",
                string.Join("; ", ceilings.Select(pair => $"{pair.Key} {Describe(pair.Value)}")));
        }

        _ = Task.Run(() => RunAsync(lifetime.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lifetime?.Cancel();

        if (installed == this)
        {
            installed = null;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lifetime?.Dispose();

        foreach (var entry in groups.Values)
        {
            entry.Container?.Dispose();
        }

        (inspector as IDisposable)?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(nodeOptions.CurrentValue.ResourceLimits.PollInterval, time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Runtime limits poll failed; trying again next poll.");
            }
        }
    }

    /// <summary>One pass: apply what changed, read what each runtime uses, stop what went over. Internal so tests step it.</summary>
    internal void Poll()
    {
        lock (pollGate)
        {
            PollOnce();
        }
    }

    private void PollOnce()
    {
        var now = time.GetUtcNow();
        IReadOnlyDictionary<int, long>? gpuMemory = null;
        var gpuRead = false;

        foreach (var name in NodeCeilings.Keys.Concat(hub.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            groups.GetOrAdd(EngineLimitGroups.Normalize(name) ?? name, key => new Group(key));
        }

        foreach (var entry in groups.Values)
        {
            var effective = Effective(entry.Name);
            var toKill = new List<(Root Root, string Why)>();

            lock (entry)
            {
                entry.Roots.RemoveAll(root => !root.Alive && inspector.Sample(root.Pid) is null);

                // Only for a runtime with a process: a limit on something not running needs no OS
                // object, and on Linux making one moves this node into a cgroup.
                if (entry.Container is null && !effective.IsEmpty && entry.Roots.Count > 0)
                {
                    entry.Container = containers.Create(entry.Name);
                    entry.Applied = null;

                    // Started before any limit existed: they go in now, children with them.
                    foreach (var pid in inspector.WithDescendants(entry.Roots.Select(root => root.Pid).ToArray()))
                    {
                        if (TryOpen(pid) is { } process)
                        {
                            using (process)
                            {
                                entry.Container.Add(process);
                            }
                        }
                    }
                }

                if (entry.Container is not null && (entry.Applied is null || Describe(entry.Applied) != Describe(effective)))
                {
                    var held = entry.Container.Apply(effective);
                    entry.Applied = effective;
                    entry.Enforcement = Enforcement(effective, held);
                    entry.Held = held;
                }
                else if (entry.Container is null)
                {
                    entry.Enforcement = effective.IsEmpty
                        ? new Dictionary<string, string>()
                        : Enforcement(effective, null);
                    entry.Held = new Dictionary<string, string>();
                }

                if (effective.IsEmpty)
                {
                    entry.CpuPercent = null;
                    entry.MemoryBytes = null;
                    entry.VramBytes = null;
                    ToldChanged(entry, effective, toKill);
                    goto Stop;
                }

                var pids = entry.Container?.ProcessIds() is { Count: > 0 } listed
                    ? listed.Union(inspector.WithDescendants(entry.Roots.Select(root => root.Pid).ToArray())).ToArray()
                    : inspector.WithDescendants(entry.Roots.Select(root => root.Pid).ToArray());

                (entry.Container as LinuxProcessContainer)?.Sweep(pids);
                (entry.Container as WindowsJobContainer)?.Sweep(pids);

                Measure(entry, pids, now);

                if (effective.VramMiB is not null)
                {
                    if (!gpuRead)
                    {
                        gpuMemory = inspector.GpuMemory();
                        gpuRead = true;
                    }

                    entry.VramBytes = gpuMemory is null ? null : pids.Sum(pid => gpuMemory.GetValueOrDefault(pid));
                }
                else
                {
                    entry.VramBytes = null;
                }

                if (effective.MemoryMiB is { } memoryLimit
                    && !entry.Held.ContainsKey(LimitResources.Memory)
                    && entry.MemoryBytes is { } memory
                    && memory > (long)memoryLimit * 1024 * 1024)
                {
                    var why = $"{entry.Name} used {memory / (1024 * 1024)} MiB of memory, over its limit of {memoryLimit} MiB";
                    toKill.AddRange(entry.Roots.Where(root => root.Alive).Select(root => (root, why)));
                }
                else if (effective.VramMiB is { } vramLimit
                    && entry.VramBytes is { } vram
                    && vram > (long)vramLimit * 1024 * 1024)
                {
                    var why = $"{entry.Name} used {vram / (1024 * 1024)} MiB of GPU memory, over its limit of {vramLimit} MiB";
                    toKill.AddRange(entry.Roots.Where(root => root.Alive).Select(root => (root, why)));
                }

                if (toKill.Count > 0)
                {
                    entry.Breaches++;
                    entry.LastBreach = $"{toKill[0].Why} — stopped at {now:yyyy-MM-dd HH:mm:ss}Z";
                }
                else
                {
                    // A runtime started with another GPU list than the one in force sees cards it may
                    // not: it is stopped, and its supervisor starts it with the new list.
                    var wanted = effective.Gpus is null ? Unrestricted : effective.Gpus.Count == 0 ? "-1" : string.Join(',', effective.Gpus);

                    foreach (var root in entry.Roots.Where(root => root.Alive && root.LaunchedWith != wanted))
                    {
                        toKill.Add((root, $"{entry.Name} was started seeing GPUs '{root.LaunchedWith}' and may now see '{wanted}'"));
                    }

                    if (toKill.Count == 0)
                    {
                        ToldChanged(entry, effective, toKill);
                    }
                }

                Stop:
                foreach (var (root, _) in toKill)
                {
                    root.Stopped = true;
                }
            }

            foreach (var (root, why) in toKill)
            {
                logger.LogWarning("Stopping {Group} process {Pid}: {Why}. Its supervisor starts it again under the limits.", entry.Name, root.Pid, why);

                try
                {
                    kill(root.Process, root.Pid);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not stop {Group} process {Pid}.", entry.Name, root.Pid);
                }
            }
        }

        RaiseIfChanged();
    }

    /// <summary>
    /// 111 D3: a runtime told other limits than the ones in force planned for the wrong numbers — it is
    /// stopped and started again, as 102 D3 does for a GPU list. Only one that was told something: a
    /// process adopted without a teller (or by a node before this phase) is held as before and left alone.
    /// </summary>
    private static void ToldChanged(Group entry, EngineLimits effective, List<(Root Root, string Why)> toKill)
    {
        if (!Tellers.TryGetValue(entry.Name, out var teller))
        {
            return;
        }

        string wanted;

        try
        {
            wanted = Describe(teller.Told(effective));
        }
        catch (Exception)
        {
            return;
        }

        foreach (var root in entry.Roots.Where(root => root.Alive && root.Told is { } told && told.Limits != wanted))
        {
            toKill.Add((root, $"{entry.Name} was told '{root.Told!.Limits}' when it started, and its limits now say '{wanted}'"));
        }
    }

    /// <summary>What the newest live process of the runtime was told when it started; null when nothing was.</summary>
    private static IReadOnlyList<string>? Told(Group? entry) =>
        entry?.Roots.LastOrDefault(root => root.Alive && root.Told is not null)?.Told!.Lines is { Count: > 0 } lines ? lines : null;

    private void Measure(Group entry, IReadOnlyList<int> pids, DateTimeOffset now)
    {
        long memory = 0;
        var cpu = TimeSpan.Zero;
        var seen = new Dictionary<int, TimeSpan>();

        foreach (var pid in pids)
        {
            if (inspector.Sample(pid) is not { } sample)
            {
                continue;
            }

            memory += sample.MemoryBytes;
            seen[pid] = sample.CpuTime;

            // A process new since the last poll counts from now, not from its birth.
            if (entry.LastCpu.TryGetValue(pid, out var before) && sample.CpuTime >= before)
            {
                cpu += sample.CpuTime - before;
            }
        }

        var elapsed = entry.LastSampleAt is { } last ? now - last : TimeSpan.Zero;

        entry.CpuPercent = elapsed > TimeSpan.Zero
            ? Math.Round(Math.Min(100, cpu.TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100), 1)
            : null;

        entry.MemoryBytes = memory;
        entry.LastCpu = seen;
        entry.LastSampleAt = now;
    }

    /// <summary>How each limit is held; <paramref name="held"/> null is "no process yet", which holds nothing so far.</summary>
    private IReadOnlyDictionary<string, string> Enforcement(EngineLimits effective, IReadOnlyDictionary<string, string>? held)
    {
        var result = new Dictionary<string, string>();

        if (held is null)
        {
            foreach (var resource in new[] { (LimitResources.Cpu, effective.CpuPercent is not null), (LimitResources.Cores, effective.Cores is not null), (LimitResources.Memory, effective.MemoryMiB is not null), (LimitResources.Vram, effective.VramMiB is not null), (LimitResources.Gpus, effective.Gpus is not null) })
            {
                if (resource.Item2)
                {
                    result[resource.Item1] = "applies when the runtime starts";
                }
            }

            return result;
        }

        if (effective.CpuPercent is not null)
        {
            result[LimitResources.Cpu] = held.GetValueOrDefault(LimitResources.Cpu)
                ?? (OperatingSystem.IsLinux()
                    ? "none: this node may not make a cgroup (a container needs a writable cgroup namespace, a systemd service Delegate=yes)"
                    : "none: no CPU-share control on this box");
        }

        if (effective.Cores is not null)
        {
            result[LimitResources.Cores] = held.GetValueOrDefault(LimitResources.Cores) ?? "none: no affinity control on this box";
        }

        if (effective.MemoryMiB is not null)
        {
            result[LimitResources.Memory] = held.GetValueOrDefault(LimitResources.Memory) ?? "watchdog (stops the runtime above it)";
        }

        if (effective.VramMiB is not null)
        {
            result[LimitResources.Vram] = inspector.CanReadGpuMemory
                ? "watchdog (stops the runtime above it)"
                : "none: this box gives no per-process GPU memory reading";
        }

        if (effective.Gpus is not null)
        {
            result[LimitResources.Gpus] = "launch (CUDA_, HIP_, ROCR_ and GGML_VK_VISIBLE_DEVICES; a change restarts the runtime)";
        }

        return result;
    }

    private void RaiseIfChanged()
    {
        var now = string.Join(
            "|",
            groups.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).Select(g =>
            {
                lock (g)
                {
                    return $"{g.Name}:{Describe(Effective(g.Name))}:{string.Join(',', g.Enforcement.Select(e => e.Key + '=' + e.Value))}:{g.Roots.Count(r => r.Alive)}:{g.Breaches}";
                }
            }));

        if (now == signature)
        {
            return;
        }

        signature = now;
        Changed?.Invoke();
    }

    internal static string Describe(EngineLimits limits)
    {
        var parts = new List<string>();

        if (limits.CpuPercent is { } cpu)
        {
            parts.Add($"cpu {cpu}%");
        }

        if (limits.Cores is { } cores)
        {
            parts.Add($"{cores} core(s)");
        }

        if (limits.MemoryMiB is { } memory)
        {
            parts.Add($"memory {memory} MiB");
        }

        if (limits.VramMiB is { } vram)
        {
            parts.Add($"vram {vram} MiB");
        }

        if (limits.Gpus is { } gpus)
        {
            parts.Add(gpus.Count == 0 ? "no GPU" : $"gpus {string.Join(',', gpus)}");
        }

        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private static Process? TryOpen(int pid)
    {
        try
        {
            return Process.GetProcessById(pid);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void KillTree(Process? process, int pid)
    {
        var target = process ?? TryOpen(pid);

        if (target is null)
        {
            return;
        }

        try
        {
            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            if (process is null)
            {
                target.Dispose();
            }
        }
    }

    private sealed class Group(string name)
    {
        public string Name { get; } = name;

        public List<Root> Roots { get; } = [];

        public IProcessContainer? Container { get; set; }

        public EngineLimits? Applied { get; set; }

        public IReadOnlyDictionary<string, string> Held { get; set; } = new Dictionary<string, string>();

        public IReadOnlyDictionary<string, string> Enforcement { get; set; } = new Dictionary<string, string>();

        public double? CpuPercent { get; set; }

        public long? MemoryBytes { get; set; }

        public long? VramBytes { get; set; }

        public Dictionary<int, TimeSpan> LastCpu { get; set; } = new();

        public DateTimeOffset? LastSampleAt { get; set; }

        public int Breaches { get; set; }

        public string? LastBreach { get; set; }
    }

    /// <summary>What a launch was told (111): the limits, as <see cref="Describe"/> writes them, and the lines it was given.</summary>
    internal sealed record Telling(string Limits, IReadOnlyList<string> Lines);

    private sealed class Root(int pid, Process? process, string launchedWith, Telling? told)
    {
        public int Pid { get; } = pid;

        public Process? Process { get; } = process;

        public string LaunchedWith { get; } = launchedWith;

        public Telling? Told { get; } = told;

        /// <summary>Stopped by the limiter: no longer counted, whatever the OS says for the next second.</summary>
        public bool Stopped { get; set; }

        public bool Alive
        {
            get
            {
                if (Stopped)
                {
                    return false;
                }

                if (Process is null)
                {
                    return true;
                }

                try
                {
                    return !Process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }
    }
}
