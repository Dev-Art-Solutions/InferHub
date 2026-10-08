using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.HuggingFace;
using InferHub.Node.Backends.Strata;
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
/// Phase 99 across a real wire: a real hub, a real <see cref="CoordinatorConnection"/>, and a node whose
/// backend is a Strata catalogue — each loaded config a real socket, each install a scripted
/// <c>setup.py</c>. The hub routes an unloaded Strata model, installs another from a Hugging Face link,
/// and pins it through the shipped <see cref="NodeStrataToggle"/>.
/// </summary>
public class StrataMeshTests
{
    private const string CoderLink = "ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-Coder-GGUF:IQ1_M";

    [Fact]
    public async Task TheHubRoutesAStrataModelInstallsAnotherFromALinkAndSwitchesToIt()
    {
        await using var mesh = await StrataMesh.StartAsync("strata-iq2_xs");

        // Installed configs are routable before any is loaded, and the node offers what it could install.
        await mesh.WaitForAsync(() => mesh.Models().SequenceEqual(["strata-iq2_xs"]));
        var boot = await mesh.WaitForStateAsync(s => s.Models.Count == 1 && s.Installable is { Count: > 0 });
        Assert.Equal(NodeCatalogModel.Unloaded, boot.Models.Single().State);
        Assert.Contains(boot.Installable!, i => i is { Name: "strata-iq2_xs", Installed: true });
        Assert.Contains(boot.Installable!, i => i is { Name: "strata-coder-iq1_m", Installed: false, Link: CoderLink });

        var answered = await mesh.ChatAsync("strata-iq2_xs");
        Assert.True(answered.Success, answered.Error);
        Assert.Equal(1, mesh.Launcher.Chats("strata-iq2_xs"));

        // The link a hub sends for a Strata repo runs Strata's setup on the node, not the GGUF store.
        var done = await mesh.RunAsync(ModelCommand.KindPull, CoderLink);
        Assert.Null(done.Error);
        Assert.Equal("success", done.Status);
        Assert.Contains("--family", mesh.Runner.Runs.Single().ArgumentList);

        await mesh.WaitForAsync(() => mesh.Models().SequenceEqual(["strata-coder-iq1_m", "strata-iq2_xs"]));
        await mesh.WaitForStateAsync(s => s.Installable!.Single(i => i.Name == "strata-coder-iq1_m").Installed);

        // Picking it from the hub is a switch on the one-slot default, and it lands in the profile.
        var load = await mesh.Toggle.SetLoadedAsync(mesh.Node()!, "strata-coder-iq1_m", loaded: true, "test", CancellationToken.None);
        Assert.True(load.Applied, load.Error);
        await mesh.WaitForStateAsync(s =>
            s.Models.Single(m => m.Name == "strata-coder-iq1_m") is { Pinned: true, State: NodeCatalogModel.Loaded }
            && s.Models.Single(m => m.Name == "strata-iq2_xs").State == NodeCatalogModel.Unloaded);
        Assert.Equal(["strata-coder-iq1_m"], mesh.Launcher.Alive.Keys);
        Assert.Equal(["strata-coder-iq1_m"], mesh.Profiles.Get($"node:{StrataMesh.NodeId}")!.Strata!.Loaded);

        // A delete through the store is refused in Strata's words: its sizes share files.
        var delete = await mesh.RunAsync(ModelCommand.KindDelete, "strata-coder-iq1_m");
        Assert.Contains("Strata install", delete.Error);
        Assert.Contains("strata-coder-iq1_m", mesh.Models());
    }

    [Fact]
    public async Task AnInstallTheNodeCannotDoIsAFrameNotASilence()
    {
        await using var mesh = await StrataMesh.StartAsync("strata-iq2_xs");
        await mesh.WaitForStateAsync(s => s.Models.Count == 1);

        var refused = await mesh.RunAsync(ModelCommand.KindPull, "ukisai/Swift-1.5-Qwen3.8-Flash-Next-GSQ-RCO-GGUF:IQ3_S");

        Assert.Equal("error", refused.Status);
        Assert.Contains("IQ2_XS, IQ3_XXS", refused.Error);
        Assert.Empty(mesh.Runner.Runs);

        var colibri = await mesh.Toggle.SetLoadedAsync(mesh.Node()!, "llama3", loaded: true, "test", CancellationToken.None);
        Assert.Equal(CatalogToggleOutcome.UnknownModel, colibri.Refusal);
        Assert.Contains("installed by Strata's setup", colibri.Error);
    }

