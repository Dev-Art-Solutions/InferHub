namespace InferHub.Coordinator.Services;

public sealed class RouterOptions
{
    public const string StrategyLeastBusy = "least-busy";
    public const string StrategyThroughput = "throughput";

    public int AffinitySlidingMinutes { get; set; } = 10;

    public int AffinityLoadBreakThreshold { get; set; } = 2;

    /// <summary>
    /// How the router breaks between capable nodes (phase 26): <c>least-busy</c> (default, the
    /// pre-v2.8 behaviour, bit-for-bit) or <c>throughput</c> (measured tokens/sec adjusted for
    /// in-flight load). Opt-in for one release — the default is unchanged until there is evidence.
    /// </summary>
    public string Strategy { get; set; } = StrategyLeastBusy;
}
