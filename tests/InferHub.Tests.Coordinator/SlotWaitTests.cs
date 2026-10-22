using System.Collections.Concurrent;
using System.Threading.Channels;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 110. With <c>Queue:SlotWaitSeconds</c> set, a request whose every holder is running all the
/// jobs it has slots for waits at the hub and leaves for the first node that frees one — never a 503,
/// one waiter per freed slot, and nothing at all when the key is left at 0.
/// </summary>
public class SlotWaitTests
{
    private const string ChatJob = """{"model":"llama3","messages":[{"role":"user","content":"hi"}]}""";

    [Fact]
    public async Task ThirdChatWaitsAtTheHubAndGoesToTheNodeThatFreesFirst()
    {
        var registry = Seed(slotsA: 1, slotsB: 1);
        var dispatcher = new HeldDispatcher(registry);
        var context = TestUsage.Context(registry, queueOptions: new QueueOptions { SlotWaitSeconds = 10 });

        var first = Dispatch(registry, context, dispatcher);
        var second = Dispatch(registry, context, dispatcher);
        await WaitForAsync(() => dispatcher.Started.Count == 2);
        Assert.Equal(["node-a", "node-b"], dispatcher.Started.OrderBy(id => id).ToArray());

        var third = Dispatch(registry, context, dispatcher);
        await Task.Delay(300);

        // Without the wait it is already inside one of the two engines.
        Assert.Equal(2, dispatcher.Started.Count);
        Assert.Equal(1, context.Queue.Snapshot().SlotWait!.Depth);

        dispatcher.Release("node-b");
        Assert.False((await second.WaitAsync(TimeSpan.FromSeconds(5))).IsError);
        await WaitForAsync(() => dispatcher.Started.Count == 3);

        Assert.Equal("node-b", dispatcher.Started.Last());

        dispatcher.ReleaseAll();
        Assert.False((await first.WaitAsync(TimeSpan.FromSeconds(5))).IsError);
        Assert.False((await third.WaitAsync(TimeSpan.FromSeconds(5))).IsError);
        Assert.Equal(1, context.Queue.Snapshot().SlotWait!.Admitted);
    }

    [Fact]
    public async Task OneFreedSlotLetsOneWaiterGoNotThree()
    {
        var registry = Seed(slotsA: 1, slotsB: 1);
        registry.IncrementInFlight("connection-a", "llama3");
        registry.IncrementInFlight("connection-b", "llama3");
        var queue = Queue(registry, new QueueOptions { SlotWaitSeconds = 10 });

        var waiters = Enumerable.Range(0, 3)
            .Select(_ => queue.WaitForFreeSlotAsync("llama3", "chat", CancellationToken.None))
            .ToArray();
        await Task.Delay(200);
        Assert.DoesNotContain(waiters, w => w.IsCompleted);

        registry.DecrementInFlight("connection-a", "llama3");
        var admitted = await Task.WhenAny(waiters).WaitAsync(TimeSpan.FromSeconds(5));

        // Hold the line as InferenceCore does until the job is counted: every other waiter sees the
        // slot free for as long as this one has not dispatched yet.
        await Task.Delay(300);
        Assert.Equal(1, waiters.Count(w => w.IsCompleted));

        registry.IncrementInFlight("connection-a", "llama3");
        (await admitted).Dispose();
        await Task.Delay(300);
        Assert.Equal(1, waiters.Count(w => w.IsCompleted));

        registry.DecrementInFlight("connection-b", "llama3");
        await WaitForAsync(() => waiters.Count(w => w.IsCompleted) == 2);
    }

    [Fact]
    public async Task AWaitThatRunsOutSendsTheRequestAnywayNeverA503()
    {
        var registry = Seed(slotsA: 1, slotsB: null);
        registry.Remove("connection-b");
        registry.IncrementInFlight("connection-a", "llama3");
        var context = TestUsage.Context(registry, queueOptions: new QueueOptions { SlotWaitSeconds = 1 });
        var dispatcher = new HeldDispatcher(registry, hold: false);

        var outcome = await Dispatch(registry, context, dispatcher).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(outcome.IsError);
        Assert.Equal(InferenceCore.ServedByNode, outcome.ServedBy);
        Assert.Equal(["node-a"], dispatcher.Started);
        Assert.Equal(1, context.Queue.Snapshot().SlotWait!.SentAnyway);
    }

