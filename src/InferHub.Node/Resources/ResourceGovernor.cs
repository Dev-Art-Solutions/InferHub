namespace InferHub.Node.Resources;

/// <summary>The last reading this node took of itself. Null means "no opinion yet", never "zero".</summary>
public readonly record struct ResourceSnapshot(double? CpuPercent, double? GpuPercent);

/// <summary>
/// Whether this box currently believes it is over its own configured resource ceiling (phase 82).
/// Always registered — nothing downstream branches on the feature existing, the same shape as
/// <c>IBackendSupervisor</c>/<c>NoBackendSupervisor</c> and <c>IToolRuntime</c>/<c>NoToolRuntime</c>.
/// </summary>
public interface IResourceGovernor
{
    ResourceSnapshot Current { get; }

    bool IsThrottled { get; }

    /// <summary>Why, in one sentence naming the config key and the numbers. Null while not throttled.</summary>
    string? Reason { get; }

    /// <summary>Raised only on a throttled/not-throttled transition — a poll that changes nothing does
    /// not fire this, the same "transition, not every beat" rule phase-69 D6 uses for backend health.</summary>
    event Action? Changed;
}

/// <summary>The stand-in when neither <c>MaxCpuPercent</c> nor <c>MaxGpuPercent</c> is set.</summary>
internal sealed class NoResourceGovernor : IResourceGovernor
{
    public static readonly NoResourceGovernor Instance = new();

    private NoResourceGovernor()
    {
    }

    public ResourceSnapshot Current => default;

    public bool IsThrottled => false;

    public string? Reason => null;

    public event Action? Changed
    {
        add { }
        remove { }
    }
}

/// <summary>
/// The pure decision at the heart of phase 82: given one more poll, is this node over its own cap.
/// </summary>
/// <remarks>
/// <para>
/// Same shape as <c>NodeProfileClamp</c> and <c>VramBudget</c> (phase-43 D1, phase-48): everything it
/// needs arrives as an argument or was given at construction, so a test drives it with a scripted
/// sequence of readings rather than a real clock or a real probe.
/// </para>
/// <para>
/// <b>Tripping and recovering are allowed to need a different number of polls</b> (D2). The default
/// biases recovery slower (<c>RecoverPolls</c> 2) than tripping (<c>SustainedPolls</c> 3 is actually
/// the larger of the two by default, but the two are independent on purpose): a box that only just
/// stopped being over its cap is not necessarily somewhere new work belongs the very next tick.
/// </para>
/// </remarks>
internal sealed class ResourceGovernorLogic
{
    private int overStreak;
    private int underStreak;

    public bool IsThrottled { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>
    /// Feeds one poll's reading in against the <em>current</em> configuration — read fresh every
    /// call rather than fixed at construction, so <c>node.local.json</c> (D7) reaches this decision
    /// within one poll with no restart. Returns true iff <see cref="IsThrottled"/> just flipped.
    /// </summary>
    public bool Apply(
        ResourceSnapshot snapshot,
        int? maxCpuPercent,
        int? maxGpuPercent,
        int sustainedPolls,
        int recoverPolls)
    {
        sustainedPolls = Math.Max(1, sustainedPolls);
        recoverPolls = Math.Max(1, recoverPolls);

        var overCpu = maxCpuPercent is { } cpuCap && snapshot.CpuPercent is { } cpu && cpu > cpuCap;
        var overGpu = maxGpuPercent is { } gpuCap && snapshot.GpuPercent is { } gpu && gpu > gpuCap;

        if (overCpu || overGpu)
        {
            overStreak++;
            underStreak = 0;
        }
        else
        {
            underStreak++;
            overStreak = 0;
        }

        var was = IsThrottled;

        if (!IsThrottled && overStreak >= sustainedPolls)
        {
            IsThrottled = true;
            Reason = DescribeOver(overCpu, overGpu, snapshot, maxCpuPercent, maxGpuPercent);
        }
        else if (IsThrottled && underStreak >= recoverPolls)
        {
            IsThrottled = false;
            Reason = null;
        }

        return was != IsThrottled;
    }

    private static string DescribeOver(
        bool overCpu, bool overGpu, ResourceSnapshot snapshot, int? maxCpuPercent, int? maxGpuPercent)
    {
        var cpuClause = $"CPU {snapshot.CpuPercent:F0}% > Node:ResourceLimits:MaxCpuPercent ({maxCpuPercent}%)";
        var gpuClause = $"GPU {snapshot.GpuPercent:F0}% > Node:ResourceLimits:MaxGpuPercent ({maxGpuPercent}%)";

        return overCpu && overGpu ? $"{cpuClause} and {gpuClause}" : overCpu ? cpuClause : gpuClause;
    }
}
