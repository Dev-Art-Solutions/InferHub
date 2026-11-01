using System.Collections.Concurrent;
using System.Threading.Channels;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 126 through <c>InferenceCore</c> with the real <c>Router</c>: background work does not take a model's last free
/// places, a node lost makes it wait, a higher class waiting goes first, essential skips the hub's slot wait — and with
/// <c>Priority:Enabled</c> off nothing of it happens.
/// </summary>
public class PriorityTests
{
    private const string ChatJob = """{"model":"llama3","messages":[{"role":"user","content":"hi"}]}""";

    private static readonly ResolvedClient Essential = new("ops", null, Priority: PriorityClasses.Essential);
    private static readonly ResolvedClient Normal = new("team", null);
    private static readonly ResolvedClient Background = new("batch", null, Priority: PriorityClasses.Background);

    [Fact]
    public async Task OffByDefaultBackgroundTakesTheLastPlaceAndNothingIsReported()
    {
        var registry = Seed(slotsA: 1, slotsB: 1);
        registry.IncrementInFlight("connection-a", "llama3");
        var gate = Gate(registry, new PriorityOptions());
        var dispatcher = new HeldDispatcher(registry, hold: false);

        Assert.Equal(PriorityClasses.Normal, gate.ClassOf(Background, null));
        Assert.Null(gate.Snapshot());

        var outcome = await Dispatch(registry, Context(registry, gate, Background, null), dispatcher).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(outcome.IsError);
        Assert.Equal(["node-b"], dispatcher.Started.ToArray());
    }

    [Fact]
    public async Task BackgroundLeavesTheLastFreePlaceForOthersAndGoesWhenThereIsRoom()
    {
        var registry = Seed(slotsA: 1, slotsB: 1);
        registry.IncrementInFlight("connection-a", "llama3");
        var gate = Gate(registry);
        var dispatcher = new HeldDispatcher(registry);

        var batch = Dispatch(registry, Context(registry, gate, Background, null), dispatcher);
        await Task.Delay(300);
        Assert.Empty(dispatcher.Started);
        Assert.Equal(1, Row(gate, PriorityClasses.Background).Waiting);

        // The place it left is there for normal work, at once.
        var team = Dispatch(registry, Context(registry, gate, Normal, null), dispatcher);
        await WaitForAsync(() => dispatcher.Started.Count == 1);
        Assert.Equal("node-b", dispatcher.Started.Single());

        // One place free again is still only the reserve.
        registry.DecrementInFlight("connection-a", "llama3");
        await Task.Delay(300);
        Assert.Single(dispatcher.Started);

        // Two free: background goes.
        dispatcher.Release("node-b");
        Assert.False((await team.WaitAsync(TimeSpan.FromSeconds(5))).IsError);
        await WaitForAsync(() => dispatcher.Started.Count == 2);
        dispatcher.ReleaseAll();
        Assert.False((await batch.WaitAsync(TimeSpan.FromSeconds(5))).IsError);

        var row = Row(gate, PriorityClasses.Background);
        Assert.Equal((1, 1, 0, 1), (row.Held, row.Released, row.TimedOut, row.Requests));
    }

    [Fact]
    public async Task ANodeLostIsCapacityLostAndBackgroundPausesFirst()
    {
        var registry = Seed(slotsA: 1, slotsB: 1);
        var gate = Gate(registry);
        var dispatcher = new HeldDispatcher(registry, hold: false);

        // Two free places: background runs as before.
        Assert.False((await Dispatch(registry, Context(registry, gate, Background, null), dispatcher)).IsError);

        registry.Remove("connection-b");

        var batch = Dispatch(registry, Context(registry, gate, Background, null), dispatcher);
        await Task.Delay(300);
        Assert.Single(dispatcher.Started);

        // Essential and normal still run on what is left.
        Assert.False((await Dispatch(registry, Context(registry, gate, Essential, null), dispatcher).WaitAsync(TimeSpan.FromSeconds(5))).IsError);
        Assert.False((await Dispatch(registry, Context(registry, gate, Normal, null), dispatcher).WaitAsync(TimeSpan.FromSeconds(5))).IsError);
        Assert.False(batch.IsCompleted);

        Seed(registry, "b", 1);
        Assert.False((await batch.WaitAsync(TimeSpan.FromSeconds(5))).IsError);
    }

