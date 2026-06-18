using InferHub.Coordinator.Services;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

public class ConversationAffinityTests
{
    [Fact]
    public void RecordAndGetReturnsConnection()
    {
        var affinity = new ConversationAffinity(Options(slidingMinutes: 10));

        affinity.Record("conv-1", "connection-a");

        Assert.Equal("connection-a", affinity.GetNodeFor("conv-1"));
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

        affinity.Record("conv-1", "connection-a");
        affinity.Record("conv-1", "connection-b");

        Assert.Equal("connection-b", affinity.GetNodeFor("conv-1"));
    }

    [Fact]
    public void GetExpiresAfterSlidingWindow()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var affinity = new ConversationAffinity(Options(slidingMinutes: 10), time);

        affinity.Record("conv-1", "connection-a");
        Assert.Equal("connection-a", affinity.GetNodeFor("conv-1"));

        time.Advance(TimeSpan.FromMinutes(11));

        Assert.Null(affinity.GetNodeFor("conv-1"));
    }

    [Fact]
    public void GetSlidesWindowOnAccess()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var affinity = new ConversationAffinity(Options(slidingMinutes: 10), time);

        affinity.Record("conv-1", "connection-a");

        time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal("connection-a", affinity.GetNodeFor("conv-1"));

        time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal("connection-a", affinity.GetNodeFor("conv-1"));
    }

    [Fact]
    public void ForgetRemovesMapping()
    {
        var affinity = new ConversationAffinity(Options(slidingMinutes: 10));

        affinity.Record("conv-1", "connection-a");
        affinity.Forget("conv-1");

        Assert.Null(affinity.GetNodeFor("conv-1"));
    }

    [Fact]
    public void ForgetConnectionClearsAllMappingsForThatConnection()
    {
        var affinity = new ConversationAffinity(Options(slidingMinutes: 10));

        affinity.Record("conv-1", "connection-a");
        affinity.Record("conv-2", "connection-a");
        affinity.Record("conv-3", "connection-b");

        var removed = affinity.ForgetConnection("connection-a");

        Assert.Equal(2, removed);
        Assert.Null(affinity.GetNodeFor("conv-1"));
        Assert.Null(affinity.GetNodeFor("conv-2"));
        Assert.Equal("connection-b", affinity.GetNodeFor("conv-3"));
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
