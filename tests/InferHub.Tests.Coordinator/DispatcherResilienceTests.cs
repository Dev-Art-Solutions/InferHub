using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

public class DispatcherResilienceTests
{
    [Fact]
    public async Task DispatchStreamAsyncThrowsNodeDisconnectedWhenNodeDropsBeforeFirstChunk()
    {
        var dispatcher = NewDispatcher(out _, out _);
        var job = new InferenceJob(Guid.NewGuid(), "generate", "{}");
        var node = new RoutableNode("conn-a", "node-a", "alpha");

        var dispatchTask = dispatcher.DispatchStreamAsync(node, job, CancellationToken.None);

        // No chunk has arrived yet → simulate the node disconnecting.
        dispatcher.FailForConnection("conn-a", new IOException("link severed"));

        var ex = await Assert.ThrowsAsync<NodeDisconnectedException>(() => dispatchTask);
        Assert.Equal("conn-a", ex.ConnectionId);
    }

    [Fact]
    public async Task DispatchStreamAsyncReturnsReaderOnceFirstChunkArrives()
    {
        var dispatcher = NewDispatcher(out _, out _);
        var job = new InferenceJob(Guid.NewGuid(), "generate", "{}");
        var node = new RoutableNode("conn-a", "node-a", "alpha");

        var dispatchTask = dispatcher.DispatchStreamAsync(node, job, CancellationToken.None);

        // Push the first chunk before completing the dispatch await.
        dispatcher.WriteChunk(new InferenceChunk(job.JobId, "{\"response\":\"hi\"}", Done: false));

        var reader = await dispatchTask;
        Assert.True(reader.TryRead(out var first));
        Assert.NotNull(first);
        Assert.False(first!.Done);
    }

    [Fact]
    public async Task FailForConnectionAfterStreamStartedEndsReaderWithError()
    {
        var dispatcher = NewDispatcher(out _, out _);
        var job = new InferenceJob(Guid.NewGuid(), "generate", "{}");
        var node = new RoutableNode("conn-a", "node-a", "alpha");

        var dispatchTask = dispatcher.DispatchStreamAsync(node, job, CancellationToken.None);
        dispatcher.WriteChunk(new InferenceChunk(job.JobId, "{\"response\":\"hi\"}", Done: false));
        var reader = await dispatchTask;

        // Drain the first chunk
        Assert.True(reader.TryRead(out _));

        dispatcher.FailForConnection("conn-a", new IOException("link severed"));

        // After the connection drops mid-stream the reader must surface the failure as an
        // exception when callers try to wait for more chunks — not silently complete.
        await Assert.ThrowsAsync<NodeDisconnectedException>(async () =>
        {
            await foreach (var _ in reader.ReadAllAsync())
            {
            }
        });
    }

    [Fact]
    public async Task FailForConnectionFailsPendingBlockingJobWithNodeDisconnected()
    {
        var dispatcher = NewDispatcher(out _, out _);
        var job = new InferenceJob(Guid.NewGuid(), "generate", "{}");
        var node = new RoutableNode("conn-a", "node-a", "alpha");

        var dispatchTask = dispatcher.DispatchAsync(node, job, CancellationToken.None);

        dispatcher.FailForConnection("conn-a", new IOException("link severed"));

        var ex = await Assert.ThrowsAsync<NodeDisconnectedException>(() => dispatchTask);
        Assert.Equal("conn-a", ex.ConnectionId);
    }

    [Fact]
    public async Task FailForConnectionAlsoResetsInFlightCounter()
    {
        var dispatcher = NewDispatcher(out var registry, out _);
        var node = new RoutableNode("conn-a", "node-a", "alpha");
        var job = new InferenceJob(Guid.NewGuid(), "generate", "{}");

        var task = dispatcher.DispatchAsync(node, job, CancellationToken.None);
        Assert.Equal(1, registry.GetLocalInFlight("conn-a"));

        dispatcher.FailForConnection("conn-a", new IOException("gone"));
        await Assert.ThrowsAsync<NodeDisconnectedException>(() => task);

        Assert.Equal(0, registry.GetLocalInFlight("conn-a"));
    }

    [Fact]
    public async Task FailForConnectionRecordsMetricFailure()
    {
        var dispatcher = NewDispatcher(out _, out var metrics);
        var node = new RoutableNode("conn-a", "node-a", "alpha");
        var job = new InferenceJob(Guid.NewGuid(), "generate", "{}");

        var task = dispatcher.DispatchAsync(node, job, CancellationToken.None);
        dispatcher.FailForConnection("conn-a", new IOException("gone"));
        await Assert.ThrowsAsync<NodeDisconnectedException>(() => task);

        var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.Equal(1, snapshot.RequestsFailed);
        Assert.Equal(0, snapshot.RequestsInFlight);
    }

