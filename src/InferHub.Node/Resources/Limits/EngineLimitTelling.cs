using System.Diagnostics;
using System.Globalization;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Resources.Limits;

/// <summary>
/// Tells a runtime its limits in its own words when it starts (phase 111) — so it plans inside them
/// instead of being stopped at them. Phase 102 holds a limit from outside (a Job Object, a cgroup,
/// affinity, the VRAM watchdog); a runtime that sizes itself from the free memory it sees — colibri
/// takes 88% of the RAM, Strata fills the card down to its reserve — does not see any of that, and
/// meets the limit as an allocation that fails or a watchdog that stops it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Telling never replaces holding</b> (111 D1). The OS object and the watchdog stay exactly as 102
/// made them; what a runtime is told is the plan it starts with, and the report says both.
/// </para>
/// <para>
/// <b>An engine-specific translation lives with its engine</b> (rule 1): colibri's teller is in
/// <c>Backends/Colibri/</c>, Strata's in <c>Backends/Strata/</c>. The limiter only knows that a group
/// may have one.
/// </para>
/// </remarks>
public interface IEngineLimitTeller
{
    /// <summary>
    /// The part of <paramref name="effective"/> this runtime is told about — pure, so the limiter can
    /// ask it every poll and restart a runtime that was told something else (111 D3).
    /// </summary>
    EngineLimits Told(EngineLimits effective);

    /// <summary>Changes the launch so the runtime knows <paramref name="told"/>; returns what it was told, one line each.</summary>
    IReadOnlyList<string> Tell(EngineLimits told, ProcessStartInfo info);
}

/// <summary>The arithmetic every teller shares: a thread count out of a core count and a CPU share.</summary>
public static class EngineLimitTelling
{
    /// <summary>
    /// How many threads a runtime should run under these limits: the cores it may use, or its share
    /// of the machine's logical processors, whichever is smaller; null when neither is limited.
    /// </summary>
    public static int? Threads(EngineLimits limits, int logicalProcessors)
    {
        int? threads = limits.Cores;

        if (limits.CpuPercent is { } percent)
        {
            var share = (int)Math.Ceiling(logicalProcessors * Math.Clamp(percent, 1, 100) / 100.0);
            threads = threads is { } cores ? Math.Min(cores, share) : share;
        }

        return threads is { } value ? Math.Max(1, value) : null;
    }

    /// <summary>Whole GiB, rounded down, never below one: what a runtime that counts in GB is given.</summary>
    public static int GiB(long mib) => (int)Math.Max(1, mib / 1024);

    internal static string Invariant(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
