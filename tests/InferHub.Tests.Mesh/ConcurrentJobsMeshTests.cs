using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
using InferHub.Node.Profiles;
using InferHub.Node.Tools;
using InferHub.Node.Vector;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 107 across a real wire. SignalR hands a client the hub's calls one at a time and waits for
/// each handler, so until 107 a node ran one hub job at a time whatever <c>MaxConcurrency</c> it
/// declared, and a <c>CancelJob</c> waited behind the job it was meant to stop. Only a real
/// <see cref="CoordinatorConnection"/> on a real hub shows either: a stubbed dispatcher has no
/// dispatch loop to be serial in.
/// </summary>
public class ConcurrentJobsMeshTests
{
    [Fact]
    public async Task TwoJobsSentToOneNodeRunSideBySide()
    {
        await using var mesh = await JobMesh.StartAsync();

        var first = mesh.Dispatcher.DispatchAsync(mesh.Node(), Chat(), CancellationToken.None);
        var second = mesh.Dispatcher.DispatchAsync(mesh.Node(), Chat(), CancellationToken.None);

        // Both are inside the backend at once. Serially, the second never starts while the first is
        // held, and this wait is the whole test.
        await mesh.WaitForAsync(() => mesh.Backend.Running == 2);
        Assert.Equal(2, mesh.Backend.MostAtOnce);

        mesh.Backend.Release();

        Assert.True((await first).Success);
        Assert.True((await second).Success);
    }

    [Fact]
    public async Task ACancelReachesTheJobItCancelsWhileItIsRunning()
    {
        await using var mesh = await JobMesh.StartAsync();
        using var caller = new CancellationTokenSource();

        var dispatch = mesh.Dispatcher.DispatchAsync(mesh.Node(), Chat(), caller.Token);
        await mesh.WaitForAsync(() => mesh.Backend.Running == 1);

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);

