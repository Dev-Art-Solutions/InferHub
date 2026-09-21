using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
using InferHub.Node.Profiles;
using InferHub.Node.Retrieval;
using InferHub.Node.Tools;
using InferHub.Node.Vector;
using InferHub.Shared.Contracts;
using InferHub.Shared.Vector;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 77, over a real wire: a real <see cref="NodeHub"/>, two real <see cref="CoordinatorConnection"/>
/// instances (a primary and a standby), each with its own real <see cref="RetrievalHost"/> running a
/// real <c>local</c> corpus on its own disk. <see cref="NodeCorpusReplicator"/> is driven directly
/// rather than through <see cref="CorpusFailoverService"/>'s timer — the same "exposed for tests"
/// shape <c>ReplicationCoordinator.RecomputeAsync</c> already uses — so promotion is deterministic
/// rather than racing a polling interval.
/// </summary>
public class NodeCorpusReplicationTests
{
    [Fact]
    public async Task AStandbyReceivesTheInitialSnapshotAndEveryLiveWriteAfterIt()
    {
        await using var mesh = await CorpusMesh.StartAsync();

        await mesh.Primary.Retrieval.StartCorpusAsync(CorpusMesh.Local(), CancellationToken.None);
        var store = (LocalVectorStore)mesh.Primary.Retrieval.Current!.Store;
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("d1", [1f, 0f]));

        mesh.Ownership.Assign(CorpusMesh.PrimaryId, ["docs"]);

        var (ok, error) = await mesh.Replicator.AssignStandbyAsync("docs", CorpusMesh.StandbyId, "test", CancellationToken.None);
        Assert.True(ok, error);

        // The initial snapshot arrives via the relay, into the standby's ReplicaStore — no corpus of
        // its own yet, exactly the "holding, not owning" state D1 describes.
        await mesh.WaitForAsync(() => mesh.Standby.Replicas.Collections.Contains("docs"));
        Assert.NotNull(mesh.Standby.Replicas.Query(new InferHub.Shared.Vector.Replication.VectorQueryRequest("docs", [1f, 0f], 5, null)));

        // A live write after the standby is attached is tailed too.
        await store.UpsertAsync("docs", new VectorUpsert("d2", [0f, 1f]));

