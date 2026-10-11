using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
using InferHub.Node.Profiles;
using InferHub.Node.Resources.Limits;
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
/// Phase 102 across a real wire: a real hub, a real <see cref="CoordinatorConnection"/>, the node's real
/// clamp and limiter. The hub sees the node's ceiling, may set lower limits of its own, and cannot raise
/// the node's — not through its own API, and not by writing the profile around it.
/// </summary>
public class EngineLimitMeshTests
{
    [Fact]
    public async Task TheHubSeesTheNodesCeilingAndItsOwnLowerLimitIsInForce()
    {
        await using var mesh = await LimitsMesh.StartAsync();

        // The ceiling arrives at registration, before the hub has asked for anything.
        var first = await mesh.WaitForGroupAsync(g => g.Node?.CpuPercent == 50);
        Assert.Equal(new EngineLimits(CpuPercent: 50, MemoryMiB: 8192), first.Node);
        Assert.Null(first.Hub);

        var set = await mesh.Control.SetAsync(mesh.Node, "llamacpp", new EngineLimits(CpuPercent: 30, VramMiB: 4000), "admin", CancellationToken.None);
        Assert.Null(set.Refusal);

        var narrowed = await mesh.WaitForGroupAsync(g => g.Hub?.CpuPercent == 30);
        Assert.Equal(new EngineLimits(CpuPercent: 30, MemoryMiB: 8192, VramMiB: 4000), narrowed.Effective);
        Assert.Equal(new EngineLimits(CpuPercent: 50, MemoryMiB: 8192), narrowed.Node);

        // Taking the hub's back leaves the node's own, untouched.
        var cleared = await mesh.Control.SetAsync(mesh.Node, "llamacpp", null, "admin", CancellationToken.None);
        Assert.Null(cleared.Refusal);
        var back = await mesh.WaitForGroupAsync(g => g.Hub is null && g.Effective.VramMiB is null);
        Assert.Equal(new EngineLimits(CpuPercent: 50, MemoryMiB: 8192), back.Effective);
    }

    [Fact]
    public async Task TheHubCannotRaiseTheNodesCeilingThroughItsApiOrAroundIt()
    {
        await using var mesh = await LimitsMesh.StartAsync();
        await mesh.WaitForGroupAsync(g => g.Node?.CpuPercent == 50);

        // Its own API refuses first, with the node's number…
        var refused = await mesh.Control.SetAsync(mesh.Node, "llamacpp", new EngineLimits(CpuPercent: 90), "admin", CancellationToken.None);
        Assert.Equal(NodeLimitsControl.AboveCeiling, refused.Refusal);
        Assert.Contains("50%", refused.Error);

        // …and a profile written around it is refused by the node (43 D1): the node's 50 stays.
        mesh.Profiles.Put("raw", new NodeProfile("raw", 0, new NodeProfileSelector(NodeId: LimitsMesh.NodeId),
            Limits: new Dictionary<string, EngineLimits> { ["llamacpp"] = new(CpuPercent: 90, Cores: 2) }));
        await mesh.Coordinator.ReassertAsync(CancellationToken.None);

        var group = await mesh.WaitForGroupAsync(g => g.Hub?.Cores == 2);
        Assert.Null(group.Hub!.CpuPercent);
        Assert.Equal(50, group.Effective.CpuPercent);
        Assert.Equal(2, group.Effective.Cores);

        var state = await mesh.WaitForProfileStateAsync(s => s.Refusals.Count > 0);
        Assert.Contains(state.Refusals, r => r.Item == "limits:llamacpp:cpuPercent" && r.Reason.Contains("never raise it to 90%"));
    }

