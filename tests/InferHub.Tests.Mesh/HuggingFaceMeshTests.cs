using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Colibri;
using InferHub.Node.Backends.HuggingFace;
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
/// Phase 98 across a real wire: the hub sends a Hugging Face link as a model command, the node's
/// store fetches it from a Hub-shaped socket (or converts it), the hub hears the progress and the
/// node's new model, and a chat for it is routed and answered.
/// </summary>
public class HuggingFaceMeshTests
{
    [Fact]
    public async Task ALinkFromTheHubBecomesAColibriModelTheFleetCanChatWith()
    {
        await using var hub = await FakeHuggingFace.StartAsync();
        hub.Repo("allenai/OLMoE-1B-7B-0924",
            ("config.json", "{}"u8.ToArray()),
            ("model-00001-of-00001.safetensors", FakeHuggingFace.Bytes(1000, 1)));
        hub.Repo("bartowski/Tiny-GGUF", ("Tiny-Q4_K_M.gguf", FakeHuggingFace.Bytes(200_000, 2)));

        await using var mesh = await HfMesh.StartAsync(hub, "olmoe");
        await mesh.WaitForAsync(() => mesh.Models().SequenceEqual(["olmoe"]));

        var done = await mesh.RunAsync("allenai/OLMoE-1B-7B-0924");
        Assert.Null(done.Error);
        Assert.Equal("success", done.Status);

        // The node reports its models when a pull finishes (96 D3): the converted model is routable.
        await mesh.WaitForAsync(() => mesh.Models().SequenceEqual(["olmoe", "olmoe-1b-7b-0924"]));

        var routed = mesh.Router.Route("olmoe-1b-7b-0924", capability: CapabilityKinds.Chat);
        Assert.NotNull(routed);
        var result = await mesh.Dispatcher.DispatchAsync(
            routed!,
            new InferenceJob(Guid.NewGuid(), "chat", FakeColibri.Chat("olmoe-1b-7b-0924")),
            CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Equal(1, mesh.Launcher.Chats("olmoe-1b-7b-0924"));

        // A GGUF from the same hub lands in the router's directory, verified.
        var gguf = await mesh.RunAsync("bartowski/Tiny-GGUF");
        Assert.Null(gguf.Error);
        Assert.True(File.Exists(Path.Combine(mesh.GgufDirectory, "Tiny-Q4_K_M", "Tiny-Q4_K_M.gguf")));

        // A link the node cannot serve ends in one terminal frame with the node's sentence.
        var refused = await mesh.RunAsync("someone/missing");
        Assert.Contains("has no 'someone/missing'", refused.Error);
    }

    private sealed class HfMesh : IAsyncDisposable
    {
        public const string NodeId = "hf-node";
        private const string Secret = "colibri-mesh-secret";

        private WebApplication app = null!;
        private CoordinatorConnection node = null!;
        private ColibriCatalog catalog = null!;
        private string scratch = null!;
        private string models = null!;

        public FakeColibriLauncher Launcher { get; } = new();

        public FakeConverter Converter { get; } = new();

        public FakeHuggingFace Hub { get; private set; } = null!;

        public ModelCommandCoordinator Commands { get; private set; } = null!;

        public string GgufDirectory => Path.Combine(scratch, "gguf");

        public string ColibriDirectory => models;

        public NodeRegistry Registry { get; } = new();

        public ProfileRegistry Profiles { get; } = new(new NoProfileStore(), NullLogger<ProfileRegistry>.Instance);

        public NodeColibriRegistry Colibri { get; } = new();

        public NodeProfileCoordinator Coordinator { get; private set; } = null!;

        public NodeColibriToggle Toggle { get; private set; } = null!;

        public IRouter Router { get; private set; } = null!;

        public IDispatcher Dispatcher { get; private set; } = null!;

        public static async Task<HfMesh> StartAsync(FakeHuggingFace hub, params string[] catalogue)
        {
            var mesh = new HfMesh
            {
                scratch = Path.Combine(Path.GetTempPath(), "inferhub-hf-mesh-" + Guid.NewGuid().ToString("N")),
                models = FakeColibri.Catalogue(catalogue),
                Hub = hub
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

        public async Task<NodeColibriState> WaitForStateAsync(Func<NodeColibriState, bool> predicate)
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

        public async Task<ModelCommandProgress> RunAsync(string link)
        {
            var finished = new TaskCompletionSource<ModelCommandProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
            Guid? id = null;

            void Handler(ModelCommandProgress frame)
            {
                if (frame.Done && (id is null || frame.CommandId == id))
                {
                    finished.TrySetResult(frame);
                }
            }

            Commands.ProgressReceived += Handler;

            try
            {
                var started = await Commands.SendAsync(NodeId, ModelCommand.KindPull, link, CancellationToken.None, engine: ModelCommand.EngineHuggingFace);
                Assert.NotNull(started);
                id = started!.CommandId;
                return await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                Commands.ProgressReceived -= Handler;
            }
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
            builder.Services.AddSingleton<ModelCommandCoordinator>();
            builder.Services.AddSingleton<InferHub.Coordinator.Cluster.IClusterMembership,
                InferHub.Coordinator.Cluster.SingleCoordinatorMembership>();

            app = builder.Build();
            app.MapHub<NodeHub>("/hubs/node");

            await app.StartAsync();
            Coordinator = app.Services.GetRequiredService<NodeProfileCoordinator>();
            Toggle = app.Services.GetRequiredService<NodeColibriToggle>();
            Commands = app.Services.GetRequiredService<ModelCommandCoordinator>();
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
                new ModelCommandExecutor(catalog, NullLogger<ModelCommandExecutor>.Instance, huggingFace: new HuggingFaceStore(
                    new HuggingFaceOptions { Enabled = true, Endpoint = Hub.Url },
                    new HuggingFaceTargets(null, GgufDirectory, models),
                    () => Hub.Client(),
                    Converter,
                    restartEngine: null,
                    catalog,
                    NullLogger.Instance)),
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