    [Fact]
    public async Task AGroupsAnswerBlockingOrStreamedIsCountedForEveryWorkerOfThatGroup()
    {
        var shards = new NodeShardRegistry();
        var dispatcher = NewDispatcher(out var registry, out _, shards, out var contribution);
        registry.Upsert("conn-b", new NodeRegistration("node-b", "beta", "http://localhost/", "1.0.0"), DateTimeOffset.UtcNow);
        shards.Report(new InferHub.Shared.Fit.NodeShardState("node-a", "running", 2048, 0, null, null, [], null, DateTimeOffset.UtcNow,
            Groups: [new InferHub.Shared.Fit.ShardGroupState("split", "m.gguf", "running", ["beta"], null, null,
                [new InferHub.Shared.Fit.ShardGroupLayers("alpha", 0, 20, false), new InferHub.Shared.Fit.ShardGroupLayers("beta", 20, 12, true)])]));
        var node = new RoutableNode("conn-a", "node-a", "alpha");

        var blocking = new InferenceJob(Guid.NewGuid(), "chat", """{"model":"split"}""");
        var task = dispatcher.DispatchAsync(node, blocking, CancellationToken.None);
        dispatcher.Complete(new InferenceResult(blocking.JobId, true, """{"model":"split","prompt_eval_count":10,"eval_count":22}""", null));
        await task;

        var streamed = new InferenceJob(Guid.NewGuid(), "chat", """{"model":"split","stream":true}""");
        var stream = dispatcher.DispatchStreamAsync(node, streamed, CancellationToken.None);
        dispatcher.WriteChunk(new InferenceChunk(streamed.JobId, """{"model":"split","done":true,"prompt_eval_count":10,"eval_count":22}""", Done: true));
        await stream;

        var beta = contribution.Report().Nodes.Single(n => n.Name == "beta");
        Assert.Equal((2L, 20L, 44L), (beta.Requests, beta.PromptTokens, beta.GeneratedTokens));
        Assert.Equal(64 * 12 / 32.0, beta.ShareTokens);
    }

    private static Dispatcher NewDispatcher(out NodeRegistry registry, out Metrics metrics, NodeShardRegistry shards, out ShardContribution contribution)
    {
        var dispatcher = NewDispatcher(out registry, out metrics);
        contribution = new ShardContribution(shards, registry, TimeProvider.System);
        return new Dispatcher(new NoOpHubContext(), registry, metrics, new ThroughputTracker(),
            Options.Create(new DispatcherOptions { TimeoutSeconds = 30 }), NullLogger<Dispatcher>.Instance, contribution);
    }

    private static Dispatcher NewDispatcher(out NodeRegistry registry, out Metrics metrics)
    {
        registry = new NodeRegistry();
        registry.Upsert(
            "conn-a",
            new NodeRegistration("node-a", "alpha", "http://localhost/", "1.0.0"),
            DateTimeOffset.UtcNow);

        metrics = new Metrics();

        var options = Options.Create(new DispatcherOptions { TimeoutSeconds = 30 });

        return new Dispatcher(
            new NoOpHubContext(),
            registry,
            metrics,
            new ThroughputTracker(),
            options,
            NullLogger<Dispatcher>.Instance);
    }

    private sealed class NoOpHubContext : IHubContext<NodeHub>
    {
        public IHubClients Clients { get; } = new NoOpClients();
        public IGroupManager Groups { get; } = new NoOpGroups();
    }

    private sealed class NoOpClients : IHubClients
    {
        public IClientProxy All { get; } = NoOpProxy.Instance;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => NoOpProxy.Instance;
        public IClientProxy Client(string connectionId) => NoOpProxy.Instance;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => NoOpProxy.Instance;
        public IClientProxy Group(string groupName) => NoOpProxy.Instance;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => NoOpProxy.Instance;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => NoOpProxy.Instance;
        public IClientProxy User(string userId) => NoOpProxy.Instance;
        public IClientProxy Users(IReadOnlyList<string> userIds) => NoOpProxy.Instance;
    }

    private sealed class NoOpProxy : IClientProxy
    {
        public static readonly NoOpProxy Instance = new();

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoOpGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