    [Fact]
    public async Task TheStatusDocumentCarriesTheLimits()
    {
        await using var mesh = await LimitsMesh.StartAsync();
        await mesh.WaitForGroupAsync(g => g.Node?.CpuPercent == 50);

        var state = mesh.Limits.Of(LimitsMesh.NodeId)!;
        var json = System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"group\":\"llamacpp\"", json);
        Assert.Contains("\"node\":{\"cpuPercent\":50", json);
        Assert.Contains("\"enforcement\":{", json);
    }

    private sealed class LimitsMesh : IAsyncDisposable
    {
        public const string NodeId = "limits-node";
        private const string Secret = "limits-mesh-secret";

        private WebApplication app = null!;
        private CoordinatorConnection node = null!;
        private EngineLimiter limiter = null!;
        private string scratch = null!;

        public NodeRegistry Registry { get; } = new();

        public NodeLimitsRegistry Limits { get; } = new();

        public IProfileRegistry Profiles { get; } = new ProfileRegistry(new NoProfileStore(), NullLogger<ProfileRegistry>.Instance);

        public NodeLimitsControl Control { get; private set; } = null!;

        public NodeProfileCoordinator Coordinator { get; private set; } = null!;

        public NodeSnapshot Node => Registry.Snapshot(DateTimeOffset.UtcNow).Single(n => n.NodeId == NodeId);

        public static async Task<LimitsMesh> StartAsync()
        {
            var mesh = new LimitsMesh { scratch = Path.Combine(Path.GetTempPath(), "inferhub-limits-" + Guid.NewGuid().ToString("N")) };
            await mesh.StartCoordinatorAsync();
            await mesh.StartNodeAsync();
            return mesh;
        }

        public async Task<EngineLimitsGroupState> WaitForGroupAsync(Func<EngineLimitsGroupState, bool> predicate)
        {
            for (var i = 0; i < 400; i++)
            {
                if (Limits.Of(NodeId)?.Groups.FirstOrDefault(g => g.Group == "llamacpp") is { } group && predicate(group))
                {
                    return group;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException($"No limits report matching the predicate arrived; the last was {System.Text.Json.JsonSerializer.Serialize(Limits.Of(NodeId))}.");
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
            builder.Services.AddSingleton(Profiles);
            builder.Services.AddSingleton(Limits);
            builder.Services.AddSingleton<NodeLimitsControl>();
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
            builder.Services.AddSingleton<InferHub.Coordinator.Cluster.IClusterMembership,
                InferHub.Coordinator.Cluster.SingleCoordinatorMembership>();

            app = builder.Build();
            app.MapHub<NodeHub>("/hubs/node");

            await app.StartAsync();
            Control = app.Services.GetRequiredService<NodeLimitsControl>();
            Coordinator = app.Services.GetRequiredService<NodeProfileCoordinator>();
        }

        private async Task StartNodeAsync()
        {
            var backend = new IdleBackend();
            var toolOptions = Options.Create(new ToolOptions());
            var runtime = new NoToolRuntime();
            var replicas = new ReplicaStore(
                Options.Create(new VectorReplicaOptions { ReplicaDirectory = Path.Combine(scratch, "replicas") }),
                NullLogger<ReplicaStore>.Instance);

            var options = new NodeOptions { Name = NodeId };
            options.ResourceLimits.Engines["llamacpp"] = new EngineLimitOptions { CpuPercent = 50, MemoryMiB = 8192 };
            var nodeOptions = Options.Create(options);

            limiter = new EngineLimiter(new Monitor(options), TimeProvider.System, NullLogger<EngineLimiter>.Instance);

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
                new ToolExecutor(runtime, toolOptions, NullLogger<ToolExecutor>.Instance),
                runtime,
                toolOptions,
                new NodeProfileApplier(nodeOptions, toolOptions, backend, runtime, TestProfiles.IdleRetrieval(), NullLogger<NodeProfileApplier>.Instance, limiter: limiter),
                TestProfiles.IdleRetrieval(),
                replicas,
                new NoBackendSupervisor(),
                InferHub.Node.Resources.NoResourceGovernor.Instance,
                NullLogger<CoordinatorConnection>.Instance,
                limiter: limiter);

            await node.StartAsync(CancellationToken.None);

            for (var i = 0; i < 200 && Registry.FindConnectionIdByNodeId(NodeId) is null; i++)
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

            limiter.Dispose();
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

        private sealed class Monitor(NodeOptions value) : IOptionsMonitor<NodeOptions>
        {
            public NodeOptions CurrentValue => value;

            public NodeOptions Get(string? name) => value;

            public IDisposable? OnChange(Action<NodeOptions, string?> listener) => null;
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
