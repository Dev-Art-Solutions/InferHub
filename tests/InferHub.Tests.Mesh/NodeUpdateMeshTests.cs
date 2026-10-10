using System.Net;
using System.Security.Cryptography;
using System.Text;
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
using InferHub.Node.Update;
using InferHub.Node.Vector;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 101 across a real wire: a real hub, a real <see cref="CoordinatorConnection"/>, and the node's real
/// <see cref="UpdateManager"/> — only the release feed, the download server and the setup itself are stand-ins.
/// The hub hears the node's version, an admin's Update reaches the node, and the node, not the hub, decides.
/// </summary>
public class NodeUpdateMeshTests
{
    [Fact]
    public async Task AnAdminsUpdateReachesTheNodeWhichDownloadsAndStartsTheSetup()
    {
        await using var mesh = await UpdateMesh.StartAsync(new UpdateOptions { Check = true, AllowFromHub = true });

        // Registration carries the version even before any check.
        var first = await mesh.WaitForStateAsync(s => s.Current == NodeVersion.Current);
        Assert.Equal(NodeUpdatePhase.Unknown, first.State);
        Assert.True(first.CanApply);

        var sent = await mesh.Control.SendAsync(UpdateMesh.NodeId, NodeUpdateCommand.KindApply, "admin@console", CancellationToken.None);
        Assert.Null(sent.Refusal);

        // The node checked (it knew of nothing), downloaded, verified, and started the setup.
        var applying = await mesh.WaitForStateAsync(s => s.State == NodeUpdatePhase.Applying);
        Assert.Equal(NodeVersion.Format(mesh.Next), applying.Available);
        await mesh.WaitForAsync(() => mesh.Applier.Launched is not null);
        Assert.EndsWith(GitHubReleaseFeed.SetupName(mesh.Next), mesh.Applier.Launched);
    }

    [Fact]
    public async Task ANodeWhoseOperatorSaidNoRefusesEvenWhenTheHubSendsItAnyway()
    {
        await using var mesh = await UpdateMesh.StartAsync(new UpdateOptions { Check = true, AllowFromHub = false });
        await mesh.WaitForStateAsync(s => s.Current == NodeVersion.Current);

        // The hub refuses first, for the message…
        var refused = await mesh.Control.SendAsync(UpdateMesh.NodeId, NodeUpdateCommand.KindApply, "admin", CancellationToken.None);
        Assert.Equal(NodeUpdateControl.Refused, refused.Refusal);
        Assert.Contains("AllowFromHub off", refused.Message);

        // …and the node refuses for itself when the command arrives by another road (43 D1).
        var connectionId = mesh.Registry.FindConnectionIdByNodeId(UpdateMesh.NodeId)!;
        await mesh.Hub.Clients.Client(connectionId).SendAsync("NodeUpdate", new NodeUpdateCommand(NodeUpdateCommand.KindApply, "someone"));

        var state = await mesh.WaitForStateAsync(s => s.LastError is not null);
        Assert.Contains("AllowFromHub is off", state.LastError);
        Assert.Null(mesh.Applier.Launched);
        Assert.Equal(0, mesh.Server.Downloads);
    }

    [Fact]
    public async Task ACheckFromTheHubReportsTheNewRelease()
    {
        await using var mesh = await UpdateMesh.StartAsync(new UpdateOptions { Check = true });
        await mesh.WaitForStateAsync(s => s.Current == NodeVersion.Current);

        var sent = await mesh.Control.SendAsync(UpdateMesh.NodeId, NodeUpdateCommand.KindCheck, "admin", CancellationToken.None);
        Assert.Null(sent.Refusal);

        var state = await mesh.WaitForStateAsync(s => s.State == NodeUpdatePhase.Available);
        Assert.Equal(NodeVersion.Format(mesh.Next), state.Available);
        Assert.Null(mesh.Applier.Launched);
    }

    private sealed class UpdateMesh : IAsyncDisposable
    {
        public const string NodeId = "update-node";
        private const string Secret = "update-mesh-secret";

        private WebApplication app = null!;
        private CoordinatorConnection node = null!;
        private string scratch = null!;

        public NodeRegistry Registry { get; } = new();

        public NodeUpdateRegistry Updates { get; } = new();

        public NodeUpdateControl Control { get; private set; } = null!;

