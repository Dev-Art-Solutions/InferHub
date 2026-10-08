using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Colibri;
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
/// Phase 97 across a real wire: a real hub, a real <see cref="CoordinatorConnection"/>, and a node
/// whose backend is a colibri catalogue of two models, each loaded as a real socket. The hub routes
/// a model nothing has loaded, pins and switches models through the shipped
/// <see cref="NodeColibriToggle"/>, and the node's own clamp decides.
/// </summary>
public class ColibriCatalogMeshTests
{
    [Fact]
    public async Task TheHubRoutesAnUnloadedModelPicksWhichStaysLoadedAndSwitchesOnDemand()
    {
        await using var mesh = await ColibriMesh.StartAsync("olmoe", "qwen-moe");

        // Both models are routable before either is loaded: the listing is the catalogue.
        await mesh.WaitForAsync(() => mesh.Models().SequenceEqual(["olmoe", "qwen-moe"]));
        var boot = await mesh.WaitForStateAsync(s => s.Models.Count == 2);
        Assert.All(boot.Models, m => Assert.Equal(NodeCatalogModel.Unloaded, m.State));
        Assert.Empty(mesh.Launcher.Alive);

        // A request for one that is not loaded loads it, through the hub.
        var routed = mesh.Router.Route("qwen-moe", capability: CapabilityKinds.Chat);
        Assert.NotNull(routed);
        var result = await mesh.Dispatcher.DispatchAsync(
            routed!,
            new InferenceJob(Guid.NewGuid(), "chat", FakeColibri.Chat("qwen-moe")),
            CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Equal(1, mesh.Launcher.Chats("qwen-moe"));
        await mesh.WaitForStateAsync(s => s.Models.Single(m => m.Name == "qwen-moe").State == NodeCatalogModel.Loaded);

        // The hub selects olmoe: on a one-slot node that is a switch, and it lands in the profile.
        var load = await mesh.Toggle.SetLoadedAsync(mesh.Node()!, "OLMOE", loaded: true, "test", CancellationToken.None);
        Assert.True(load.Applied, load.Error);
        var switched = await mesh.WaitForStateAsync(s =>
            s.Models.Single(m => m.Name == "olmoe") is { Pinned: true, State: NodeCatalogModel.Loaded }
            && s.Models.Single(m => m.Name == "qwen-moe").State == NodeCatalogModel.Unloaded);
        Assert.Equal(["olmoe"], mesh.Launcher.Alive.Keys);
        Assert.Equal(["olmoe"], mesh.Profiles.Get($"node:{ColibriMesh.NodeId}")!.Colibri!.Loaded);

        var onDemand = await mesh.Toggle.SetOnDemandAsync(mesh.Node()!, onDemand: true, "test", CancellationToken.None);
        Assert.True(onDemand.Applied, onDemand.Error);
        await mesh.WaitForStateAsync(s => s.OnDemand);

        // Unloading from the hub unpins and stops it: the RAM comes back.
        var unload = await mesh.Toggle.SetLoadedAsync(mesh.Node()!, "olmoe", loaded: false, "test", CancellationToken.None);
        Assert.True(unload.Applied, unload.Error);
        await mesh.WaitForStateAsync(s => s.Models.All(m => m.State == NodeCatalogModel.Unloaded && !m.Pinned));
        Assert.Empty(mesh.Launcher.Alive);
        Assert.Equal(["olmoe", "qwen-moe"], mesh.Models());
    }

    [Fact]
    public async Task TheHubCannotPinAModelTheNodeDoesNotHave()
    {
        await using var mesh = await ColibriMesh.StartAsync("olmoe");
        await mesh.WaitForStateAsync(s => s.Models.Count == 1);

        // The hub's own check, for the better message…
        var refused = await mesh.Toggle.SetLoadedAsync(mesh.Node()!, "../../etc/passwd", loaded: true, "test", CancellationToken.None);
        Assert.Equal(CatalogToggleOutcome.UnknownModel, refused.Refusal);
        Assert.Contains("it has olmoe", refused.Error);

        // …and the node's, which holds when the profile arrives by another road.
        mesh.Profiles.Put("raw", new NodeProfile(
            "raw", 0, new NodeProfileSelector(NodeId: ColibriMesh.NodeId),
            Colibri: new CatalogProfile(["../../etc/passwd"])));
        await mesh.Coordinator.ReassertAsync(CancellationToken.None);

        var state = await mesh.WaitForProfileStateAsync(s => s.ProfileName == "raw");
        var refusal = Assert.Single(state.Refusals);
        Assert.Contains("has no '../../etc/passwd'", refusal.Reason);
        Assert.Empty(mesh.Launcher.Alive);
    }

    private sealed class ColibriMesh : IAsyncDisposable
    {
        public const string NodeId = "colibri-node";
        private const string Secret = "colibri-mesh-secret";

        private WebApplication app = null!;
        private CoordinatorConnection node = null!;
        private ColibriCatalog catalog = null!;
        private string scratch = null!;
        private string models = null!;

        public FakeColibriLauncher Launcher { get; } = new();

        public NodeRegistry Registry { get; } = new();

        public ProfileRegistry Profiles { get; } = new(new NoProfileStore(), NullLogger<ProfileRegistry>.Instance);

        public NodeColibriRegistry Colibri { get; } = new();

        public NodeProfileCoordinator Coordinator { get; private set; } = null!;

        public NodeColibriToggle Toggle { get; private set; } = null!;

        public IRouter Router { get; private set; } = null!;

        public IDispatcher Dispatcher { get; private set; } = null!;

        public static async Task<ColibriMesh> StartAsync(params string[] catalogue)
        {
            var mesh = new ColibriMesh
            {
                scratch = Path.Combine(Path.GetTempPath(), "inferhub-colibri-mesh-" + Guid.NewGuid().ToString("N")),
                models = FakeColibri.Catalogue(catalogue)
            };

            await mesh.StartCoordinatorAsync();
            await mesh.StartNodeAsync();
            return mesh;
        }

        public NodeSnapshot? Node() => Registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.NodeId, NodeId, StringComparison.OrdinalIgnoreCase));