        // The backend's own token, on the node, is what stops the GPU. Before 107 it was never
        // cancelled: the hub's registration was disposed before it fired, and the node's dispatch
        // loop would have queued the cancel behind the job anyway.
        await mesh.WaitForAsync(() => mesh.Backend.Cancelled == 1);
    }

    [Fact]
    public async Task AStreamAbandonedBeforeItsFirstChunkIsCancelledOnTheNode()
    {
        await using var mesh = await JobMesh.StartAsync();
        using var caller = new CancellationTokenSource();

        var dispatch = mesh.Dispatcher.DispatchStreamAsync(mesh.Node(), Chat(stream: true), caller.Token);
        await mesh.WaitForAsync(() => mesh.Backend.Running == 1);

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);

        // Before 107 the dispatch's own catch took the pending stream first and nothing told the
        // node — which, with an Ollama that buffered whole answers, was every stream a client left.
        await mesh.WaitForAsync(() => mesh.Backend.Cancelled == 1);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, 3, 3)]
    [InlineData(2, 3, 2)]
    [InlineData(2, null, 2)]
    public async Task TheNodeDeclaresItsModelSlotsAndTheOperatorsNumberWins(int? configured, int? backend, int? expected)
    {
        // Phase 108 D2, through a real registration: Node:ModelSlots over what the backend declares.
        await using var mesh = await JobMesh.StartAsync(configured, backend);

        Assert.Equal(expected, mesh.Registry.GetModelSlots(mesh.Node().ConnectionId));
    }

    [Fact]
    public async Task AJobHoldsItsModelsSlotAtTheHubUntilItsAnswerArrives()
    {
        await using var mesh = await JobMesh.StartAsync(configuredSlots: 1);
        var connection = mesh.Node().ConnectionId;

        var blocking = mesh.Dispatcher.DispatchAsync(mesh.Node(), Chat(), CancellationToken.None);
        // The stream's dispatch returns at its first chunk, which the held backend sends only on release.
        var streamed = mesh.Dispatcher.DispatchStreamAsync(mesh.Node(), Chat(stream: true, model: "qwen2.5"), CancellationToken.None);
        await mesh.WaitForAsync(() => mesh.Backend.Running == 2);

        // Phase 108 D3: per model, beside the node's own counter.
        Assert.Equal(1, mesh.Registry.GetModelInFlight(connection, "llama3"));
        Assert.Equal(1, mesh.Registry.GetModelInFlight(connection, "qwen2.5:latest"));
        Assert.Equal(2, mesh.Registry.GetLocalInFlight(connection));

        mesh.Backend.Release();
        Assert.True((await blocking).Success);
        await foreach (var _ in (await streamed).ReadAllAsync())
        {
        }

        await mesh.WaitForAsync(() => mesh.Registry.GetLocalInFlight(connection) == 0);
        Assert.Equal(0, mesh.Registry.GetModelInFlight(connection, "llama3"));
        Assert.Equal(0, mesh.Registry.GetModelInFlight(connection, "qwen2.5"));
    }

    [Fact]
    public async Task ACancelledJobGivesItsModelsSlotBack()
    {
        await using var mesh = await JobMesh.StartAsync(configuredSlots: 1);
        var connection = mesh.Node().ConnectionId;
        using var caller = new CancellationTokenSource();

        var dispatch = mesh.Dispatcher.DispatchAsync(mesh.Node(), Chat(), caller.Token);
        await mesh.WaitForAsync(() => mesh.Registry.GetModelInFlight(connection, "llama3") == 1);

        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);

        Assert.Equal(0, mesh.Registry.GetModelInFlight(connection, "llama3"));
        Assert.Equal(0, mesh.Registry.GetLocalInFlight(connection));
    }

    private static InferenceJob Chat(bool stream = false, string model = "llama3")
        => new(Guid.NewGuid(), "chat", stream
            ? $$"""{"model":"{{model}}","messages":[],"stream":true}"""
            : $$"""{"model":"{{model}}","messages":[]}""");

    private sealed class JobMesh : IAsyncDisposable
    {
        private const string NodeId = "concurrent-node";
        private const string Secret = "concurrent-mesh-secret";

        private WebApplication app = null!;
        private CoordinatorConnection node = null!;
        private string scratch = null!;

        public NodeRegistry Registry { get; } = new();

        public HeldBackend Backend { get; } = new();

        public Dispatcher Dispatcher { get; private set; } = null!;

        private int? configuredSlots;

        public static async Task<JobMesh> StartAsync(int? configuredSlots = null, int? backendSlots = null)
        {
            var mesh = new JobMesh
            {
                scratch = Path.Combine(Path.GetTempPath(), "inferhub-concurrent-" + Guid.NewGuid().ToString("N")),
                configuredSlots = configuredSlots
            };
            mesh.Backend.ModelSlots = backendSlots;
            await mesh.StartCoordinatorAsync();
            await mesh.StartNodeAsync();
            return mesh;
        }

        public RoutableNode Node() => new(Registry.FindConnectionIdByNodeId(NodeId)!, NodeId, NodeId);

        public async Task WaitForAsync(Func<bool> predicate)
        {
            for (var i = 0; i < 400 && !predicate(); i++)
            {
                await Task.Delay(25);
            }

            Assert.True(predicate(), "timed out");
        }

        private async Task StartCoordinatorAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            builder.Services.AddSignalR();
            builder.Services.AddSingleton<IOptionsMonitor<ApiKeyOptions>>(new ApiKeyMonitor(new ApiKeyOptions { NodeEnrollmentSecret = Secret }));
            builder.Services.AddSingleton<NodeAuthFilter>();
            builder.Services.AddSingleton<INodeRegistry>(Registry);
            builder.Services.AddSingleton<IProfileRegistry>(new ProfileRegistry(new NoProfileStore(), NullLogger<ProfileRegistry>.Instance));
            builder.Services.AddSingleton<IAuditLog, AuditLog>();
            builder.Services.AddSingleton<IRouter, InferHub.Coordinator.Services.Router>();
            builder.Services.AddSingleton<IConversationAffinity>(new ConversationAffinity(
                Options.Create(new RouterOptions()), new NoAffinityStore(), TimeProvider.System));
            builder.Services.AddSingleton<INodeConnectionTracker, NodeConnectionTracker>();
            builder.Services.AddSingleton<Metrics>();
            builder.Services.AddSingleton<ThroughputTracker>();
            builder.Services.Configure<DispatcherOptions>(_ => { });
            builder.Services.Configure<RouterOptions>(_ => { });
            builder.Services.AddSingleton<Dispatcher>();
            builder.Services.AddSingleton<IDispatcher>(sp => sp.GetRequiredService<Dispatcher>());
            builder.Services.AddSingleton<InferHub.Coordinator.Vector.CollectionOwnership>();
            builder.Services.AddSingleton<InferHub.Coordinator.Vector.NodeCorpusRegistry>();
            builder.Services.AddSingleton<NodeProfileCoordinator>();
            builder.Services.AddSingleton<InferHub.Coordinator.Cluster.IClusterMembership,
                InferHub.Coordinator.Cluster.SingleCoordinatorMembership>();

            app = builder.Build();
            app.MapHub<NodeHub>("/hubs/node");

            await app.StartAsync();
            Dispatcher = app.Services.GetRequiredService<Dispatcher>();
        }

        private async Task StartNodeAsync()
        {
            var toolOptions = Options.Create(new ToolOptions());
            var runtime = new NoToolRuntime();
            var replicas = new ReplicaStore(
                Options.Create(new VectorReplicaOptions { ReplicaDirectory = Path.Combine(scratch, "replicas") }),
                NullLogger<ReplicaStore>.Instance);
            var nodeOptions = Options.Create(new NodeOptions { Name = NodeId, MaxConcurrency = 4, ModelSlots = configuredSlots });

            node = new CoordinatorConnection(
                Options.Create(new CoordinatorOptions
                {
                    Url = app.Urls.First(),
                    EnrollmentSecret = Secret,
                    HeartbeatInterval = TimeSpan.FromSeconds(30),
                    ModelRefreshInterval = TimeSpan.FromSeconds(30)
                }),
                nodeOptions,
                new FixedIdentity(NodeId),
                Backend,
                new InferenceExecutor(Backend, replicas, TestProfiles.IdleRetrieval(), NullLogger<InferenceExecutor>.Instance),
                new ModelCommandExecutor(Backend, NullLogger<ModelCommandExecutor>.Instance),
                new ToolExecutor(runtime, toolOptions, NullLogger<ToolExecutor>.Instance),
                runtime,
                toolOptions,
                new NodeProfileApplier(nodeOptions, toolOptions, Backend, runtime, TestProfiles.IdleRetrieval(), NullLogger<NodeProfileApplier>.Instance),
                TestProfiles.IdleRetrieval(),
                replicas,
                new NoBackendSupervisor(),
                InferHub.Node.Resources.NoResourceGovernor.Instance,
                NullLogger<CoordinatorConnection>.Instance);

            await node.StartAsync(CancellationToken.None);
            await WaitForAsync(() => Registry.FindConnectionIdByNodeId(NodeId) is not null);
        }

        public async ValueTask DisposeAsync()
        {
            Backend.Release();

            try
            {
                await node.StopAsync(CancellationToken.None);
            }
            catch (Exception)
            {
            }

            await app.StopAsync();
            await app.DisposeAsync();

            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        private sealed class FixedIdentity(string nodeId) : INodeIdentity
        {
            public string GetOrCreateNodeId() => nodeId;
        }

        private sealed class ApiKeyMonitor(ApiKeyOptions value) : IOptionsMonitor<ApiKeyOptions>
        {
            public ApiKeyOptions CurrentValue => value;

            public ApiKeyOptions Get(string? name) => value;

            public IDisposable? OnChange(Action<ApiKeyOptions, string?> listener) => null;
        }
    }

    /// <summary>A chat backend that holds every request until released, counting who is inside.</summary>
    private sealed class HeldBackend : IInferenceBackend
    {
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int running;
        private int mostAtOnce;
        private int cancelled;

        public int Running => Volatile.Read(ref running);

        public int MostAtOnce => Volatile.Read(ref mostAtOnce);

        public int Cancelled => Volatile.Read(ref cancelled);

        public void Release() => released.TrySetResult();

        public string Name => "test";

        public string Endpoint => "http://127.0.0.1:0/";

        public IReadOnlyList<string> Kinds { get; } = [CapabilityKinds.Chat];

        public bool SupportsModelManagement => false;

        public int? ModelSlots { get; set; }

        public Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelInfo>?>([new ModelInfo("llama3", "sha256:a", 1), new ModelInfo("qwen2.5", "sha256:b", 1)]);

        public async Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref mostAtOnce, now);

            try
            {
                await released.Task.WaitAsync(cancellationToken);
                return """{"model":"llama3","message":{"role":"assistant","content":"ok"},"done":true}""";
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelled);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }

        public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async IAsyncEnumerable<string> StreamAsync(
            string kind,
            string requestJson,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return await ChatAsync(requestJson, cancellationToken);
        }

        public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task WarmAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static void InterlockedMax(ref int target, int value)
        {
            var seen = Volatile.Read(ref target);

            while (value > seen)
            {
                var previous = Interlocked.CompareExchange(ref target, value, seen);

                if (previous == seen)
                {
                    return;
                }

                seen = previous;
            }
        }
    }
}
