using System.Threading.Channels;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

public class QueueTests
{
    private const string ChatJob = """{"model":"llama3","messages":[{"role":"user","content":"hi"}]}""";

    // ---- saturation definition -----------------------------------------------------------

    [Fact]
    public void AFleetWithNoCapableNodesIsNotAQueueingSituation()
    {
        // Waiting for a slot only makes sense when there are nodes to free one. No nodes is
        // the 404/fallback path's job, not the queue's.
        var registry = new NodeRegistry();

        Assert.False(FleetSaturation.HasSaturatedFleet(registry, "llama3"));
        Assert.True(FleetSaturation.IsSaturated(registry, "llama3")); // cloud burst's question
    }

    [Fact]
    public void ANodeWithNoDeclaredCapNeverQueues()
    {
        var registry = SaturableRegistry(maxConcurrency: null);

        for (var i = 0; i < 50; i++)
        {
            registry.IncrementInFlight("conn-a");
        }

        Assert.False(FleetSaturation.HasSaturatedFleet(registry, "llama3"));
    }

    // ---- the wait --------------------------------------------------------------------------

    [Fact]
    public async Task AWaiterIsAdmittedWhenASlotFrees()
    {
        var registry = SaturableRegistry(maxConcurrency: 1);
        registry.IncrementInFlight("conn-a");
        var queue = Queue(registry, new QueueOptions { MaxWaitSeconds = 10 });

        var waiter = queue.WaitForCapacityAsync("llama3", CancellationToken.None);
        await Task.Delay(300);
        Assert.False(waiter.IsCompleted);

        registry.DecrementInFlight("conn-a");

        Assert.Equal(QueueOutcome.Admitted, await waiter.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TheWaitBoundIsReal()
    {
        var registry = SaturableRegistry(maxConcurrency: 1);
        registry.IncrementInFlight("conn-a");
        var queue = Queue(registry, new QueueOptions { MaxWaitSeconds = 0 });

        Assert.Equal(QueueOutcome.TimedOut, await queue.WaitForCapacityAsync("llama3", CancellationToken.None));
    }

    [Fact]
    public async Task TheDepthBoundIsReal()
    {
        var registry = SaturableRegistry(maxConcurrency: 1);
        registry.IncrementInFlight("conn-a");
        var queue = Queue(registry, new QueueOptions { MaxWaitSeconds = 10, MaxDepth = 1 });

        var occupant = queue.WaitForCapacityAsync("llama3", CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal(QueueOutcome.QueueFull, await queue.WaitForCapacityAsync("llama3", CancellationToken.None));

        registry.DecrementInFlight("conn-a");
        Assert.Equal(QueueOutcome.Admitted, await occupant.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task TheSnapshotCountsWhatHappened()
    {
        var registry = SaturableRegistry(maxConcurrency: 1);
        registry.IncrementInFlight("conn-a");
        var queue = Queue(registry, new QueueOptions { MaxWaitSeconds = 0, MaxDepth = 1 });

        await queue.WaitForCapacityAsync("llama3", CancellationToken.None); // times out instantly

        var snapshot = queue.Snapshot();
        Assert.Equal(0, snapshot.Depth);
        Assert.Equal(1, snapshot.Queued);
        Assert.Equal(1, snapshot.TimedOut);
        Assert.Equal(0, snapshot.Admitted);
        Assert.NotNull(snapshot.MedianWaitMs);
    }

    // ---- InferenceCore: queue vs 503 vs fallback ---------------------------------------------

    [Fact]
    public async Task ASaturatedFleetPastTheBoundIsA503WithRetryAfter()
    {
        var registry = SaturableRegistry(maxConcurrency: 1);
        registry.IncrementInFlight("conn-a");

        var outcome = await Dispatch(registry, new QueueOptions { MaxWaitSeconds = 0 }, new NeverFallback());

        Assert.True(outcome.IsError);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, outcome.ErrorStatus);
        Assert.NotNull(outcome.RetryAfterSeconds);
    }

    [Fact]
    public async Task WithSaturationBurstEnabledTheUpstreamWinsOverTheQueue()
    {
        // The precedence, explicit and tested: a client who opted into burst asked for an
        // answer in seconds, not a place in line.
        var registry = SaturableRegistry(maxConcurrency: 1);
        registry.IncrementInFlight("conn-a");

        var fallback = new RecordingFallback();
        var outcome = await Dispatch(registry, new QueueOptions { MaxWaitSeconds = 10 }, fallback);

        Assert.False(outcome.IsError);
        Assert.Equal(InferenceCore.ServedByFallback, outcome.ServedBy);
        Assert.Equal("llama3", fallback.LastModel);
    }

    [Fact]
    public async Task AnUncappedBusyNodeDispatchesImmediatelyExactlyAsInV26()
    {
        // The regression the queue must not cause: no declared cap, no queueing, no 503.
        var registry = SaturableRegistry(maxConcurrency: null);
        for (var i = 0; i < 50; i++)
        {
            registry.IncrementInFlight("conn-a");
        }

        var outcome = await Dispatch(registry, new QueueOptions { MaxWaitSeconds = 0 }, new NeverFallback());

        Assert.False(outcome.IsError);
        Assert.Equal(InferenceCore.ServedByNode, outcome.ServedBy);
    }

    [Fact]
    public async Task AWaiterThatGetsASlotDispatchesToTheNode()
    {
        var registry = SaturableRegistry(maxConcurrency: 1);
        registry.IncrementInFlight("conn-a");

        var pending = Dispatch(registry, new QueueOptions { MaxWaitSeconds = 10 }, new NeverFallback());
        await Task.Delay(300);
        registry.DecrementInFlight("conn-a");

        var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(outcome.IsError);
        Assert.Equal(InferenceCore.ServedByNode, outcome.ServedBy);
    }

    // ---- harness -----------------------------------------------------------------------

    private static NodeRegistry SaturableRegistry(int? maxConcurrency)
    {
        var registry = new NodeRegistry();
        registry.Upsert(
            "conn-a",
            new NodeRegistration("node-a", "node-a", "http://localhost:11434/", "2.7.0", null, maxConcurrency),
            DateTimeOffset.UtcNow);
        registry.ReportModels(
            "conn-a",
            new NodeModels("node-a", [new ModelInfo("llama3", null, null)], DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow);
        return registry;
    }

    private static RequestQueue Queue(INodeRegistry registry, QueueOptions options)
        => new(registry, Options.Create(options), NullLogger<RequestQueue>.Instance);

    private static Task<InferenceCore.DispatchOutcome> Dispatch(
        NodeRegistry registry,
        QueueOptions queueOptions,
        IFallbackDispatcher fallback)
        => InferenceCore.DispatchAsync(
            "chat",
            ChatJob,
            "llama3",
            stream: false,
            conversationKey: null,
            TestUsage.Context(registry, queueOptions: queueOptions),
            new StubRouter(new RoutableNode("conn-a", "node-a", "alpha")),
            new StubDispatcher(),
            fallback,
            new Metrics(),
            NullLogger.Instance,
            CancellationToken.None);

    private sealed class StubRouter(RoutableNode? node) : InferHub.Coordinator.Services.IRouter
    {
        public RoutableNode? Route(string model, string? conversationKey = null, string? excludeConnectionId = null)
            => node;
    }

    private sealed class StubDispatcher : IDispatcher
    {
        public Task<InferenceResult> DispatchAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
            => Task.FromResult(InferenceResult.Succeeded(job.JobId, """{"model":"llama3","done":true}"""));

        public Task<ChannelReader<InferenceChunk>> DispatchStreamAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public bool Complete(InferenceResult result) => throw new NotImplementedException();

        public bool WriteChunk(InferenceChunk chunk) => throw new NotImplementedException();

        public void FailForConnection(string connectionId, Exception? exception) => throw new NotImplementedException();
    }

    private sealed class NeverFallback : IFallbackDispatcher
    {
        public bool ShouldServe(string model, bool hasCapableNode) => false;

        public Task<FallbackResult> DispatchAsync(string kind, string rawJson, string model, bool stream, CancellationToken cancellationToken)
            => throw new InvalidOperationException("must not be called");
    }

    private sealed class RecordingFallback : IFallbackDispatcher
    {
        public string? LastModel { get; private set; }

        public bool ShouldServe(string model, bool hasCapableNode) => true;

        public Task<FallbackResult> DispatchAsync(string kind, string rawJson, string model, bool stream, CancellationToken cancellationToken)
        {
            LastModel = model;
            return Task.FromResult(new FallbackResult(null, """{"model":"llama3","done":true}"""));
        }
    }
}
