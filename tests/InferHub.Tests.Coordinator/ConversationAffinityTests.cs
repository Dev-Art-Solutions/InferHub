using InferHub.Coordinator.Services;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

public class ConversationAffinityTests
{
    [Fact]
    public void RecordAndGetReturnsNode()
    {
        var affinity = new ConversationAffinity(Options(slidingMinutes: 10));

        affinity.Record("conv-1", "node-a");

        Assert.Equal("node-a", affinity.GetNodeFor("conv-1"));
    }

    [Fact]
    public void GetReturnsNullForUnknownKey()
    {
        var affinity = new ConversationAffinity(Options(slidingMinutes: 10));

        Assert.Null(affinity.GetNodeFor("nope"));
    }

    [Fact]
    public void RecordOverwritesPreviousMapping()
    {
        var affinity = new ConversationAffinity(Options(slidingMinutes: 10));

        affinity.Record("conv-1", "node-a");
        affinity.Record("conv-1", "node-b");

        Assert.Equal("node-b", affinity.GetNodeFor("conv-1"));
    }

    [Fact]
    public void GetExpiresAfterSlidingWindow()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var affinity = new ConversationAffinity(Options(slidingMinutes: 10), time);

        affinity.Record("conv-1", "node-a");
        Assert.Equal("node-a", affinity.GetNodeFor("conv-1"));

        time.Advance(TimeSpan.FromMinutes(11));

        Assert.Null(affinity.GetNodeFor("conv-1"));
    }

    [Fact]
    public void GetSlidesWindowOnAccess()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var affinity = new ConversationAffinity(Options(slidingMinutes: 10), time);

        affinity.Record("conv-1", "node-a");

        time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal("node-a", affinity.GetNodeFor("conv-1"));

        time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal("node-a", affinity.GetNodeFor("conv-1"));
    }

    [Fact]
    public void ForgetRemovesMapping()
    {
        var affinity = new ConversationAffinity(Options(slidingMinutes: 10));

        affinity.Record("conv-1", "node-a");
        affinity.Forget("conv-1");

        Assert.Null(affinity.GetNodeFor("conv-1"));
    }

    [Fact]
    public void ForgetNodeClearsAllMappingsForThatNode()
    {
        var affinity = new ConversationAffinity(Options(slidingMinutes: 10));

        affinity.Record("conv-1", "node-a");
        affinity.Record("conv-2", "node-a");
        affinity.Record("conv-3", "node-b");

        var removed = affinity.ForgetNode("node-a");

        Assert.Equal(2, removed);
        Assert.Null(affinity.GetNodeFor("conv-1"));
        Assert.Null(affinity.GetNodeFor("conv-2"));
        Assert.Equal("node-b", affinity.GetNodeFor("conv-3"));
    }

    [Fact]
    public void CountReflectsLiveEntries()
    {
        var affinity = new ConversationAffinity(Options(slidingMinutes: 10));

        Assert.Equal(0, affinity.Count);

        affinity.Record("conv-1", "node-a");
        affinity.Record("conv-2", "node-b");
        Assert.Equal(2, affinity.Count);

        affinity.Forget("conv-1");
        Assert.Equal(1, affinity.Count);
    }

    private static IOptions<RouterOptions> Options(int slidingMinutes)
    {
        return Microsoft.Extensions.Options.Options.Create(new RouterOptions
        {
            AffinitySlidingMinutes = slidingMinutes,
            AffinityLoadBreakThreshold = 2
        });
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan delta) => now += delta;
    }
}