        public IHubContext<NodeHub> Hub { get; private set; } = null!;

        public RecordingApplier Applier { get; } = new();

        public SetupServer Server { get; } = new();

        public Version Next { get; } = new(NodeVersion.Parsed.Major, NodeVersion.Parsed.Minor + 1, 0);

        public static async Task<UpdateMesh> StartAsync(UpdateOptions options)
        {
            var mesh = new UpdateMesh { scratch = Path.Combine(Path.GetTempPath(), "inferhub-updates-" + Guid.NewGuid().ToString("N")) };
            await mesh.StartCoordinatorAsync();
            await mesh.StartNodeAsync(options);
            return mesh;
        }

        public async Task WaitForAsync(Func<bool> predicate)
        {
            for (var i = 0; i < 400 && !predicate(); i++)
            {
                await Task.Delay(25);
            }

            Assert.True(predicate(), "timed out");
        }

        public async Task<NodeUpdateState> WaitForStateAsync(Func<NodeUpdateState, bool> predicate)
        {
            for (var i = 0; i < 400; i++)
            {
                if (Updates.Of(NodeId) is { } state && predicate(state))
                {
                    return state;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException($"No update report matching the predicate arrived; the last was {Updates.Of(NodeId)}.");
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
            builder.Services.AddSingleton(Updates);
            builder.Services.AddSingleton<NodeUpdateControl>();
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
            Control = app.Services.GetRequiredService<NodeUpdateControl>();
            Hub = app.Services.GetRequiredService<IHubContext<NodeHub>>();
        }

        private async Task StartNodeAsync(UpdateOptions options)
        {
            var backend = new IdleBackend();
            var toolOptions = Options.Create(new ToolOptions());
            var runtime = new NoToolRuntime();
            var replicas = new ReplicaStore(
                Options.Create(new VectorReplicaOptions { ReplicaDirectory = Path.Combine(scratch, "replicas") }),
                NullLogger<ReplicaStore>.Instance);
            var nodeOptions = Options.Create(new NodeOptions { Name = NodeId });

            var release = new UpdateRelease(
                Next,
                "v" + NodeVersion.Format(Next),
                GitHubReleaseFeed.SetupName(Next),
                new Uri($"https://example.invalid/{GitHubReleaseFeed.SetupName(Next)}"),
                new Uri($"https://example.invalid/{GitHubReleaseFeed.SetupName(Next)}.sha256"),
                "https://example.invalid/releases/next");
            Server.Publish(release);

            var updates = new UpdateManager(
                Options.Create(options),
                new FixedFeed(release),
                new UpdateDownloader(new HttpClient(Server)),
                Applier,
                Path.Combine(scratch, "data"),
                () => 0,
                TimeProvider.System,
                NullLogger<UpdateManager>.Instance);

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
                new NodeProfileApplier(nodeOptions, toolOptions, backend, runtime, TestProfiles.IdleRetrieval(), NullLogger<NodeProfileApplier>.Instance),
                TestProfiles.IdleRetrieval(),
                replicas,
                new NoBackendSupervisor(),
                InferHub.Node.Resources.NoResourceGovernor.Instance,
                NullLogger<CoordinatorConnection>.Instance,
                updates: updates);

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

        private sealed class FixedFeed(UpdateRelease release) : IReleaseFeed
        {
            public Task<UpdateRelease?> NewestAboveAsync(Version current, CancellationToken cancellationToken)
                => Task.FromResult<UpdateRelease?>(release.Version > current ? release : null);
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

    internal sealed class RecordingApplier : IUpdateApplier
    {
        public string? Launched { get; private set; }

        public bool CanApply => true;

        public string? WhyNot => null;

        public Task LaunchAsync(string setupPath, string logPath, CancellationToken cancellationToken)
        {
            Launched = setupPath;
            return Task.CompletedTask;
        }
    }

    internal sealed class SetupServer : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> files = new();
        private int downloads;

        public int Downloads => Volatile.Read(ref downloads);

        public void Publish(UpdateRelease release)
        {
            var setup = Encoding.UTF8.GetBytes("setup " + release.Tag);
            files[release.SetupUrl.ToString()] = setup;
            files[release.ChecksumUrl.ToString()] = Encoding.UTF8.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData(setup))}  {release.SetupName}\n");
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref downloads);

            return Task.FromResult(files.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
