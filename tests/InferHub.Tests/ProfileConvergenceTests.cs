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
/// Profiles across a real wire (phase 43): a real <see cref="NodeHub"/>, a real
/// <see cref="CoordinatorConnection"/>, a real <see cref="ProcessToolRuntime"/> with a real child
/// process, and a real <see cref="NodeProfileClamp"/> running on the node side of the connection.
/// </summary>
/// <remarks>
/// The mesh shape is <c>ToolMeshTests</c>'s, for the reason recorded there: the pull at
/// registration, the push on write and the state report are three hub methods and three client
/// handler registrations, and a suite that stubbed either end would be testing the names a test
/// author remembered rather than the ones that ship.
/// </remarks>
public class ProfileConvergenceTests
{
    [Fact]
    public async Task ANodeAsksForItsProfileAtRegistrationAndConverges()
    {
        await using var mesh = await ProfileMesh.StartAsync(profile: TestProfiles.Profile(
            name: "gpu-boxes",
            selector: new NodeProfileSelector(Labels: new Dictionary<string, string> { ["tier"] = "gpu" }),
            tools: new Dictionary<string, bool> { ["echo"] = false }));

        var state = await mesh.WaitForStateAsync(state => state.ProfileName == "gpu-boxes");

        Assert.Equal("applied", state.Status());
        Assert.Contains("tool 'echo' off", state.Applied);
        Assert.Empty(state.Refusals);

        // And the narrowing is real on the node, not just reported: the capability is withdrawn.
        await mesh.WaitForAsync(() => (mesh.Node()?.Capabilities ?? []).All(c => c.Kind != "echo"));
    }

    [Fact]
    public async Task ASelectorThatDoesNotMatchLeavesTheNodeOnItsOwnConfiguration()
    {
        await using var mesh = await ProfileMesh.StartAsync(profile: TestProfiles.Profile(
            name: "cpu-boxes",
            selector: new NodeProfileSelector(Labels: new Dictionary<string, string> { ["tier"] = "cpu" }),
            tools: new Dictionary<string, bool> { ["echo"] = false }));

        var state = await mesh.WaitForStateAsync(_ => true);

        Assert.Null(state.ProfileName);
        Assert.Equal("none", state.Status());
        Assert.Contains(mesh.Node()?.Capabilities ?? [], c => c.Kind == "echo");
    }

    /// <summary>
    /// Written after a reboot, and the node was not there to be told. It asks on the way back in,
    /// which is the whole difference between desired state and a command (D2).
    /// </summary>
    [Fact]
    public async Task AReconnectingNodeConvergesWithoutTheHubTrackingIt()
    {
        await using var mesh = await ProfileMesh.StartAsync();

        await mesh.WaitForStateAsync(state => state.ProfileName is null);

        // The profile is written while this node is away — nothing is pushed to it.
        mesh.Profiles.Put("gpu-boxes", TestProfiles.Profile(
            name: "gpu-boxes",
            selector: new NodeProfileSelector(NodeId: ProfileMesh.NodeId),
            tools: new Dictionary<string, bool> { ["echo"] = false }));

        await mesh.RestartNodeAsync();

        var state = await mesh.WaitForStateAsync(state => state.ProfileName == "gpu-boxes");
        Assert.Equal(1, state.Revision);
        Assert.Contains("tool 'echo' off", state.Applied);
    }

    [Fact]
    public async Task PartialApplicationReportsPerItemRefusalsAndAppliesTheRest()
    {
        await using var mesh = await ProfileMesh.StartAsync();

        await mesh.WriteAsync("mixed", TestProfiles.Profile(
            name: "mixed",
            selector: new NodeProfileSelector(NodeId: ProfileMesh.NodeId),
            capabilities: new Dictionary<string, bool> { ["embed"] = false },
            tools: new Dictionary<string, bool> { ["whisper"] = true }));

        var state = await mesh.WaitForStateAsync(state => state.ProfileName == "mixed");

        Assert.Equal("refused", state.Status());
        Assert.Contains("capability 'embed' off", state.Applied);

        var refusal = Assert.Single(state.Refusals);
        Assert.Equal("tool:whisper", refusal.Item);
        Assert.Contains("Tools:Allowed", refusal.Reason);

        // The one that could not be honoured did not cost the one that could.
        Assert.Contains(mesh.Node()?.Capabilities ?? [], c => c.Kind == "echo");
    }

