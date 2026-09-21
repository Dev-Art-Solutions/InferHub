using InferHub.Node.Resources;

namespace InferHub.Tests;

/// <summary>
/// Phase 82, D2. The debounce decision is pure precisely so this suite can be a table of scripted
/// readings — the same reasoning <c>VramBudgetTests</c> and <c>NodeProfileClamp</c>'s own suite give
/// for keeping the load-bearing decision I/O-free.
/// </summary>
public class ResourceGovernorTests
{
    private static ResourceSnapshot Cpu(double percent) => new(percent, null);

    [Fact]
    public void NoCapsConfiguredNeverThrottles()
    {
        var logic = new ResourceGovernorLogic();

        for (var i = 0; i < 10; i++)
        {
            logic.Apply(Cpu(100), maxCpuPercent: null, maxGpuPercent: null, sustainedPolls: 1, recoverPolls: 1);
        }

        Assert.False(logic.IsThrottled);
    }

    [Fact]
    public void AnUnmeasuredReadingIsNeverTreatedAsOverTheCap()
    {
        var logic = new ResourceGovernorLogic();

        for (var i = 0; i < 10; i++)
        {
            logic.Apply(new ResourceSnapshot(null, null), maxCpuPercent: 50, maxGpuPercent: null, sustainedPolls: 1, recoverPolls: 1);
        }

        Assert.False(logic.IsThrottled);
    }

    [Fact]
    public void OneOverCapPollDoesNotTripTheDefaultThreeSustainedPolls()
    {
        var logic = new ResourceGovernorLogic();

        logic.Apply(Cpu(95), maxCpuPercent: 80, maxGpuPercent: null, sustainedPolls: 3, recoverPolls: 2);
        logic.Apply(Cpu(95), maxCpuPercent: 80, maxGpuPercent: null, sustainedPolls: 3, recoverPolls: 2);

        Assert.False(logic.IsThrottled);
    }

    [Fact]
    public void SustainedPollsConsecutiveOverCapPollsTrips()
    {
        var logic = new ResourceGovernorLogic();

        var changed1 = logic.Apply(Cpu(95), 80, null, sustainedPolls: 3, recoverPolls: 2);
        var changed2 = logic.Apply(Cpu(95), 80, null, sustainedPolls: 3, recoverPolls: 2);
        var changed3 = logic.Apply(Cpu(95), 80, null, sustainedPolls: 3, recoverPolls: 2);

        Assert.False(changed1);
        Assert.False(changed2);
        Assert.True(changed3);
        Assert.True(logic.IsThrottled);
        Assert.Contains("CPU 95%", logic.Reason);
        Assert.Contains("MaxCpuPercent (80%)", logic.Reason);
    }

    [Fact]
    public void ANonConsecutiveOverCapPollResetsTheStreak()
    {
        var logic = new ResourceGovernorLogic();

        logic.Apply(Cpu(95), 80, null, sustainedPolls: 3, recoverPolls: 2);
        logic.Apply(Cpu(95), 80, null, sustainedPolls: 3, recoverPolls: 2);
        logic.Apply(Cpu(50), 80, null, sustainedPolls: 3, recoverPolls: 2); // one clean poll in the middle
        logic.Apply(Cpu(95), 80, null, sustainedPolls: 3, recoverPolls: 2);
        logic.Apply(Cpu(95), 80, null, sustainedPolls: 3, recoverPolls: 2);

        Assert.False(logic.IsThrottled);
    }

    [Fact]
    public void RecoveryNeedsItsOwnConsecutiveCleanPollsIndependentOfSustainedPolls()
    {
        var logic = new ResourceGovernorLogic();

        // Trip with sustainedPolls=1 so tripping is immediate.
        logic.Apply(Cpu(95), 80, null, sustainedPolls: 1, recoverPolls: 3);
        Assert.True(logic.IsThrottled);

        logic.Apply(Cpu(10), 80, null, sustainedPolls: 1, recoverPolls: 3);
        logic.Apply(Cpu(10), 80, null, sustainedPolls: 1, recoverPolls: 3);
        Assert.True(logic.IsThrottled); // only 2 of the 3 required clean polls so far

        var recovered = logic.Apply(Cpu(10), 80, null, sustainedPolls: 1, recoverPolls: 3);

        Assert.True(recovered);
        Assert.False(logic.IsThrottled);
        Assert.Null(logic.Reason);
    }

    [Fact]
    public void EitherCpuOrGpuOverItsOwnCapIsEnoughToTrip()
    {
        var logic = new ResourceGovernorLogic();

        var changed = logic.Apply(new ResourceSnapshot(10, 95), maxCpuPercent: 80, maxGpuPercent: 80, sustainedPolls: 1, recoverPolls: 1);

        Assert.True(changed);
        Assert.True(logic.IsThrottled);
        Assert.Contains("GPU 95%", logic.Reason);
        Assert.DoesNotContain("CPU", logic.Reason);
    }

    [Fact]
    public void BothOverCapNamesBothInTheReason()
    {
        var logic = new ResourceGovernorLogic();

        logic.Apply(new ResourceSnapshot(95, 95), maxCpuPercent: 80, maxGpuPercent: 80, sustainedPolls: 1, recoverPolls: 1);

        Assert.Contains("CPU 95%", logic.Reason);
        Assert.Contains("GPU 95%", logic.Reason);
    }

    [Fact]
    public void ExactlyAtTheCapIsNotOverIt()
    {
        var logic = new ResourceGovernorLogic();

        logic.Apply(Cpu(80), maxCpuPercent: 80, maxGpuPercent: null, sustainedPolls: 1, recoverPolls: 1);

        Assert.False(logic.IsThrottled);
    }

    [Fact]
    public void NoResourceGovernorIsNeverThrottledAndCostsNothingToTouch()
    {
        var governor = NoResourceGovernor.Instance;

        Assert.False(governor.IsThrottled);
        Assert.Null(governor.Reason);
        Assert.Equal(default, governor.Current);

        // Subscribing/unsubscribing must not throw — nothing downstream should special-case it.
        void Handler() { }
        governor.Changed += Handler;
        governor.Changed -= Handler;
    }
}