        await mesh.WaitForAsync(() =>
            mesh.Standby.Replicas.Query(new InferHub.Shared.Vector.Replication.VectorQueryRequest("docs", [1f, 0f], 5, null))?.Count == 2);
    }

    [Fact]
    public async Task WhenThePrimaryIsGonePastGraceThePromotedStandbyServesTheSameData()
    {
        await using var mesh = await CorpusMesh.StartAsync();

        await mesh.Primary.Retrieval.StartCorpusAsync(CorpusMesh.Local(), CancellationToken.None);
        var store = (LocalVectorStore)mesh.Primary.Retrieval.Current!.Store;
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("d1", [1f, 0f]));

        mesh.Ownership.Assign(CorpusMesh.PrimaryId, ["docs"]);
        var (ok, _) = await mesh.Replicator.AssignStandbyAsync("docs", CorpusMesh.StandbyId, "test", CancellationToken.None);
        Assert.True(ok);
        await mesh.WaitForAsync(() => mesh.Standby.Replicas.Collections.Contains("docs"));

        // The primary is gone for good — a disconnect, and CorpusFailoverService's grace period is
        // skipped here by calling PromoteAsync directly, which is what its tick does once elapsed.
        await mesh.Primary.Connection.StopAsync(CancellationToken.None);
        await mesh.Replicator.PromoteAsync("docs", CancellationToken.None);

        // Promotion moves the replica's files into the standby's own corpus directory and reports
        // back; the hub then pushes a profile naming the collection, which is what actually starts
        // the corpus — the ordinary profile-driven path, not a second one.
        await mesh.WaitForAsync(() => string.Equals(mesh.Ownership.NodeOwning("docs"), CorpusMesh.StandbyId, StringComparison.OrdinalIgnoreCase));
        await mesh.WaitForAsync(() => mesh.Standby.Retrieval.Current is not null);

        var promoted = (LocalVectorStore)mesh.Standby.Retrieval.Current!.Store;
        var matches = await promoted.QueryAsync("docs", new VectorQuery([1f, 0f], K: 5));
        Assert.Contains(matches, m => m.Id == "d1");
    }

    private sealed class CorpusMesh : IAsyncDisposable
    {
        public const string PrimaryId = "corpus-primary";
        public const string StandbyId = "corpus-standby";
        private const string Secret = "corpus-mesh-secret";

        private WebApplication app = null!;
        private ToolWorkerFixture.TempDirectory scratch = null!;

        public NodeRegistry Registry { get; } = new();
        public ProfileRegistry Profiles { get; } = new(new NoProfileStore(), NullLogger<ProfileRegistry>.Instance);
        public CollectionOwnership Ownership { get; private set; } = null!;
        public NodeProfileCoordinator Coordinator { get; private set; } = null!;
        public NodeCorpusReplicator Replicator { get; private set; } = null!;

        public NodeHandle Primary { get; private set; } = null!;
        public NodeHandle Standby { get; private set; } = null!;

        public static async Task<CorpusMesh> StartAsync()
        {
            var mesh = new CorpusMesh { scratch = new ToolWorkerFixture.TempDirectory() };
            await mesh.StartCoordinatorAsync();
            mesh.Primary = await mesh.StartNodeAsync(PrimaryId);
            mesh.Standby = await mesh.StartNodeAsync(StandbyId);
            return mesh;
        }

        public static CorpusRequest Local() => new("local", null, null, null, null, CorpusRequest.Profile);

        public async Task WaitForAsync(Func<bool> predicate)
        {
            for (var i = 0; i < 400 && !predicate(); i++)
            {
                await Task.Delay(25);
            }

            Assert.True(predicate());
        }

        private async Task StartCoordinatorAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            builder.Services.AddSignalR();
            builder.Services.AddSingleton<IOptionsMonitor<ApiKeyOptions>>(
                new ApiKeyMonitor(new ApiKeyOptions { NodeEnrollmentSecret = Secret }));
            builder.Services.AddSingleton<NodeAuthFilter>();
            builder.Services.AddSingleton<INodeRegistry>(Registry);
            builder.Services.AddSingleton<IProfileRegistry>(Profiles);
            builder.Services.AddSingleton<IAuditLog, AuditLog>();
            builder.Services.AddSingleton<InferHub.Coordinator.Services.IRouter, Router>();
            builder.Services.AddSingleton<IConversationAffinity>(new ConversationAffinity(
                Options.Create(new RouterOptions()), new NoAffinityStore(), TimeProvider.System));
            builder.Services.AddSingleton<INodeConnectionTracker, NodeConnectionTracker>();
            builder.Services.AddSingleton<Metrics>();
            builder.Services.AddSingleton<ThroughputTracker>();
            builder.Services.Configure<DispatcherOptions>(_ => { });
            builder.Services.Configure<RouterOptions>(_ => { });
            builder.Services.AddSingleton<Dispatcher>();
            builder.Services.AddSingleton<IDispatcher>(sp => sp.GetRequiredService<Dispatcher>());
            builder.Services.AddSingleton<CollectionOwnership>();
            builder.Services.AddSingleton<InferHub.Coordinator.Vector.NodeCorpusRegistry>();
            builder.Services.AddSingleton<NodeProfileCoordinator>();
            builder.Services.AddSingleton<NodeCorpusReplicator>();
            builder.Services.AddSingleton<InferHub.Coordinator.Cluster.IClusterMembership,
                InferHub.Coordinator.Cluster.SingleCoordinatorMembership>();

            app = builder.Build();
            app.MapHub<NodeHub>("/hubs/node");

            await app.StartAsync();
            Coordinator = app.Services.GetRequiredService<NodeProfileCoordinator>();
            Ownership = app.Services.GetRequiredService<CollectionOwnership>();
            Replicator = app.Services.GetRequiredService<NodeCorpusReplicator>();
        }

        private async Task<NodeHandle> StartNodeAsync(string nodeId)
        {
            var nodeRoot = Path.Combine(scratch.Path, nodeId);
            var manifests = new ToolWorkerFixture.TempDirectory("inferhub-corpus-mesh-" + nodeId);
            var toolOptions = ToolWorkerFixture.Options(Path.Combine(scratch.Path, nodeId + "-scratch"), "none");
            toolOptions.ManifestDirectory = manifests.Path;
            toolOptions.Enabled = false; // no tool under test here — an empty, disabled runtime spawns nothing

            var runtime = new ProcessToolRuntime(
                ToolWorkerFixture.Wrap(toolOptions),
                TimeProvider.System,
                NullLoggerFactory.Instance,
                NullLogger<ProcessToolRuntime>.Instance);
            await runtime.StartAsync(CancellationToken.None);

            var services = new ServiceCollection();
            services.AddLogging(logging => logging.ClearProviders());
            services.AddSingleton<InferHub.Shared.Vector.IEmbeddingDispatcher>(new NoEmbeddings());
            services.AddSingleton<InferHub.Shared.Vector.IReranker>(new NoReranker());
            services.AddSingleton<InferHub.Shared.Vector.IRetrievalMetrics>(InferHub.Shared.Vector.NullRetrievalMetrics.Instance);
            services.AddSingleton<InferHub.Shared.Vector.IVectorQueryRouter, InferHub.Shared.Vector.NullVectorQueryRouter>();
            services.AddSingleton(_ => new InferHub.Shared.Ingestion.TextExtractor());

            var retrievalOptions = new LocalRetrievalOptions
            {
                DataDirectory = Path.Combine(nodeRoot, "corpus"),
                DefaultEmbeddingModel = "test-embed",
                ReplicateOwnedCollections = true,
            };

            var retrieval = new RetrievalHost(
                services.BuildServiceProvider(),
                Options.Create(retrievalOptions),
                NullLogger<RetrievalHost>.Instance);

            var replicas = new ReplicaStore(
                Options.Create(new VectorReplicaOptions { ReplicaDirectory = Path.Combine(nodeRoot, "replicas") }),
                NullLogger<ReplicaStore>.Instance);

            var backend = new EmbedOnlyBackend();
            var nodeOptions = Options.Create(new NodeOptions { Name = nodeId, MaxConcurrency = 8 });

            var connection = new CoordinatorConnection(
                Options.Create(new CoordinatorOptions
                {
                    Url = app.Urls.First(),
                    EnrollmentSecret = Secret,
                    HeartbeatInterval = TimeSpan.FromSeconds(30),
                    ModelRefreshInterval = TimeSpan.FromSeconds(30)
                }),
                nodeOptions,
                new FixedIdentity(nodeId),
                backend,
                new InferenceExecutor(backend, replicas, retrieval, NullLogger<InferenceExecutor>.Instance),
                new ModelCommandExecutor(backend, NullLogger<ModelCommandExecutor>.Instance),
                new ToolExecutor(runtime, ToolWorkerFixture.Wrap(toolOptions), NullLogger<ToolExecutor>.Instance),
                runtime,
                ToolWorkerFixture.Wrap(toolOptions),
                new NodeProfileApplier(
                    nodeOptions,
                    ToolWorkerFixture.Wrap(toolOptions),
                    backend,
                    runtime,
                    retrieval,
                    NullLogger<NodeProfileApplier>.Instance),
                retrieval,
                replicas,
                new NoBackendSupervisor(),
                InferHub.Node.Resources.NoResourceGovernor.Instance,
                NullLogger<CoordinatorConnection>.Instance);

            await connection.StartAsync(CancellationToken.None);

            for (var i = 0; i < 200 && Registry.Snapshot(DateTimeOffset.UtcNow).All(n => !string.Equals(n.NodeId, nodeId, StringComparison.OrdinalIgnoreCase)); i++)
            {
                await Task.Delay(25);
            }

            return new NodeHandle(connection, retrieval, replicas, runtime, manifests);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var node in new[] { Primary, Standby })
            {
                if (node is null) continue;

                try { await node.Connection.StopAsync(CancellationToken.None); } catch { }
                await node.Retrieval.DisposeAsync();
                await node.Runtime.StopAsync(CancellationToken.None);
                node.Manifests.Dispose();
            }

            await app.StopAsync();
            await app.DisposeAsync();
            scratch.Dispose();
        }

        public sealed record NodeHandle(
            CoordinatorConnection Connection,
            RetrievalHost Retrieval,
            ReplicaStore Replicas,
            ProcessToolRuntime Runtime,
            ToolWorkerFixture.TempDirectory Manifests);

        private sealed class EmbedOnlyBackend : IInferenceBackend
        {
            public string Name => "test";
            public string Endpoint => "http://127.0.0.1:0/";
            public IReadOnlyList<string> Kinds { get; } = [CapabilityKinds.Chat, CapabilityKinds.Embed];
            public bool SupportsModelManagement => false;

            public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ModelInfo>>([new ModelInfo("nomic-embed-text", "sha256:a", 1)]);

            public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();
            public IAsyncEnumerable<string> StreamAsync(string kind, string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();
            public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task DeleteAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task WarmAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        private sealed class NoEmbeddings : InferHub.Shared.Vector.IEmbeddingDispatcher
        {
            public Task<string> DispatchEmbedAsync(string rawJson, string? modelOverride, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<float[]> EmbedSingleAsync(string text, string? model, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        private sealed class NoReranker : InferHub.Shared.Vector.IReranker
        {
            public Task<IReadOnlyList<InferHub.Shared.Vector.VectorMatch>> RerankAsync(
                string query,
                IReadOnlyList<InferHub.Shared.Vector.VectorMatch> candidates,
                string? model,
                CancellationToken cancellationToken) => Task.FromResult(candidates);
        }

        private sealed class FixedIdentity(string nodeId) : InferHub.Node.INodeIdentity
        {
            public string GetOrCreateNodeId() => nodeId;
        }

        private sealed class ApiKeyMonitor(ApiKeyOptions value) : IOptionsMonitor<ApiKeyOptions>
        {
            public ApiKeyOptions CurrentValue { get; } = value;
            public ApiKeyOptions Get(string? name) => CurrentValue;
            public IDisposable? OnChange(Action<ApiKeyOptions, string?> listener) => null;
        }
    }
}