    [Fact]
    public async Task TwoMatchingProfilesAreAConflictAndTheNodeKeepsWhatItHas()
    {
        await using var mesh = await ProfileMesh.StartAsync();

        await mesh.WriteAsync("first", TestProfiles.Profile(
            name: "first",
            selector: new NodeProfileSelector(NodeId: ProfileMesh.NodeId),
            capabilities: new Dictionary<string, bool> { ["embed"] = false }));

        var applied = await mesh.WaitForStateAsync(state => state.ProfileName == "first");
        Assert.Equal("applied", applied.Status());

        // A second profile that also matches. Neither is sent.
        await mesh.WriteAsync("second", TestProfiles.Profile(
            name: "second",
            selector: new NodeProfileSelector(Labels: new Dictionary<string, string> { ["tier"] = "gpu" }),
            capabilities: new Dictionary<string, bool> { ["chat"] = false }));

        var assignment = mesh.Profiles.MatchFor(ProfileMesh.NodeId, mesh.Labels);
        Assert.True(assignment.IsConflict);
        Assert.Equal(["first", "second"], assignment.Conflicts);

        // The node still reports the profile it applied before the conflict appeared.
        await Task.Delay(200);
        Assert.Equal("first", mesh.Profiles.StateOf(ProfileMesh.NodeId)?.ProfileName);
    }

    [Fact]
    public async Task DeletingAProfileRevertsTheNodeToItsOwnConfiguration()
    {
        await using var mesh = await ProfileMesh.StartAsync();

        await mesh.WriteAsync("gpu-boxes", TestProfiles.Profile(
            name: "gpu-boxes",
            selector: new NodeProfileSelector(NodeId: ProfileMesh.NodeId),
            tools: new Dictionary<string, bool> { ["echo"] = false }));

        await mesh.WaitForStateAsync(state => state.ProfileName == "gpu-boxes");
        await mesh.WaitForAsync(() => (mesh.Node()?.Capabilities ?? []).All(c => c.Kind != "echo"));

        mesh.Profiles.Delete("gpu-boxes");
        await mesh.Coordinator.ReassertAsync(CancellationToken.None);

        await mesh.WaitForStateAsync(state => state.ProfileName is null);
        await mesh.WaitForAsync(() => (mesh.Node()?.Capabilities ?? []).Any(c => c.Kind == "echo"));
    }

    /// <summary>
    /// The cap lands on the registry entry rather than needing a re-registration, which is what the
    /// saturation check reads.
    /// </summary>
    [Fact]
    public async Task LoweringConcurrencyTakesEffectWithoutTheNodeReconnecting()
    {
        await using var mesh = await ProfileMesh.StartAsync();

        await mesh.WriteAsync("slow-down", TestProfiles.Profile(
            name: "slow-down",
            selector: new NodeProfileSelector(NodeId: ProfileMesh.NodeId),
            maxConcurrency: 2));

        await mesh.WaitForStateAsync(state => state.ProfileName == "slow-down");
        await mesh.WaitForAsync(() => mesh.Node()?.MaxConcurrency == 2);
    }

    // ---- persistence ---------------------------------------------------------------------------

    /// <summary>
    /// A profile that evaporates on hub restart is useless for the thing it was asked to do (D3).
    /// </summary>
    [Fact]
    public void ProfilesRoundTripAcrossACoordinatorRestart()
    {
        using var directory = new ToolWorkerFixture.TempDirectory("inferhub-profiles");
        var options = Options.Create(new FleetOptions
        {
            Profiles = new ProfileOptions
            {
                Persistence = ProfileOptions.PersistenceFile,
                DataDirectory = directory.Path
            }
        });

        using (var store = new FileProfileStore(options))
        {
            var registry = new ProfileRegistry(store, NullLogger<ProfileRegistry>.Instance);
            registry.Put("gpu-boxes", TestProfiles.Profile(
                selector: new NodeProfileSelector(Labels: new Dictionary<string, string> { ["tier"] = "gpu" }),
                capabilities: new Dictionary<string, bool> { ["embed"] = false }));

            registry.Put("gpu-boxes", TestProfiles.Profile(
                selector: new NodeProfileSelector(Labels: new Dictionary<string, string> { ["tier"] = "gpu" }),
                capabilities: new Dictionary<string, bool> { ["embed"] = false, ["chat"] = false }));

            registry.Put("doomed", TestProfiles.Profile(selector: new NodeProfileSelector(NodeId: "x")));
            registry.Delete("doomed");
        }

        using var reopened = new FileProfileStore(options);
        var restarted = new ProfileRegistry(reopened, NullLogger<ProfileRegistry>.Instance);

        var profile = Assert.Single(restarted.All());
        Assert.Equal("gpu-boxes", profile.Name);
        Assert.Equal(2, profile.Revision);
        Assert.Equal(2, profile.Capabilities!.Count);

        // A revision must never be reused, including for a name that comes back — a node that had
        // applied revision 1 of the old one must not read a new one as "already applied".
        Assert.Equal(3, restarted.Put("gpu-boxes", profile).Revision);
    }

