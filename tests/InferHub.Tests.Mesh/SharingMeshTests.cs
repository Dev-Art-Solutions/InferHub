using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
using InferHub.Node.Control;
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
/// Phase 113 across a real wire: a real hub, a real <see cref="CoordinatorConnection"/>. The node's owner decides
/// whether it shares; the hub is told and holds it — a paused node keeps its models and gets no new job.
/// </summary>
public sealed class SharingMeshTests
{
    [Fact]
    public async Task ANodeSaysAtRegistrationThatItSharesAndIsRoutable()
    {
        await using var mesh = await SharingMesh.StartAsync(SharingOptions.Always);

        var state = await mesh.WaitForSharingAsync(s => s is { Paused: false });
        Assert.Equal("always", state.Mode);
        Assert.Single(mesh.Registry.FindNodesWithModel("llama3"));
        Assert.True(mesh.Connection.HubHoldsSharing);
    }

    [Fact]
    public async Task InIdleModeTheOwnersOwnJobPausesSharingAtTheHubAndItResumesAfter()
    {
        await using var mesh = await SharingMesh.StartAsync(SharingOptions.Idle, idleSeconds: 1);
        await mesh.WaitForSharingAsync(s => s is { Paused: false });

        using (mesh.Activity.Begin("chat", "llama3"))
        {
            var paused = await mesh.WaitForSharingAsync(s => s is { Paused: true });
            Assert.Contains("its owner is using it", paused.Reason);
            Assert.Empty(mesh.Registry.FindNodesWithModel("llama3"));
            Assert.Single(mesh.Registry.FindNodesWithModel("llama3", includeUnserviceable: true));
        }

        await mesh.WaitForSharingAsync(s => s is { Paused: false });
        Assert.Single(mesh.Registry.FindNodesWithModel("llama3"));
    }

    [Fact]
    public async Task TheOwnersPauseReachesTheHubLiveAndAHubJobNeverPausesIt()
    {
        await using var mesh = await SharingMesh.StartAsync(SharingOptions.Idle);
        await mesh.WaitForSharingAsync(s => s is { Paused: false });

        using (NodeActivity.FromMesh())
        using (mesh.Activity.Begin("chat", "llama3"))
        {
            await Task.Delay(200);
            Assert.False(mesh.Sharing.Paused);
        }

        mesh.Options.Set(new NodeOptions { Name = SharingMesh.NodeId, Sharing = new SharingOptions { Mode = SharingOptions.Paused } });

        var paused = await mesh.WaitForSharingAsync(s => s is { Paused: true });
        Assert.Equal("paused by its owner", paused.Reason);
        Assert.Empty(mesh.Registry.FindNodesWithModel("llama3"));
    }

    [Fact]
    public async Task OutsideItsHoursTheHubHearsWhyAndWhenItSharesAgain()
    {
        await using var mesh = await SharingMesh.StartAsync(SharingOptions.Always);
        await mesh.WaitForSharingAsync(s => s is { Paused: false });

        // Phase 116: one hour, two days from now — closed whenever this test runs.
        var day = DateTimeOffset.UtcNow.AddDays(2).DayOfWeek.ToString()[..3].ToLowerInvariant();
        mesh.Options.Set(new NodeOptions { Name = SharingMesh.NodeId, Sharing = new SharingOptions { Hours = [$"{day} 10:00-11:00"], TimeZone = "UTC" } });

        var paused = await mesh.WaitForSharingAsync(s => s is { Paused: true });
        Assert.StartsWith("outside its sharing hours; shares again", paused.Reason);
        Assert.NotNull(paused.ResumesAtUtc);
        Assert.Equal(10, paused.ResumesAtUtc!.Value.Hour);
        Assert.Empty(mesh.Registry.FindNodesWithModel("llama3"));
        Assert.Single(mesh.Registry.FindNodesWithModel("llama3", includeUnserviceable: true));
    }

    private sealed class SharingMesh : IAsyncDisposable
    {
        public const string NodeId = "sharing-node";
        private const string Secret = "sharing-mesh-secret";

        private WebApplication app = null!;
        private string scratch = null!;

        public NodeRegistry Registry { get; } = new();

        public NodeActivity Activity { get; } = new();

        public Monitor Options { get; private set; } = null!;

        public NodeSharing Sharing { get; private set; } = null!;

        public CoordinatorConnection Connection { get; private set; } = null!;

        public static async Task<SharingMesh> StartAsync(string mode, int idleSeconds = 60)
        {
            var mesh = new SharingMesh { scratch = Path.Combine(Path.GetTempPath(), "inferhub-sharing-" + Guid.NewGuid().ToString("N")) };
            await mesh.StartCoordinatorAsync();
            await mesh.StartNodeAsync(mode, idleSeconds);
            return mesh;
        }

