using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

public class AffinityPersistenceTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "inferhub-affinity-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FilePersistenceKeepsFreshConversationAcrossRestart()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        using (var first = NewStore())
        {
            var affinity = new ConversationAffinity(RouterOpts(slidingMinutes: 10), first, time);
            affinity.Record("conv-1", "node-a");
        }

        // "Restart": a brand-new store + affinity over the same directory, a few minutes later.
        time.Advance(TimeSpan.FromMinutes(3));
        using var second = NewStore();
        var reloaded = new ConversationAffinity(RouterOpts(slidingMinutes: 10), second, time);

        Assert.Equal("node-a", reloaded.GetNodeFor("conv-1"));
        Assert.Equal(1, reloaded.Count);
    }

    [Fact]
    public void FilePersistenceDropsExpiredConversationOnLoad()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        using (var first = NewStore())
        {
            var affinity = new ConversationAffinity(RouterOpts(slidingMinutes: 10), first, time);
            affinity.Record("conv-1", "node-a");
        }

        // Past the sliding window before the restart — a hint that was already stale is not resurrected.
        time.Advance(TimeSpan.FromMinutes(11));
        using var second = NewStore();
        var reloaded = new ConversationAffinity(RouterOpts(slidingMinutes: 10), second, time);

        Assert.Null(reloaded.GetNodeFor("conv-1"));
        Assert.Equal(0, reloaded.Count);
    }

    [Fact]
    public void ForgetIsPersistedAcrossRestart()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        using (var first = NewStore())
        {
            var affinity = new ConversationAffinity(RouterOpts(slidingMinutes: 10), first, time);
            affinity.Record("conv-1", "node-a");
            affinity.Record("conv-2", "node-b");
            affinity.Forget("conv-1");
        }

        using var second = NewStore();
        var reloaded = new ConversationAffinity(RouterOpts(slidingMinutes: 10), second, time);

        Assert.Null(reloaded.GetNodeFor("conv-1"));
        Assert.Equal("node-b", reloaded.GetNodeFor("conv-2"));
    }

    [Fact]
    public void SnapshotCompactionSurvivesRestart()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        // SnapshotEveryOps is small so the compaction path (temp file → move → truncate ops) runs.
        using (var first = NewStore(snapshotEveryOps: 4))
        {
            var affinity = new ConversationAffinity(RouterOpts(slidingMinutes: 60), first, time);
            for (var i = 0; i < 20; i++)
            {
                affinity.Record("conv-" + i, "node-" + (i % 3));
            }
        }

        using var second = NewStore(snapshotEveryOps: 4);
        var reloaded = new ConversationAffinity(RouterOpts(slidingMinutes: 60), second, time);

        Assert.Equal(20, reloaded.Count);
        Assert.Equal("node-1", reloaded.GetNodeFor("conv-1"));
        Assert.Equal("node-2", reloaded.GetNodeFor("conv-17"));
    }

    [Fact]
    public void NonePersistenceForgetsAcrossRestart()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var affinity = new ConversationAffinity(RouterOpts(slidingMinutes: 10), new NoAffinityStore(), time);
        affinity.Record("conv-1", "node-a");

        // A "restart" with the default no-op store starts empty — byte-identical to v2.11.
        var reloaded = new ConversationAffinity(RouterOpts(slidingMinutes: 10), new NoAffinityStore(), time);

        Assert.Null(reloaded.GetNodeFor("conv-1"));
        Assert.Equal(0, reloaded.Count);
    }

    [Fact]
    public void PersistedHintForAbsentNodeIsACleanRouterMiss()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        using var store = NewStore();
        var options = RouterOpts(slidingMinutes: 10);
        var affinity = new ConversationAffinity(options, store, time);

        // Pin a conversation to a node that is not (or no longer) in the registry.
        affinity.Record("conv-1", "ghost-node");

        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-a", Registration("node-a", "alpha"), now);
        registry.ReportModels("connection-a", new NodeModels("node-a", [new ModelInfo("llama3", "d", 1)], now), now);

        var router = new Router(registry, affinity, new ThroughputTracker(), options);

        // The ghost hint resolves to nothing among the candidates, so routing falls through cleanly
        // to a live node rather than throwing or returning the absent one.
        var routed = router.Route("llama3", "conv-1");
        Assert.NotNull(routed);
        Assert.Equal("node-a", routed!.NodeId);
    }

    private FileAffinityStore NewStore(int snapshotEveryOps = 256) =>
        new(Options.Create(new AffinityOptions
        {
            Persistence = AffinityOptions.PersistenceFile,
            DataDirectory = dir,
            SnapshotEveryOps = snapshotEveryOps
        }));

    private static IOptions<RouterOptions> RouterOpts(int slidingMinutes) =>
        Options.Create(new RouterOptions
        {
            AffinitySlidingMinutes = slidingMinutes,
            AffinityLoadBreakThreshold = 2
        });

    private static NodeRegistration Registration(string nodeId, string name) =>
        new(nodeId, name, "http://localhost:11434/", "1.0.0");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan delta) => now += delta;
    }
}