    [Fact]
    public void ASelectorThatNamesNothingMatchesNothing()
    {
        var registry = new ProfileRegistry(new NoProfileStore(), NullLogger<ProfileRegistry>.Instance);
        registry.Put("empty", TestProfiles.Profile(selector: new NodeProfileSelector()));

        var assignment = registry.MatchFor("node-1", new Dictionary<string, string> { ["tier"] = "gpu" });

        Assert.Null(assignment.Profile);
        Assert.False(assignment.IsConflict);
    }

    [Fact]
    public void EveryLabelInASelectorMustMatch()
    {
        var registry = new ProfileRegistry(new NoProfileStore(), NullLogger<ProfileRegistry>.Instance);
        registry.Put("both", TestProfiles.Profile(selector: new NodeProfileSelector(
            Labels: new Dictionary<string, string> { ["tier"] = "gpu", ["region"] = "eu" })));

        Assert.Null(registry.MatchFor("n", new Dictionary<string, string> { ["tier"] = "gpu" }).Profile);
        Assert.NotNull(registry.MatchFor("n", new Dictionary<string, string>
        {
            ["tier"] = "gpu",
            ["region"] = "eu",
            ["extra"] = "ignored"
        }).Profile);
    }

    // ---- the mesh ------------------------------------------------------------------------------

    private sealed class ProfileMesh : IAsyncDisposable
    {
        public const string NodeId = "profile-node";
        private const string Secret = "profile-mesh-secret";

        private WebApplication app = null!;
        private CoordinatorConnection node = null!;
        private ProcessToolRuntime runtime = null!;
        private ToolWorkerFixture.TempDirectory manifests = null!;
        private ToolWorkerFixture.TempDirectory scratch = null!;
        private ToolOptions toolOptions = null!;

        public NodeRegistry Registry { get; } = new();

        public ProfileRegistry Profiles { get; } = new(new NoProfileStore(), NullLogger<ProfileRegistry>.Instance);

        public NodeProfileCoordinator Coordinator { get; private set; } = null!;

        public IReadOnlyDictionary<string, string> Labels { get; } =
            new Dictionary<string, string> { ["tier"] = "gpu" };

        public static async Task<ProfileMesh> StartAsync(NodeProfile? profile = null)
        {
            var mesh = new ProfileMesh
            {
                manifests = new ToolWorkerFixture.TempDirectory("inferhub-profile-mesh"),
                scratch = new ToolWorkerFixture.TempDirectory()
            };

            mesh.manifests.WriteManifest("echo.json", new
            {
                id = "echo",
                capabilities = new[] { new { kind = "echo", models = new[] { "echo" } } },
                command = ToolWorkerFixture.Command(),
                startTimeoutSeconds = 30,
                requestTimeoutSeconds = 30
            });

            if (profile is not null)
            {
                mesh.Profiles.Put(profile.Name, profile);
            }

            await mesh.StartCoordinatorAsync();
            await mesh.StartNodeAsync();

            return mesh;
        }