        public async Task<NodeSharingState> WaitForSharingAsync(Func<NodeSharingState, bool> predicate)
        {
            for (var i = 0; i < 400; i++)
            {
                if (Registry.Snapshot(DateTimeOffset.UtcNow).FirstOrDefault(n => n.NodeId == NodeId)?.Sharing is { } state && predicate(state))
                {
                    return state;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException("No sharing report matching the predicate arrived at the hub.");
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
                Microsoft.Extensions.Options.Options.Create(new RouterOptions()), new NoAffinityStore(), TimeProvider.System));
            builder.Services.AddSingleton<INodeConnectionTracker, NodeConnectionTracker>();
            builder.Services.AddSingleton<Metrics>();
            builder.Services.AddSingleton<ThroughputTracker>();
            builder.Services.Configure<DispatcherOptions>(_ => { });
            builder.Services.Configure<RouterOptions>(_ => { });
            builder.Services.AddSingleton<InferHub.Coordinator.Services.Dispatcher>();
            builder.Services.AddSingleton<IDispatcher>(sp => sp.GetRequiredService<InferHub.Coordinator.Services.Dispatcher>());
            builder.Services.AddSingleton<InferHub.Coordinator.Vector.CollectionOwnership>();
            builder.Services.AddSingleton<InferHub.Coordinator.Vector.NodeCorpusRegistry>();
            builder.Services.AddSingleton<NodeProfileCoordinator>();
            builder.Services.AddSingleton<InferHub.Coordinator.Cluster.IClusterMembership,
                InferHub.Coordinator.Cluster.SingleCoordinatorMembership>();

            app = builder.Build();
            app.MapHub<NodeHub>("/hubs/node");
            await app.StartAsync();
        }

        private async Task StartNodeAsync(string mode, int idleSeconds)
        {
            var backend = new IdleBackend();
            var toolOptions = Microsoft.Extensions.Options.Options.Create(new ToolOptions());
            var runtime = new NoToolRuntime();
            var replicas = new ReplicaStore(
                Microsoft.Extensions.Options.Options.Create(new VectorReplicaOptions { ReplicaDirectory = Path.Combine(scratch, "replicas") }),
                NullLogger<ReplicaStore>.Instance);

            var options = new NodeOptions { Name = NodeId, Sharing = new SharingOptions { Mode = mode, IdleSeconds = idleSeconds } };
            Options = new Monitor(options);
            var nodeOptions = Microsoft.Extensions.Options.Options.Create(options);
            Sharing = new NodeSharing(Options, Activity, NullLogger<NodeSharing>.Instance);

            Connection = new CoordinatorConnection(
                Microsoft.Extensions.Options.Options.Create(new CoordinatorOptions
                {
                    Url = app.Urls.First(),
                    EnrollmentSecret = Secret,
                    HeartbeatInterval = TimeSpan.FromSeconds(30),
                    ModelRefreshInterval = TimeSpan.FromSeconds(30)
                }),
                nodeOptions,
                new FixedIdentity(NodeId),
                backend,
                new InferenceExecutor(backend, replicas, TestProfiles.IdleRetrieval(), NullLogger<InferenceExecutor>.Instance, Activity),
                new ModelCommandExecutor(backend, NullLogger<ModelCommandExecutor>.Instance),
                new ToolExecutor(runtime, toolOptions, NullLogger<ToolExecutor>.Instance),
                runtime,
                toolOptions,
                new NodeProfileApplier(nodeOptions, toolOptions, backend, runtime, TestProfiles.IdleRetrieval(), NullLogger<NodeProfileApplier>.Instance),
                TestProfiles.IdleRetrieval(),
                replicas,
                new NoBackendSupervisor(),
                InferHub.Node.Resources.NoResourceGovernor.Instance,
                NullLogger<CoordinatorConnection>.Instance,
                sharing: Sharing);

            await Connection.StartAsync(CancellationToken.None);

            for (var i = 0; i < 200 && Registry.FindConnectionIdByNodeId(NodeId) is null; i++)
            {
                await Task.Delay(25);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Connection.StopAsync(CancellationToken.None);
            }
            catch (Exception)
            {
            }

            Sharing.Dispose();
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

        public sealed class Monitor(NodeOptions value) : IOptionsMonitor<NodeOptions>
        {
            private Action<NodeOptions, string?>? listener;

            public NodeOptions CurrentValue { get; private set; } = value;

            public NodeOptions Get(string? name) => CurrentValue;

            public IDisposable? OnChange(Action<NodeOptions, string?> listener)
            {
                this.listener = listener;
                return null;
            }

            public void Set(NodeOptions options)
            {
                CurrentValue = options;
                listener?.Invoke(options, null);
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

        private sealed class IdleBackend : IInferenceBackend
        {
            public string Name => "test";

            public string Endpoint => "http://127.0.0.1:0/";

            public IReadOnlyList<string> Kinds { get; } = [CapabilityKinds.Chat];

            public bool SupportsModelManagement => false;

            public Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ModelInfo>?>([new ModelInfo("llama3", "sha256:a", 1)]);

            public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

            public IAsyncEnumerable<string> StreamAsync(string kind, string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

            public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task DeleteAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task WarmAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();
        }
    }
}