    [Fact]
    public async Task OffByDefaultAFullFleetDispatchesAtOnceAndReportsNothing()
    {
        var registry = Seed(slotsA: 1, slotsB: 1);
        registry.IncrementInFlight("connection-a", "llama3");
        registry.IncrementInFlight("connection-b", "llama3");
        var context = TestUsage.Context(registry);
        var dispatcher = new HeldDispatcher(registry, hold: false);

        var outcome = await Dispatch(registry, context, dispatcher).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(outcome.IsError);
        Assert.Null(context.Queue.Snapshot().SlotWait);
        Assert.DoesNotContain("slotWait", System.Text.Json.JsonSerializer.Serialize(
            StatusEndpoint.BuildQueueBlock(context.Queue),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task ANodeThatDeclaredNothingIsAlwaysFreeSoNobodyWaits()
    {
        var registry = Seed(slotsA: 1, slotsB: null);
        registry.IncrementInFlight("connection-a", "llama3");
        registry.IncrementInFlight("connection-b", "llama3");
        var queue = Queue(registry, new QueueOptions { SlotWaitSeconds = 10 });

        Assert.False(FleetSaturation.HasNoFreeSlot(registry, "llama3", "chat"));
        Assert.Same(SlotTicket.None, await queue.WaitForFreeSlotAsync("llama3", "chat", CancellationToken.None));
        Assert.Equal(0, queue.Snapshot().SlotWait!.Waited);
    }

    [Fact]
    public async Task PastTheDepthBoundARequestGoesStraightOn()
    {
        var registry = Seed(slotsA: 1, slotsB: 1);
        registry.IncrementInFlight("connection-a", "llama3");
        registry.IncrementInFlight("connection-b", "llama3");
        var queue = Queue(registry, new QueueOptions { SlotWaitSeconds = 10, MaxDepth = 1 });

        var occupant = queue.WaitForFreeSlotAsync("llama3", "chat", CancellationToken.None);
        await Task.Delay(50);

        Assert.Same(SlotTicket.None, await queue.WaitForFreeSlotAsync("llama3", "chat", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, queue.Snapshot().SlotWait!.SentAnyway);

        registry.DecrementInFlight("connection-a", "llama3");
        (await occupant.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task ACallerWhoLeavesStopsWaiting()
    {
        var registry = Seed(slotsA: 1, slotsB: 1);
        registry.IncrementInFlight("connection-a", "llama3");
        registry.IncrementInFlight("connection-b", "llama3");
        var queue = Queue(registry, new QueueOptions { SlotWaitSeconds = 10 });
        using var caller = new CancellationTokenSource();

        var waiting = queue.WaitForFreeSlotAsync("llama3", "chat", caller.Token);
        caller.CancelAfter(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, queue.Snapshot().SlotWait!.Depth);
    }

    [Fact]
    public void AModelsOwnSlotCountIsWhatFullMeans()
    {
        // 109's per-model number feeds the same predicate: four of llama3 fit on node A.
        var registry = Seed(slotsA: 1, slotsB: 1);
        registry.ReportModels("connection-a", new NodeModels("node-a", [new ModelInfo("llama3", "d", 1, Slots: 4)], DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
        registry.IncrementInFlight("connection-b", "llama3");

        for (var i = 0; i < 3; i++)
        {
            registry.IncrementInFlight("connection-a", "llama3");
        }

        Assert.False(FleetSaturation.HasNoFreeSlot(registry, "llama3"));
        registry.IncrementInFlight("connection-a", "llama3");
        Assert.True(FleetSaturation.HasNoFreeSlot(registry, "llama3"));
    }

    // ---- harness -----------------------------------------------------------------------

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 200 && !predicate(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(predicate(), "timed out");
    }

    private static NodeRegistry Seed(int? slotsA, int? slotsB)
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        foreach (var (id, slots) in new[] { ("a", slotsA), ("b", slotsB) })
        {
            registry.Upsert(
                $"connection-{id}",
                new NodeRegistration($"node-{id}", $"{id}-node", "http://localhost:11434/", "3.74.0", ModelSlots: slots),
                now);
            registry.ReportModels(
                $"connection-{id}",
                new NodeModels($"node-{id}", [new ModelInfo("llama3", $"digest-{id}", 100)], now),
                now);
        }

        return registry;
    }

    private static RequestQueue Queue(INodeRegistry registry, QueueOptions options)
        => new(registry, Options.Create(options), NullLogger<RequestQueue>.Instance);

    private static Task<InferenceCore.DispatchOutcome> Dispatch(
        NodeRegistry registry,
        InferenceCore.ClientContext context,
        IDispatcher dispatcher)
    {
        var options = Options.Create(new RouterOptions());
        return InferenceCore.DispatchAsync(
            "chat",
            ChatJob,
            "llama3",
            stream: false,
            conversationKey: null,
            context,
            new Router(registry, new ConversationAffinity(options), new ThroughputTracker(), options),
            registry,
            dispatcher,
            new NoProvider(),
            new Metrics(),
            NullLogger.Instance,
            CancellationToken.None);
    }

    /// <summary>
    /// Counts a job against its node before its first await, as <see cref="Dispatcher"/> does — the
    /// ordering 110 D3 leans on — and holds it until that node is released.
    /// </summary>
    private sealed class HeldDispatcher(NodeRegistry registry, bool hold = true) : IDispatcher
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> gates = new();

        public ConcurrentQueue<string> Started { get; } = new();

        public void Release(string nodeId) => Gate(nodeId).TrySetResult();

        public void ReleaseAll()
        {
            Release("node-a");
            Release("node-b");
        }

        public async Task<InferenceResult> DispatchAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
        {
            registry.IncrementInFlight(node.ConnectionId, "llama3");
            Started.Enqueue(node.NodeId);

            try
            {
                if (hold)
                {
                    await Gate(node.NodeId).Task.WaitAsync(cancellationToken);
                }

                return InferenceResult.Succeeded(job.JobId, """{"model":"llama3","done":true}""");
            }
            finally
            {
                registry.DecrementInFlight(node.ConnectionId, "llama3");
            }
        }

        private TaskCompletionSource Gate(string nodeId)
            => gates.GetOrAdd(nodeId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public Task<ChannelReader<InferenceChunk>> DispatchStreamAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public bool Complete(InferenceResult result) => throw new NotImplementedException();

        public bool WriteChunk(InferenceChunk chunk) => throw new NotImplementedException();

        public void FailForConnection(string connectionId, Exception? exception) => throw new NotImplementedException();
    }

    private sealed class NoProvider : IProviderDispatcher
    {
        public ProviderDecision Decide(string model, bool hasCapableNode, ProviderSteer steer) => ProviderDecision.No;

        public Task<ProviderResult> DispatchAsync(string kind, string rawJson, string model, bool stream, CancellationToken cancellationToken)
            => throw new InvalidOperationException("must not be called");
    }
}