    private sealed class StrataMesh : IAsyncDisposable
    {
        public const string NodeId = "strata-node";
        private const string Secret = "strata-mesh-secret";

        private WebApplication app = null!;
        private CoordinatorConnection node = null!;
        private StrataCatalog catalog = null!;
        private string scratch = null!;
        private string install = null!;

        public FakeColibriLauncher Launcher { get; } = new();

        public FakeSetupRunner Runner { get; private set; } = null!;

        public NodeRegistry Registry { get; } = new();

        public ProfileRegistry Profiles { get; } = new(new NoProfileStore(), NullLogger<ProfileRegistry>.Instance);

        public NodeStrataRegistry Strata { get; } = new();

        public NodeStrataToggle Toggle { get; private set; } = null!;

        public ModelCommandCoordinator Commands { get; private set; } = null!;

        public IRouter Router { get; private set; } = null!;

        public IDispatcher Dispatcher { get; private set; } = null!;

        public static async Task<StrataMesh> StartAsync(params string[] configs)
        {
            var mesh = new StrataMesh
            {
                scratch = Path.Combine(Path.GetTempPath(), "inferhub-strata-mesh-" + Guid.NewGuid().ToString("N")),
                install = FakeStrata.Install(configs)
            };

            mesh.Runner = new FakeSetupRunner(mesh.install);
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

        public async Task<InferenceResult> ChatAsync(string model)
        {
            var routed = Router.Route(model, capability: CapabilityKinds.Chat);
            Assert.NotNull(routed);
            return await Dispatcher.DispatchAsync(routed!, new InferenceJob(Guid.NewGuid(), "chat", FakeColibri.Chat(model)), CancellationToken.None);
        }

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
                if (Strata.Of(NodeId) is { } state && predicate(state))
                {
                    return state;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException("No Strata report matching the predicate arrived.");
        }

        public async Task<ModelCommandProgress> RunAsync(string kind, string model)
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
                var started = await Commands.SendAsync(NodeId, kind, model, CancellationToken.None, engine: ModelCommand.EngineHuggingFace);
                Assert.NotNull(started);
                id = started!.CommandId;
                return await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                Commands.ProgressReceived -= Handler;
            }
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
            builder.Services.AddSingleton(Strata);
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
            builder.Services.AddSingleton<NodeStrataToggle>();
            builder.Services.AddSingleton<ModelCommandCoordinator>();
            builder.Services.AddSingleton<InferHub.Coordinator.Cluster.IClusterMembership,
                InferHub.Coordinator.Cluster.SingleCoordinatorMembership>();

            app = builder.Build();
            app.MapHub<NodeHub>("/hubs/node");

            await app.StartAsync();
            Toggle = app.Services.GetRequiredService<NodeStrataToggle>();
            Commands = app.Services.GetRequiredService<ModelCommandCoordinator>();
            Router = app.Services.GetRequiredService<IRouter>();
            Dispatcher = app.Services.GetRequiredService<IDispatcher>();
        }

        private async Task StartNodeAsync()
        {
            var options = FakeStrata.Options(install);
            catalog = FakeStrata.Catalog(options, Launcher);
            catalog.InstallsFromHub = true;
            catalog.Start();

            var hf = new HuggingFaceOptions { Enabled = true };
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
                new ModelCommandExecutor(
                    catalog,
                    NullLogger<ModelCommandExecutor>.Instance,
                    huggingFace: new HuggingFaceStore(
                        hf,
                        new HuggingFaceTargets(null, null, null, Strata: true),
                        () => throw new InvalidOperationException("a Strata install never reaches the GGUF store"),
                        converter: null,
                        restartEngine: null,
                        colibri: null,
                        NullLogger.Instance),
                    strata: FakeStrata.Installer(catalog, options, Runner, hf)),
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
                    strata: catalog),
                TestProfiles.IdleRetrieval(),
                replicas,
                new NoBackendSupervisor(),
                InferHub.Node.Resources.NoResourceGovernor.Instance,
                NullLogger<CoordinatorConnection>.Instance,
                strata: catalog);

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

            foreach (var dir in new[] { scratch, install })
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