    [Fact]
    public async Task ABackgroundWaitThatRunsOutIsA503NamingTheReserve()
    {
        var registry = Seed(slotsA: 1, slotsB: null, onlyA: true);
        var gate = Gate(registry, new PriorityOptions { Enabled = true, ReserveSlots = 1, BackgroundMaxWaitSeconds = 1 });

        var outcome = await Dispatch(registry, Context(registry, gate, Background, null), new HeldDispatcher(registry)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(503, outcome.ErrorStatus);
        Assert.Contains("Priority:ReserveSlots", outcome.ErrorMessage);
        Assert.Equal(1, outcome.RetryAfterSeconds);
        Assert.Equal(1, Row(gate, PriorityClasses.Background).TimedOut);
    }

    [Fact]
    public async Task ANodeThatDeclaresNoLimitHasNothingToReserveSoBackgroundIsNeverHeld()
    {
        var registry = Seed(slotsA: null, slotsB: null, onlyA: true);
        var gate = Gate(registry);
        var dispatcher = new HeldDispatcher(registry, hold: false);

        Assert.Null(gate.FreePlaces("llama3", "chat"));
        Assert.False((await Dispatch(registry, Context(registry, gate, Background, null), dispatcher).WaitAsync(TimeSpan.FromSeconds(5))).IsError);
    }

    [Theory]
    [InlineData("essential", null, "essential")]
    [InlineData("essential", "background", "background")]
    [InlineData("essential", "NORMAL", "normal")]
    [InlineData("background", "essential", "background")]
    [InlineData(null, "essential", "normal")]
    [InlineData(null, "urgent", "normal")]
    [InlineData("normal", "background", "background")]
    public void ARequestMayLowerItsClassNeverRaiseIt(string? client, string? header, string expected)
    {
        var gate = Gate(new NodeRegistry());

        Assert.Equal(expected, gate.ClassOf(new ResolvedClient("c", null, Priority: client), header));
    }

    [Fact]
    public async Task WaitingForCapacityAHigherClassGoesFirst()
    {
        var registry = Seed(slotsA: null, slotsB: null, onlyA: true, maxConcurrency: 1);
        registry.IncrementInFlight("connection-a", "llama3");
        var gate = Gate(registry);
        var queue = new RequestQueue(registry, Options.Create(new QueueOptions { MaxWaitSeconds = 10 }), NullLogger<RequestQueue>.Instance, gate);

        var normal = queue.WaitForCapacityAsync("llama3", PriorityClasses.Normal, CancellationToken.None);
        await Task.Delay(100);
        var essential = queue.WaitForCapacityAsync("llama3", PriorityClasses.Essential, CancellationToken.None);
        await Task.Delay(100);

        // One place frees: the essential request, though it came second.
        registry.DecrementInFlight("connection-a", "llama3");
        Assert.Equal(QueueOutcome.Admitted, await essential.WaitAsync(TimeSpan.FromSeconds(5)));
        registry.IncrementInFlight("connection-a", "llama3");
        await Task.Delay(400);
        Assert.False(normal.IsCompleted);

        registry.DecrementInFlight("connection-a", "llama3");
        Assert.Equal(QueueOutcome.Admitted, await normal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Row(gate, PriorityClasses.Essential).Held);
        Assert.Equal(1, Row(gate, PriorityClasses.Normal).Released);
    }

    [Fact]
    public async Task EssentialWorkIsNotHeldAtTheHubForAFreeSlot()
    {
        var registry = Seed(slotsA: 1, slotsB: 1);
        var gate = Gate(registry);
        var dispatcher = new HeldDispatcher(registry);
        var queueOptions = new QueueOptions { SlotWaitSeconds = 10 };

        var first = Dispatch(registry, Context(registry, gate, Normal, null, queueOptions), dispatcher);
        var second = Dispatch(registry, Context(registry, gate, Normal, null, queueOptions), dispatcher);
        await WaitForAsync(() => dispatcher.Started.Count == 2);

        var held = Dispatch(registry, Context(registry, gate, Normal, null, queueOptions), dispatcher);
        await Task.Delay(300);
        Assert.Equal(2, dispatcher.Started.Count);

        var ops = Dispatch(registry, Context(registry, gate, Essential, null, queueOptions), dispatcher);
        await WaitForAsync(() => dispatcher.Started.Count == 3);
        Assert.False(held.IsCompleted);

        dispatcher.ReleaseAll();
        foreach (var task in new[] { first, second, held, ops })
        {
            Assert.False((await task.WaitAsync(TimeSpan.FromSeconds(5))).IsError);
        }
    }

    [Fact]
    public void AMisspeltClassFailsStartupAndTheBoundsAreChecked()
    {
        var clients = new ApiKeyOptionsValidator().Validate(null, new ApiKeyOptions
        {
            Clients = [new ClientConfig { Id = "ops", Key = "k1", Priority = "essentail" }]
        });
        Assert.Contains("essentail", clients.FailureMessage);
        Assert.True(new ApiKeyOptionsValidator().Validate(null, new ApiKeyOptions
        {
            Clients = [new ClientConfig { Id = "ops", Key = "k1", Priority = "Essential" }]
        }).Succeeded);

        var validator = new PriorityOptionsValidator();
        Assert.True(validator.Validate(null, new PriorityOptions { ReserveSlots = -5 }).Succeeded);
        Assert.Contains("Priority:ReserveSlots", validator.Validate(null, new PriorityOptions { Enabled = true, ReserveSlots = -1 }).FailureMessage);
        Assert.Contains("Priority:BackgroundMaxWaitSeconds", validator.Validate(null, new PriorityOptions { Enabled = true, BackgroundMaxWaitSeconds = 0 }).FailureMessage);
    }

    [Fact]
    public void TheScrapeHasOneSeriesPerClassOnlyWhileOn()
    {
        var gate = Gate(new NodeRegistry());
        gate.Counted(PriorityClasses.Essential);

        var on = PrometheusFormatter.Format(Scrape(gate.Snapshot()));
        Assert.Contains("inferhub_priority_reserve_slots 1", on);
        Assert.Contains("inferhub_priority_requests_total{class=\"essential\"} 1", on);
        Assert.Contains("inferhub_priority_requests_total{class=\"background\"} 0", on);
        Assert.Contains("inferhub_priority_waiting{class=\"normal\"} 0", on);

        Assert.DoesNotContain("inferhub_priority", PrometheusFormatter.Format(Scrape(null)));
    }

    // ---- harness -----------------------------------------------------------------------

    private static PrometheusScrape Scrape(PrioritySnapshot? priority)
        => new("test", new Metrics().Snapshot(DateTimeOffset.UtcNow), [], [], new QueueSnapshot(0, 0, 0, 0, 0, null), [], 0, Priority: priority);

    private static PriorityClassSnapshot Row(PriorityGate gate, string name)
        => gate.Snapshot()!.Classes.Single(row => row.Class == name);

    private static PriorityGate Gate(INodeRegistry registry, PriorityOptions? options = null)
        => new(registry, Options.Create(options ?? new PriorityOptions { Enabled = true }), NullLogger<PriorityGate>.Instance);

    private static InferenceCore.ClientContext Context(
        INodeRegistry registry, PriorityGate gate, ResolvedClient client, string? header, QueueOptions? queueOptions = null)
    {
        var admission = new AdmissionControl();
        return new InferenceCore.ClientContext(
            client,
            admission,
            TestUsage.Meter(admission: admission),
            new RequestQueue(registry, Options.Create(queueOptions ?? new QueueOptions()), NullLogger<RequestQueue>.Instance, gate),
            gate.Enabled ? gate : null,
            gate.ClassOf(client, header));
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 200 && !predicate(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(predicate(), "timed out");
    }

    private static NodeRegistry Seed(int? slotsA, int? slotsB, bool onlyA = false, int? maxConcurrency = null)
    {
        var registry = new NodeRegistry();
        Seed(registry, "a", slotsA, maxConcurrency);

        if (!onlyA)
        {
            Seed(registry, "b", slotsB, maxConcurrency);
        }

        return registry;
    }

    private static void Seed(NodeRegistry registry, string id, int? slots, int? maxConcurrency = null)
    {
        var now = DateTimeOffset.UtcNow;
        registry.Upsert(
            $"connection-{id}",
            new NodeRegistration($"node-{id}", $"{id}-node", "http://localhost:11434/", "3.86.0", MaxConcurrency: maxConcurrency, ModelSlots: slots),
            now);
        registry.ReportModels($"connection-{id}", new NodeModels($"node-{id}", [new ModelInfo("llama3", $"digest-{id}", 100)], now), now);
    }

    private static Task<InferenceCore.DispatchOutcome> Dispatch(NodeRegistry registry, InferenceCore.ClientContext context, IDispatcher dispatcher)
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

    /// <summary>Counts a job against its node before its first await, as the real dispatcher does, and holds it until released.</summary>
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
