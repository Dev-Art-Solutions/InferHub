using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Colibri;
using InferHub.Node.Backends.HuggingFace;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
using InferHub.Node.Fit;
using InferHub.Node.Profiles;
using InferHub.Node.Tools;
using InferHub.Node.Vector;
using InferHub.Shared.Contracts;
using InferHub.Shared.Fit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 120 across a real wire: the console's three calls reach the node down its own connection, the node answers
/// them with its own estimate and its owner's terms, and an install it agrees to arrives back as the pull — with the
/// chosen context written where llama.cpp keeps it.
/// </summary>
public class ManagementMeshTests
{
    private const string Repo = "someone/Tiny-GGUF";

    [Fact]
    public async Task TheHubAsksTheNodeAndTheOwnersAnswerDecides()
    {
        await using var hub = await FakeHuggingFace.StartAsync();
        hub.Repo(Repo, ("Tiny-Q4_K_M.gguf", GgufWriter.Llama(arch: "qwen2", layers: 4, heads: 8, kvHeads: 2, keyLength: 64, context: 32768)));
        hub.Info[Repo] = new { id = Repo, pipeline_tag = "text-generation", gguf = new { total = 500_000_000L, context_length = 32768 } };

        await using var mesh = await ManagementMesh.StartAsync(hub);
        using var http = new HttpClient { BaseAddress = new Uri(mesh.Url) };

        // The terms the node connected with are on the hub's snapshot, for the console to show.
        Assert.Equal(new ManagementTerms(false, null, 20), mesh.Node()!.Management);

        // Off: the node's own sentence, as a 403, and nothing installs.
        var refused = await http.PostAsJsonAsync($"/api/admin/nodes/{ManagementMesh.NodeId}/fit", new { link = Repo, context = 8192 });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("AllowFromHub", await refused.Content.ReadAsStringAsync());

        // The owner says yes — read live, no reconnect.
        mesh.Options.Management.AllowFromHub = true;

        var estimate = await http.PostAsJsonAsync($"/api/admin/nodes/{ManagementMesh.NodeId}/fit", new { link = Repo, context = 8192 });
        Assert.Equal(HttpStatusCode.OK, estimate.StatusCode);
        using (var json = JsonDocument.Parse(await estimate.Content.ReadAsStringAsync()))
        {
            var llama = json.RootElement.GetProperty("options").EnumerateArray().First(o => o.GetProperty("engine").GetString() == FitEngines.LlamaCpp);
            Assert.True(llama.GetProperty("available").GetBoolean());
            Assert.True(json.RootElement.GetProperty("hub").EnumerateArray().First(h => h.GetProperty("engine").GetString() == FitEngines.LlamaCpp).GetProperty("allowed").GetBoolean());
        }

        // A link that is not one: the node's 400, not a hub's guess.
        var bad = await http.PostAsJsonAsync($"/api/admin/nodes/{ManagementMesh.NodeId}/fit", new { link = "!!", context = 8192 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // The install: the node agrees, the hub sends the pull, the store lands it with the context.
        var finished = mesh.NextDone();
        var install = await http.PostAsJsonAsync($"/api/admin/nodes/{ManagementMesh.NodeId}/fit/install", new { link = Repo, context = 16384, engine = "llamacpp", variant = "Q4_K_M" });
        Assert.Equal(HttpStatusCode.Accepted, install.StatusCode);
        using (var json = JsonDocument.Parse(await install.Content.ReadAsStringAsync()))
        {
            Assert.Equal(16384, json.RootElement.GetProperty("context").GetInt32());
            Assert.Equal(ModelCommand.EngineHuggingFace, json.RootElement.GetProperty("engine").GetString());
        }

        var done = await finished.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(done.Error);
        var landed = Path.Combine(mesh.GgufDirectory, "Tiny-Q4_K_M");
        Assert.True(File.Exists(Path.Combine(landed, "Tiny-Q4_K_M.gguf")));
        Assert.Equal(16384, HuggingFaceStore.ContextOf(landed));

        // The owner's floor: more free disk than there is — the node refuses as over its terms, 409.
        mesh.Options.Management.KeepFreeGiB = 1_000_000;
        var over = await http.PostAsJsonAsync($"/api/admin/nodes/{ManagementMesh.NodeId}/fit/install", new { link = Repo, context = 8192, engine = "llamacpp", variant = "Q4_K_M" });
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Contains("KeepFreeGiB", await over.Content.ReadAsStringAsync());

        // A node the hub does not have.
        var missing = await http.GetAsync("/api/admin/nodes/nobody/fit/catalog");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private sealed class ManagementMesh : IAsyncDisposable
    {
        public const string NodeId = "managed-node";
        private const string Secret = "management-mesh-secret";

        private WebApplication app = null!;
        private CoordinatorConnection node = null!;
        private ColibriCatalog catalog = null!;
        private string scratch = null!;
        private string models = null!;
        private ModelCommandCoordinator commands = null!;

        public NodeOptions Options { get; } = new() { Name = NodeId };

        public FakeHuggingFace Hub { get; private set; } = null!;

        public string Url => app.Urls.First();

        public string GgufDirectory => Path.Combine(scratch, "gguf");

        public NodeRegistry Registry { get; } = new();

        public static async Task<ManagementMesh> StartAsync(FakeHuggingFace hub)
        {
            var mesh = new ManagementMesh
            {
                scratch = Path.Combine(Path.GetTempPath(), "inferhub-mgmt-mesh-" + Guid.NewGuid().ToString("N")),
                models = FakeColibri.Catalogue("olmoe"),
                Hub = hub
            };

            Directory.CreateDirectory(mesh.GgufDirectory);
            await mesh.StartCoordinatorAsync();
            await mesh.StartNodeAsync();
            return mesh;
        }

        public NodeSnapshot? Node() => Registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.NodeId, NodeId, StringComparison.OrdinalIgnoreCase));

        /// <summary>The next model command that finishes.</summary>
        public Task<ModelCommandProgress> NextDone()
        {
            var finished = new TaskCompletionSource<ModelCommandProgress>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler(ModelCommandProgress frame)
            {
                if (frame.Done && finished.TrySetResult(frame))
                {
                    commands.ProgressReceived -= Handler;
                }
            }

            commands.ProgressReceived += Handler;
            return finished.Task;
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
            builder.Services.AddSingleton(new NodeColibriRegistry());
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
            builder.Services.AddSingleton<IToolDispatcher>(sp => sp.GetRequiredService<InferHub.Coordinator.Services.Dispatcher>());
            builder.Services.AddSingleton<InferHub.Coordinator.Vector.CollectionOwnership>();
            builder.Services.AddSingleton<InferHub.Coordinator.Vector.NodeCorpusRegistry>();
            builder.Services.AddSingleton<NodeProfileCoordinator>();
            builder.Services.AddSingleton<NodeColibriToggle>();
            builder.Services.AddSingleton<ModelCommandCoordinator>();
            builder.Services.AddSingleton<NodeManagementControl>();
            builder.Services.AddSingleton<InferHub.Coordinator.Cluster.IClusterMembership,
                InferHub.Coordinator.Cluster.SingleCoordinatorMembership>();

            app = builder.Build();
            app.MapHub<NodeHub>("/hubs/node");
            app.MapGroup("/api/admin").MapNodeManagement(_ => "test");

            await app.StartAsync();
            commands = app.Services.GetRequiredService<ModelCommandCoordinator>();
        }

        private async Task StartNodeAsync()
        {
            catalog = FakeColibri.Catalog(
                new ColibriOptions { Serve = new ColibriServeOptions { ModelsDir = models, LoadTimeout = TimeSpan.FromSeconds(10) } },
                new FakeColibriLauncher());
            catalog.Start();

            var toolOptions = Microsoft.Extensions.Options.Options.Create(new ToolOptions());
            var runtime = new NoToolRuntime();
            var replicas = new ReplicaStore(
                Microsoft.Extensions.Options.Options.Create(new VectorReplicaOptions { ReplicaDirectory = Path.Combine(scratch, "replicas") }),
                NullLogger<ReplicaStore>.Instance);
            var nodeOptions = Microsoft.Extensions.Options.Options.Create(Options);

            // The machine as the estimate sees it: a llama.cpp router that installs into the store's directory, a big disk.
            var machine = new MachineFacts([new GpuFacts(0, "card", 24576, 24000)], 65536, 60000, 16,
                [new EngineFacts(FitEngines.LlamaCpp, null, true, null, GgufDirectory, 1 << 20, null, null)], []);
            var fit = new FitService(new ModelFactsReader(() => Hub.Client(), new HttpClient(), new Uri("http://127.0.0.1:9/")), () => machine);
            var management = new HubManagement(
                fit,
                new CatalogService(null, new HttpClient(), new Uri("http://127.0.0.1:9/"), fit.Machine),
                TestOptions.Monitor(Options),
                NullLogger<HubManagement>.Instance);

            node = new CoordinatorConnection(
                Microsoft.Extensions.Options.Options.Create(new CoordinatorOptions
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
                    new FakeConverter(),
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
                colibri: catalog,
                management: management);

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