        public string[] Models() => (Node()?.Capabilities ?? [])
            .Where(c => c.Kind == CapabilityKinds.Chat)
            .SelectMany(c => c.Models)
            .Order(StringComparer.Ordinal)
            .ToArray();

        public async Task WaitForAsync(Func<bool> predicate)
        {
            for (var i = 0; i < 400 && !predicate(); i++)
            {
                await Task.Delay(25);
            }

            Assert.True(predicate(), $"timed out; the hub has {string.Join(", ", Models())}");
        }

        public async Task<NodeCatalogState> WaitForStateAsync(Func<NodeCatalogState, bool> predicate)
        {
            for (var i = 0; i < 400; i++)
            {
                if (Colibri.Of(NodeId) is { } state && predicate(state))
                {
                    return state;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException("No colibri report matching the predicate arrived.");
        }

        public async Task<NodeProfileState> WaitForProfileStateAsync(Func<NodeProfileState, bool> predicate)
        {
            for (var i = 0; i < 400; i++)
            {
                if (Profiles.StateOf(NodeId) is { } state && predicate(state))
                {
                    return state;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException("No profile state matching the predicate arrived.");
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
            builder.Services.AddSingleton<IProfileRegistry>(Profiles);
            builder.Services.AddSingleton(Colibri);
            builder.Services.AddSingleton<IAuditLog, AuditLog>();
            builder.Services.AddSingleton<IRouter, InferHub.Coordinator.Services.Router>();
            builder.Services.AddSingleton<IConversationAffinity>(new ConversationAffinity(
                Options.Create(new RouterOptions()), new NoAffinityStore(), TimeProvider.System));
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
            builder.Services.AddSingleton<NodeColibriToggle>();
            builder.Services.AddSingleton<InferHub.Coordinator.Cluster.IClusterMembership,
                InferHub.Coordinator.Cluster.SingleCoordinatorMembership>();

            app = builder.Build();
            app.MapHub<NodeHub>("/hubs/node");

            await app.StartAsync();
            Coordinator = app.Services.GetRequiredService<NodeProfileCoordinator>();
            Toggle = app.Services.GetRequiredService<NodeColibriToggle>();
            Router = app.Services.GetRequiredService<IRouter>();
            Dispatcher = app.Services.GetRequiredService<IDispatcher>();
        }

        private async Task StartNodeAsync()
        {
            catalog = FakeColibri.Catalog(
                new ColibriOptions { Serve = new ColibriServeOptions { ModelsDir = models, LoadTimeout = TimeSpan.FromSeconds(10) } },
                Launcher);
            catalog.Start();

            var toolOptions = Options.Create(new ToolOptions());
            var runtime = new NoToolRuntime();
            var replicas = new ReplicaStore(
                Options.Create(new VectorReplicaOptions { ReplicaDirectory = Path.Combine(scratch, "replicas") }),
                NullLogger<ReplicaStore>.Instance);
            var nodeOptions = Options.Create(new NodeOptions { Name = NodeId });

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
                catalog,
                new InferenceExecutor(catalog, replicas, TestProfiles.IdleRetrieval(), NullLogger<InferenceExecutor>.Instance),
                new ModelCommandExecutor(catalog, NullLogger<ModelCommandExecutor>.Instance),
                new ToolExecutor(runtime, toolOptions, NullLogger<ToolExecutor>.Instance),
                runtime,
                toolOptions,
                new NodeProfileApplier(
                    nodeOptions,
                    toolOptions,
                    catalog,
                    runtime,
                    TestProfiles.IdleRetrieval(),
                    NullLogger<NodeProfileApplier>.Instance,
                    colibri: catalog),
                TestProfiles.IdleRetrieval(),
                replicas,
                new NoBackendSupervisor(),
                InferHub.Node.Resources.NoResourceGovernor.Instance,
                NullLogger<CoordinatorConnection>.Instance,
                colibri: catalog);

            await node.StartAsync(CancellationToken.None);

            for (var i = 0; i < 200 && Node() is null; i++)
            {
                await Task.Delay(25);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await node.StopAsync(CancellationToken.None);
            }
            catch (Exception)
            {
            }

            await catalog.StopAsync(CancellationToken.None);
            await app.StopAsync();
            await app.DisposeAsync();

            foreach (var dir in new[] { scratch, models })
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (DirectoryNotFoundException)
                {
                }
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
}
