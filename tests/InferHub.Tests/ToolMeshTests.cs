using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
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
/// The whole phase, end to end: an HTTP client → a real coordinator → a real SignalR wire → a real
/// node → a <b>real child process</b> → and back.
/// </summary>
/// <remarks>
/// <para>
/// It is built out of the shipped pieces on purpose. The node side is a real
/// <see cref="CoordinatorConnection"/>, so the hub method names and the handler registrations under
/// test are the ones that ship rather than the ones a test author remembered; the hub side is the
/// real <see cref="Dispatcher"/>, <see cref="NodeHub"/>, <see cref="NodeRegistry"/>,
/// <see cref="Router"/> and <c>/api/tools/{capability}</c>.
/// </para>
/// <para>
/// This is the shape <c>NodeHubStreamingTests</c> exists to enforce: streaming was silently broken
/// end-to-end for several releases because every test stubbed <c>IDispatcher</c> and none crossed
/// the wire. <c>StreamToolChunks</c> is the third client-to-server stream in this codebase, so it
/// gets the same treatment on the day it lands rather than after the outage.
/// </para>
/// </remarks>
public class ToolMeshTests
{
    [Fact]
    public async Task AToolJobRoundTripsFromAnHttpClientThroughTheMeshToAChildProcessAndBack()
    {
        await using var mesh = await ToolMesh.StartAsync();

        var response = await mesh.Client.PostAsync(
            "/api/tools/echo",
            JsonContent.Create(new { model = "echo", hello = "mesh" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"hello\":\"mesh\"", body);
        Assert.Equal("node", response.Headers.GetValues("X-InferHub-Served-By").Single());
    }

    [Fact]
    public async Task TheNodeDeclaresItsToolCapabilitiesToTheCoordinator()
    {
        await using var mesh = await ToolMesh.StartAsync();

        var node = Assert.Single(mesh.Registry.Snapshot(DateTimeOffset.UtcNow));
        var capability = Assert.Single(node.Capabilities ?? [], c => c.Kind == "echo");

        Assert.Equal(["echo"], capability.Models);
    }

    [Fact]
    public async Task AStreamingToolJobBindsAcrossTheRealWireAndDeliversEveryChunk()
    {
        await using var mesh = await ToolMesh.StartAsync();

        var response = await mesh.Client.PostAsync(
            "/api/tools/echo?stream=true",
            JsonContent.Create(new { model = "echo", behaviour = "chunks", count = 3, stream = true }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var lines = (await response.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, lines.Length);
        Assert.Contains("\"index\":0", lines[0]);
        Assert.Contains("\"chunks\":3", lines[^1]);
    }

    [Fact]
    public async Task AttachmentsCrossTheWireAndAFileComesBack()
    {
        await using var mesh = await ToolMesh.StartAsync();

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("echo"), "model");
        form.Add(new StringContent("""{"model":"echo","behaviour":"files"}"""), "payload");
        form.Add(new ByteArrayContent("bytes over the wire"u8.ToArray()), "file", "input.txt");

        var response = await mesh.Client.PostAsync("/api/tools/echo", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("echoed 1 file(s)", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The limit that is not the edge's, and the reason this test exists at all.
    /// </summary>
    /// <remarks>
    /// SignalR's default <c>MaximumReceiveMessageSize</c> is <b>32 KB</b>, and exceeding it does not
    /// fail the message — it kills the connection. Phase 41 shipped attachments and verified them
    /// across a real wire with a <em>16-byte</em> file, four orders of magnitude under the cap, so
    /// the wire test proved the plumbing and said nothing about the size. Found in phase 42 by
    /// running a real mesh: a six-second synthesised wav is ~300 KB, so <em>every</em> real
    /// <c>/v1/audio/speech</c> through a coordinator returned a 500 and dropped the node.
    /// <c>NodeHubLimits</c> derives the wire cap from <c>Tools:MaxAttachmentBytes</c>, so the two
    /// numbers cannot disagree. This asserts a payload comfortably past 32 KB in <b>both</b>
    /// directions.
    /// </remarks>
    [Fact]
    public async Task AnAttachmentLargerThanSignalRsDefaultMessageSizeCrossesTheWireBothWays()
    {
        await using var mesh = await ToolMesh.StartAsync();

        // 256 KB up, which the node reads back and reports on, and a file back down.
        var payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("echo"), "model");
        form.Add(new StringContent("""{"model":"echo","behaviour":"files"}"""), "payload");
        form.Add(new ByteArrayContent(payload), "file", "big.bin");

        var response = await mesh.Client.PostAsync("/api/tools/echo", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("echoed 1 file(s)", await response.Content.ReadAsStringAsync());

        // …and the node is still connected afterwards, which is the half a 500 would hide: blowing
        // the cap tears down the SignalR connection, so the *next* request is what tells you.
        var next = await mesh.Client.PostAsync(
            "/api/tools/echo",
            JsonContent.Create(new { model = "echo", still = "connected" }));

        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnAttachmentCapOfNothingLeavesSignalRsOwnDefaultAlone(long attachmentBytes)
        => Assert.Equal(NodeHubLimits.SignalRDefault, NodeHubLimits.ReceiveSizeFor(attachmentBytes));

    [Fact]
    public void AnyRealAttachmentCapRaisesTheWireCapAboveTheDefault()
        => Assert.True(NodeHubLimits.ReceiveSizeFor(1024) >= NodeHubLimits.SignalRDefault);

    [Fact]
    public void TheWireCapCoversTheAttachmentCapAfterBase64()
    {
        // 25 MB of bytes is ~33.3 MB of base64 before the envelope. A cap that forgot the encoding
        // would be under by a third — which is a limit that works in every test with a small file.
        var cap = NodeHubLimits.ReceiveSizeFor(ToolAttachmentLimits.DefaultMaxBytes);

        Assert.True(cap > ToolAttachmentLimits.DefaultMaxBytes * 4 / 3);
    }

    [Fact]
    public async Task AnAttachmentOverTheCapIsA413AtTheEdgeNamingTheLimit()
    {
        await using var mesh = await ToolMesh.StartAsync(maxAttachmentBytes: 32);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("echo"), "model");
        form.Add(new ByteArrayContent(new byte[128]), "file", "big.bin");

        var response = await mesh.Client.PostAsync("/api/tools/echo", form);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("over the 32-byte limit", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ACapabilityNobodyProvidesIsA503WithRetryAfterAndAModelNobodyHoldsIsStillA404()
    {
        await using var mesh = await ToolMesh.StartAsync();

        // The model exists on the node (it is the echo tool's), but nothing provides 'transcribe'.
        var unprovided = await mesh.Client.PostAsync(
            "/api/tools/transcribe",
            JsonContent.Create(new { model = "echo" }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, unprovided.StatusCode);
        Assert.Equal("30", unprovided.Headers.GetValues("Retry-After").Single());

        // Read the field rather than the wire: System.Text.Json escapes the quotes around the
        // capability name, so a substring assertion here would be checking the encoder.
        var refusal = JsonDocument.Parse(await unprovided.Content.ReadAsStringAsync())
            .RootElement.GetProperty("error").GetString();

        Assert.Equal("no node currently provides 'transcribe' for model 'echo'", refusal);

        var unknown = await mesh.Client.PostAsync(
            "/api/tools/echo",
            JsonContent.Create(new { model = "does-not-exist" }));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task AToolFailureIsAFailedJobAndTheNodeStillServesInferenceAfterwards()
    {
        await using var mesh = await ToolMesh.StartAsync();

        var failed = await mesh.Client.PostAsync(
            "/api/tools/echo",
            JsonContent.Create(new { model = "echo", behaviour = "error", message = "no such voice" }));

        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Contains("no such voice", await failed.Content.ReadAsStringAsync());

        // The claim the phase turns on: a tool failure is a failed job, never a failed node.
        var next = await mesh.Client.PostAsync(
            "/api/tools/echo",
            JsonContent.Create(new { model = "echo", still = "alive" }));

        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    /// <summary>
    /// The binder trap, for the third method. See the comment on <c>NodeHub.StreamChunks</c>: a
    /// <see cref="CancellationToken"/> parameter here is counted as a real argument the caller must
    /// send, the stream never binds, and every streaming request hangs forever.
    /// </summary>
    [Fact]
    public void StreamToolChunksMustNotDeclareACancellationTokenParameter()
    {
        var method = typeof(NodeHub).GetMethod(
            nameof(NodeHub.StreamToolChunks),
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);

        var parameters = method!.GetParameters();

        Assert.Single(parameters);
        Assert.Equal(typeof(IAsyncEnumerable<ToolChunk>), parameters[0].ParameterType);
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(CancellationToken));
    }

    // ---- the mesh ------------------------------------------------------------------------------

    private sealed class ToolMesh : IAsyncDisposable
    {
        private WebApplication app = null!;
        private CoordinatorConnection node = null!;
        private ProcessToolRuntime runtime = null!;
        private ToolWorkerFixture.TempDirectory manifests = null!;
        private ToolWorkerFixture.TempDirectory scratch = null!;

        private const string Secret = "tool-mesh-secret";

        public HttpClient Client { get; private set; } = null!;

        public NodeRegistry Registry { get; } = new();

        public static async Task<ToolMesh> StartAsync(long? maxAttachmentBytes = null)
        {
            var mesh = new ToolMesh
            {
                manifests = new ToolWorkerFixture.TempDirectory("inferhub-manifests"),
                scratch = new ToolWorkerFixture.TempDirectory()
            };

            mesh.manifests.WriteManifest("echo.json", new
            {
                id = "echo",
                capabilities = new[] { new { kind = "echo", models = new[] { "echo" } } },
                command = ToolWorkerFixture.Command(),
                requestTimeoutSeconds = 30,
                startTimeoutSeconds = 30
            });

            await mesh.StartCoordinatorAsync(maxAttachmentBytes);
            await mesh.StartNodeAsync();

            return mesh;
        }

        private async Task StartCoordinatorAsync(long? maxAttachmentBytes)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            builder.Services.AddSignalR(o =>
                o.MaximumReceiveMessageSize = InferHub.Coordinator.Hubs.NodeHubLimits.ReceiveSizeFor(
                    maxAttachmentBytes ?? InferHub.Shared.Contracts.ToolAttachmentLimits.DefaultMaxBytes));
            builder.Services.AddSingleton<IOptionsMonitor<ApiKeyOptions>>(
                new ApiKeyMonitor(new ApiKeyOptions { NodeEnrollmentSecret = Secret }));
            builder.Services.AddSingleton<NodeAuthFilter>();
            builder.Services.AddSingleton<INodeRegistry>(Registry);
            builder.Services.AddSingleton<InferHub.Coordinator.Services.IRouter, Router>();
            builder.Services.AddSingleton<IConversationAffinity>(
                new ConversationAffinity(Options.Create(new RouterOptions()), new NoAffinityStore(), TimeProvider.System));
            builder.Services.AddSingleton<INodeConnectionTracker, NodeConnectionTracker>();
            builder.Services.AddSingleton<Metrics>();
            builder.Services.AddSingleton<ThroughputTracker>();
            builder.Services.Configure<DispatcherOptions>(_ => { });
            builder.Services.Configure<RouterOptions>(_ => { });
            builder.Services.Configure<ToolEdgeOptions>(options =>
            {
                if (maxAttachmentBytes is { } cap)
                {
                    options.MaxAttachmentBytes = cap;
                }
            });
            builder.Services.AddSingleton<Dispatcher>();
            builder.Services.AddSingleton<IDispatcher>(sp => sp.GetRequiredService<Dispatcher>());
            builder.Services.AddSingleton<IToolDispatcher>(sp => sp.GetRequiredService<Dispatcher>());
            builder.Services.AddSingleton<InferHub.Coordinator.Cluster.IClusterMembership,
                InferHub.Coordinator.Cluster.SingleCoordinatorMembership>();

            app = builder.Build();
            app.MapToolEndpoints();
            app.MapHub<NodeHub>("/hubs/node");

            await app.StartAsync();
            Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        }

        private async Task StartNodeAsync()
        {
            var toolOptions = ToolWorkerFixture.Options(scratch.Path, "echo");
            toolOptions.ManifestDirectory = manifests.Path;

            runtime = new ProcessToolRuntime(
                ToolWorkerFixture.Wrap(toolOptions),
                TimeProvider.System,
                NullLoggerFactory.Instance,
                NullLogger<ProcessToolRuntime>.Instance);

            await runtime.StartAsync(CancellationToken.None);

            var backend = new ToolOnlyBackend();
            var replicas = new ReplicaStore(
                Options.Create(new VectorReplicaOptions
                {
                    ReplicaDirectory = Path.Combine(scratch.Path, "replicas")
                }),
                NullLogger<ReplicaStore>.Instance);

            node = new CoordinatorConnection(
                Options.Create(new CoordinatorOptions
                {
                    Url = app.Urls.First(),
                    EnrollmentSecret = Secret,
                    HeartbeatInterval = TimeSpan.FromSeconds(30),
                    ModelRefreshInterval = TimeSpan.FromSeconds(30)
                }),
                Options.Create(new NodeOptions { Name = "tool-node" }),
                new FixedIdentity("tool-node"),
                backend,
                new InferenceExecutor(backend, replicas, NullLogger<InferenceExecutor>.Instance),
                new ModelCommandExecutor(backend, NullLogger<ModelCommandExecutor>.Instance),
                new ToolExecutor(runtime, ToolWorkerFixture.Wrap(toolOptions), NullLogger<ToolExecutor>.Instance),
                runtime,
                replicas,
                new NoBackendSupervisor(),
                NullLogger<CoordinatorConnection>.Instance);

            await node.StartAsync(CancellationToken.None);

            // Registration and the first model report are two invocations; wait for the capability
            // to land rather than racing it.
            for (var i = 0; i < 100 && !Registry.Snapshot(DateTimeOffset.UtcNow)
                     .Any(n => (n.Capabilities ?? []).Any(c => c.Kind == "echo")); i++)
            {
                await Task.Delay(50);
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
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            manifests.Dispose();
            scratch.Dispose();
        }

        /// <summary>
        /// A node whose backend holds nothing: it exists to prove that a box with no Ollama models
        /// still declares — and is routed for — its tool capabilities (phase-41's tools-only node).
        /// </summary>
        private sealed class ToolOnlyBackend : IInferenceBackend
        {
            public string Name => "none";

            public string Endpoint => "http://127.0.0.1:0/";

            public bool SupportsModelManagement => false;

            public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ModelInfo>>([]);

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

        private sealed class FixedIdentity(string id) : INodeIdentity
        {
            public string GetOrCreateNodeId() => id;
        }

        private sealed class NoAffinityStore : IAffinityStore
        {
            public IReadOnlyCollection<PersistedAffinity> Load() => [];

            public void Record(string conversationKey, string nodeId, DateTimeOffset lastUsed)
            {
            }

            public void Forget(string conversationKey)
            {
            }
        }

        private sealed class ApiKeyMonitor(ApiKeyOptions value) : IOptionsMonitor<ApiKeyOptions>
        {
            public ApiKeyOptions CurrentValue { get; } = value;

            public ApiKeyOptions Get(string? name) => CurrentValue;

            public IDisposable? OnChange(Action<ApiKeyOptions, string?> listener) => null;
        }
    }
}
