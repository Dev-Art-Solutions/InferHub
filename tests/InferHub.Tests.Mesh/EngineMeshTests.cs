using System.Collections.Concurrent;
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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 95 across a real wire: a real hub, a real <see cref="CoordinatorConnection"/>, and a node
/// whose two llama.cpp engines are real <see cref="UpstreamBackend"/>s crossing real sockets to two
/// servers that answer what <c>llama-server</c> answers. The hub starts and stops them through the
/// shipped <see cref="NodeBackendToggle"/>, and the node's own clamp decides.
/// </summary>
public class EngineMeshTests
{
    [Fact]
    public async Task TheHubSeesEveryEngineAndStartsAndStopsThemOneAtATime()
    {
        await using var qwen = await FakeLlamaServer.StartAsync("qwen-gguf");
        await using var phi = await FakeLlamaServer.StartAsync("phi-gguf");
        await using var mesh = await EngineMesh.StartAsync(qwen, phi);

        // Boot: qwen autostarts, phi is listed but manual. The hub sees both — the list is the
        // ceiling, not the running set — and routes only what is running.
        await mesh.WaitForAsync(() => mesh.Models().SequenceEqual(["qwen-gguf"]));
        var boot = await mesh.WaitForEnginesAsync(engines => engines.Count == 2);
        Assert.Equal(NodeEngineInfo.Running, boot.Single(e => e.Name == "qwen").State);
        Assert.Equal(NodeEngineInfo.Stopped, boot.Single(e => e.Name == "phi").State);

        var started = await mesh.Toggle.SetRunningAsync(mesh.Node()!, "phi", running: true, "test", CancellationToken.None);
        Assert.True(started.Applied, started.Error);
        await mesh.WaitForAsync(() => mesh.Models().SequenceEqual(["phi-gguf", "qwen-gguf"]));

        var stopped = await mesh.Toggle.SetRunningAsync(mesh.Node()!, "QWEN", running: false, "test", CancellationToken.None);
        Assert.True(stopped.Applied, stopped.Error);
        Assert.Equal("qwen", stopped.Engine);
        await mesh.WaitForAsync(() => mesh.Models().SequenceEqual(["phi-gguf"]));
        await mesh.WaitForEnginesAsync(engines => engines.Single(e => e.Name == "qwen").State == NodeEngineInfo.Stopped);

        // One profile carries both instructions, written as a node:{id} profile because none matched.
        var profile = mesh.Profiles.Get($"node:{EngineMesh.NodeId}")!;
        Assert.Equal(new Dictionary<string, bool> { ["phi"] = true, ["qwen"] = false }, profile.Backends);

        // And the hub routes the remaining engine's model to this node, which sends it to that engine.
        var routed = mesh.Router.Route("phi-gguf", capability: CapabilityKinds.Chat);
        Assert.NotNull(routed);
        var result = await mesh.Dispatcher.DispatchAsync(
            routed!,
            new InferenceJob(Guid.NewGuid(), "chat", """{"model":"phi-gguf","messages":[{"role":"user","content":"hi"}],"stream":false}"""),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, phi.Chats);
        Assert.Equal(0, qwen.Chats);
        Assert.Null(mesh.Router.Route("qwen-gguf", capability: CapabilityKinds.Chat));
    }

    [Fact]
    public async Task TheHubCannotStartAnEngineTheNodeNeverListed()
    {
        await using var qwen = await FakeLlamaServer.StartAsync("qwen-gguf");
        await using var phi = await FakeLlamaServer.StartAsync("phi-gguf");
        await using var mesh = await EngineMesh.StartAsync(qwen, phi);
        await mesh.WaitForEnginesAsync(engines => engines.Count == 2);

        // The hub's own check, for the better message…
        var refused = await mesh.Toggle.SetRunningAsync(mesh.Node()!, "llama-server -m /etc/shadow", running: true, "test", CancellationToken.None);
        Assert.Equal(BackendToggleOutcome.UnknownEngine, refused.Refusal);
        Assert.Contains("it has phi, qwen", refused.Error);

        // …and the node's, which is the one that holds when the profile arrives by another road.
        mesh.Profiles.Put("raw", new NodeProfile(
            "raw", 0, new NodeProfileSelector(NodeId: EngineMesh.NodeId),
            Backends: new Dictionary<string, bool> { ["llama-server -m /etc/shadow"] = true }));
        await mesh.Coordinator.ReassertAsync(CancellationToken.None);

        var state = await mesh.WaitForProfileStateAsync(s => s.ProfileName == "raw");
        var refusal = Assert.Single(state.Refusals);
        Assert.Contains("Backend:Engines on this node does not name", refusal.Reason);
        Assert.Equal(["qwen-gguf"], mesh.Models());
    }

    private sealed class FakeLlamaServer : IAsyncDisposable
    {
        private WebApplication app = null!;
        private int chats;

        public string Url { get; private set; } = null!;

        public int Chats => Volatile.Read(ref chats);

