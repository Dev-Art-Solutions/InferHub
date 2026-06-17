namespace InferHub.Coordinator.Services;

public sealed class RouterOptions
{
    public int AffinitySlidingMinutes { get; set; } = 10;

    public int AffinityLoadBreakThreshold { get; set; } = 2;
}