        public NodeSnapshot? Node() => Registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.NodeId, NodeId, StringComparison.OrdinalIgnoreCase));

        public async Task WriteAsync(string name, NodeProfile profile)
        {
            Profiles.Put(name, profile);
            await Coordinator.ReassertAsync(CancellationToken.None);
        }

        public async Task<NodeProfileState> WaitForStateAsync(Func<NodeProfileState, bool> predicate)
        {
            for (var i = 0; i < 200; i++)
            {
                if (Profiles.StateOf(NodeId) is { } state && predicate(state))
                {
                    return state;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException(
                $"No profile state matching the predicate arrived. Last seen: {Profiles.StateOf(NodeId)?.ProfileName ?? "(nothing)"}.");
        }

        public async Task WaitForAsync(Func<bool> predicate)
        {
            for (var i = 0; i < 200 && !predicate(); i++)
            {
                await Task.Delay(25);
            }

            Assert.True(predicate());
        }

        /// <summary>Stop and start the node, leaving the coordinator up — a reboot, from the hub's side.</summary>
        public async Task RestartNodeAsync()
        {
            await node.StopAsync(CancellationToken.None);
            await node.DisposeAsync();
            await StartNodeAsync();
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
            // Phase 44: ownership is re-derived on every profile re-assert, so the coordinator needs it
            // (D1). Empty here — these suites assign no corpus, so every collection stays the hub's.
            builder.Services.AddSingleton<InferHub.Coordinator.Vector.CollectionOwnership>();
            builder.Services.AddSingleton<InferHub.Coordinator.Vector.NodeCorpusRegistry>();
            builder.Services.AddSingleton<NodeProfileCoordinator>();
            builder.Services.AddSingleton<InferHub.Coordinator.Cluster.IClusterMembership,
                InferHub.Coordinator.Cluster.SingleCoordinatorMembership>();

            app = builder.Build();
            app.MapHub<NodeHub>("/hubs/node");

            await app.StartAsync();
            Coordinator = app.Services.GetRequiredService<NodeProfileCoordinator>();
        }

        private async Task StartNodeAsync()
        {
            if (runtime is null)
            {
                toolOptions = ToolWorkerFixture.Options(scratch.Path, "echo");
                toolOptions.ManifestDirectory = manifests.Path;

                runtime = new ProcessToolRuntime(
                    ToolWorkerFixture.Wrap(toolOptions),
                    TimeProvider.System,
                    NullLoggerFactory.Instance,
                    NullLogger<ProcessToolRuntime>.Instance);

                await runtime.StartAsync(CancellationToken.None);
            }

            var backend = new EmbedOnlyBackend();
            var replicas = new ReplicaStore(
                Options.Create(new VectorReplicaOptions { ReplicaDirectory = Path.Combine(scratch.Path, "replicas") }),
                NullLogger<ReplicaStore>.Instance);

            var nodeOptions = Options.Create(new NodeOptions
            {
                Name = "profile-node",
                Labels = new Dictionary<string, string>(Labels),
                MaxConcurrency = 8
            });

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
                backend,
                new InferenceExecutor(backend, replicas, TestProfiles.IdleRetrieval(), NullLogger<InferenceExecutor>.Instance),
                new ModelCommandExecutor(backend, NullLogger<ModelCommandExecutor>.Instance),
                new ToolExecutor(runtime, ToolWorkerFixture.Wrap(toolOptions), NullLogger<ToolExecutor>.Instance),
                runtime,
                // A fresh applier per node start: this *is* a reboot, and a node that remembered its
                // last profile across one would prove nothing about convergence.
                new NodeProfileApplier(
                    nodeOptions,
                    ToolWorkerFixture.Wrap(toolOptions),
                    backend,
                    runtime,
                    TestProfiles.IdleRetrieval(),
                    NullLogger<NodeProfileApplier>.Instance),
                TestProfiles.IdleRetrieval(),
                replicas,
                new NoBackendSupervisor(),
                NullLogger<CoordinatorConnection>.Instance);

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

            await runtime.StopAsync(CancellationToken.None);
            await app.StopAsync();
            await app.DisposeAsync();
            manifests.Dispose();
            scratch.Dispose();
        }

        /// <summary>Holds one embedding model, so a capability profile has something to switch off.</summary>
        private sealed class EmbedOnlyBackend : IInferenceBackend
        {
            public string Name => "test";

            public string Endpoint => "http://127.0.0.1:0/";

            public bool SupportsModelManagement => false;

            public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ModelInfo>>([new ModelInfo("nomic-embed-text", "sha256:a", 1)]);

            public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public IAsyncEnumerable<string> StreamAsync(string kind, string requestJson, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task DeleteAsync(string model, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task WarmAsync(string model, CancellationToken cancellationToken)
                => throw new NotSupportedException();
        }

        private sealed class FixedIdentity(string nodeId) : INodeIdentity
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