        public static async Task<FakeLlamaServer> StartAsync(string alias)
        {
            var fake = new FakeLlamaServer();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            fake.app = builder.Build();
            fake.app.MapGet("/health", () => Results.Json(new { status = "ok" }));
            fake.app.MapGet("/v1/models", () => Results.Json(new { @object = "list", data = new[] { new { id = alias, @object = "model", owned_by = "llamacpp" } } }));
            fake.app.MapPost("/v1/chat/completions", () =>
            {
                Interlocked.Increment(ref fake.chats);
                return Results.Json(new
                {
                    id = "chatcmpl-1",
                    @object = "chat.completion",
                    created = 0,
                    model = alias,
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = "hello" }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 3, completion_tokens = 1, total_tokens = 4 }
                });
            });

            await fake.app.StartAsync();
            fake.Url = fake.app.Urls.First();
            return fake;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class EngineMesh : IAsyncDisposable
    {
        public const string NodeId = "engine-node";
        private const string Secret = "engine-mesh-secret";

        private WebApplication app = null!;
        private CoordinatorConnection node = null!;
        private MultiBackend engines = null!;
        private string scratch = null!;

        public NodeRegistry Registry { get; } = new();

        public ProfileRegistry Profiles { get; } = new(new NoProfileStore(), NullLogger<ProfileRegistry>.Instance);

        public NodeBackendRegistry Backends { get; } = new();

        public NodeProfileCoordinator Coordinator { get; private set; } = null!;

        public NodeBackendToggle Toggle { get; private set; } = null!;

        public IRouter Router { get; private set; } = null!;

        public IDispatcher Dispatcher { get; private set; } = null!;

        public static async Task<EngineMesh> StartAsync(FakeLlamaServer qwen, FakeLlamaServer phi)
        {
            var mesh = new EngineMesh { scratch = Path.Combine(Path.GetTempPath(), "inferhub-engines-" + Guid.NewGuid().ToString("N")) };
            await mesh.StartCoordinatorAsync();
            await mesh.StartNodeAsync(qwen, phi);
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

        public async Task<IReadOnlyList<NodeEngineInfo>> WaitForEnginesAsync(Func<IReadOnlyList<NodeEngineInfo>, bool> predicate)
        {
            for (var i = 0; i < 400; i++)
            {
                if (Backends.Of(NodeId) is { } state && predicate(state.Engines))
                {
                    return state.Engines;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException("No engines report matching the predicate arrived.");
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
            builder.Services.AddSingleton(Backends);
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
            builder.Services.AddSingleton<NodeBackendToggle>();
            builder.Services.AddSingleton<InferHub.Coordinator.Cluster.IClusterMembership,
                InferHub.Coordinator.Cluster.SingleCoordinatorMembership>();

            app = builder.Build();
            app.MapHub<NodeHub>("/hubs/node");

            await app.StartAsync();
            Coordinator = app.Services.GetRequiredService<NodeProfileCoordinator>();
            Toggle = app.Services.GetRequiredService<NodeBackendToggle>();
            Router = app.Services.GetRequiredService<IRouter>();
            Dispatcher = app.Services.GetRequiredService<IDispatcher>();
        }

        private async Task StartNodeAsync(FakeLlamaServer qwen, FakeLlamaServer phi)
        {
            var services = new ServiceCollection();
            services.AddHttpClient(UpstreamBackend.HttpClientName);
            var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

            UpstreamBackend Llama(FakeLlamaServer server) => new(
                factory,
                Options.Create(new BackendOptions { Type = BackendOptions.LlamaCpp }),
                Options.Create(new UpstreamBackendOptions { BaseUrl = server.Url + "/v1" }),
                NullLogger<UpstreamBackend>.Instance,
                [CapabilityKinds.Chat]);

            engines = new MultiBackend(
                [
                    new Engine("phi", BackendOptions.LlamaCpp, Llama(phi), autostart: false),
                    new Engine("qwen", BackendOptions.LlamaCpp, Llama(qwen), autostart: true)
                ],
                TimeSpan.FromSeconds(5),
                TimeProvider.System,
                NullLogger.Instance);

            await engines.StartAsync(CancellationToken.None);

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
                engines,
                new InferenceExecutor(engines, replicas, TestProfiles.IdleRetrieval(), NullLogger<InferenceExecutor>.Instance),
                new ModelCommandExecutor(engines, NullLogger<ModelCommandExecutor>.Instance),
                new ToolExecutor(runtime, toolOptions, NullLogger<ToolExecutor>.Instance),
                runtime,
                toolOptions,
                new NodeProfileApplier(
                    nodeOptions,
                    toolOptions,
                    engines,
                    runtime,
                    TestProfiles.IdleRetrieval(),
                    NullLogger<NodeProfileApplier>.Instance,
                    engines),
                TestProfiles.IdleRetrieval(),
                replicas,
                new NoBackendSupervisor(),
                InferHub.Node.Resources.NoResourceGovernor.Instance,
                NullLogger<CoordinatorConnection>.Instance,
                engines: engines);

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

            await engines.StopAllAsync();
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
}
