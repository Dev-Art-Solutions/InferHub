using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.OpenAi;
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
/// The two hosts phase 46 has to keep in step, each with a <b>real child process</b> behind it.
/// </summary>
/// <remarks>
/// <para>
/// The worker is the echo fixture in its phase-46 image mode: it writes real PNGs — signature, IHDR,
/// a valid zlib IDAT — into the scratch directory and names them back, and refuses a size outside its
/// aspect buckets exactly as a real recipe does. That is enough to prove every path this phase owns:
/// the envelope, the budgets, the statuses, the metering, the parity, the wire. It is <b>not</b>
/// enough to prove that a picture is a picture, and nothing here pretends otherwise — that is what
/// the published-image verification is for, and a unit suite that needed Stable Diffusion weights,
/// four gigabytes of PyTorch and a card would simply not run.
/// </para>
/// <para>
/// Both halves cross a real wire, which is not decoration: <c>SoloParityTests</c> exists because
/// handler-level comparison proves the handlers agree and says nothing about the response, and phase
/// 42 shipped a release in which every attachment test passed against a <b>16-byte</b> file while
/// the first real one tore the SignalR connection down.
/// </para>
/// </remarks>
internal static class ImageFixture
{
    public const string Model = "sd-test";

    /// <summary>
    /// A recipe this fixture declares under <c>image</c> and <b>not</b> under <c>image-edit</c>
    /// (phase 50) — the fixture's FLUX: a model the fleet holds and cannot edit with.
    /// </summary>
    public const string GenerateOnlyModel = "generate-only-test";

    /// <summary>An aspect bucket the echo worker accepts. Anything else is its <c>invalid_request</c>.</summary>
    public const string Size = "512x512";

    /// <summary>The fixture's video recipe (phase 57). Declared only when a test asks for it.</summary>
    public const string VideoModel = "wan-test";

    /// <summary>A geometry the fixture's video recipe offers — and a multiple of 16, unlike <see cref="Size"/>.</summary>
    public const string VideoSize = "832x480";

    /// <summary>The prompt <c>ImagePrivacyTests</c> hunts for. It must reach the worker and nothing else.</summary>
    public const string KnownPrompt = "a lighthouse in a storm, oil painting, dramatic light";

    public static ToolWorkerFixture.TempDirectory Manifests(params string[] workerArguments)
        => Manifests(false, workerArguments);

    /// <param name="video">
    /// Declare the phase-57 <c>video</c> kind as well. <b>Off by default on purpose</b>: every suite
    /// written before this phase asserts against a node that offers two kinds, and a fixture that
    /// silently grew a third would make those assertions pass or fail for reasons nobody chose.
    /// </param>
    public static ToolWorkerFixture.TempDirectory Manifests(bool video, params string[] workerArguments)
    {
        var directory = new ToolWorkerFixture.TempDirectory("inferhub-image-manifests");

        var capabilities = new List<object>
        {
            new { kind = "image", models = new[] { Model, GenerateOnlyModel } },

            // Phase 50. The catalogue splits: one of the two models generates and edits, the other
            // only generates — which is what makes "this recipe cannot edit" a real 503 with a real
            // alternative in it rather than an assertion about a string.
            new { kind = "image-edit", models = new[] { Model } },

            // A second capability on the same node, so the 503-vs-404 split has something real to be
            // wrong about: `speak-test` exists on this fleet, but not for `image`.
            new { kind = "speak", models = new[] { "speak-test" } }
        };

        if (video)
        {
            capabilities.Add(new { kind = "video", models = new[] { VideoModel } });
        }

        directory.WriteManifest("diffusion.json", new
        {
            id = "diffusion",
            capabilities = capabilities.ToArray(),
            command = ToolWorkerFixture.Command(workerArguments),
            requestTimeoutSeconds = 30,
            startTimeoutSeconds = 30
        });

        return directory;
    }

    /// <summary>A solo node with the image worker loaded, on a real Kestrel port.</summary>
    public static Task<(SoloHost Host, IDisposable Cleanup)> SoloAsync(params string[] workerArguments)
        => SoloAsync(null, workerArguments);

    /// <summary>A solo node that also offers the phase-57 video kind.</summary>
    public static Task<(SoloHost Host, IDisposable Cleanup)> SoloVideoAsync(params string[] workerArguments)
        => SoloAsync(null, true, workerArguments);

    /// <param name="seamRepair">
    /// <c>Tools:Image:SeamRepair</c> (phase 55). Solo mode is the shape that shows this key is a
    /// <em>node's</em> answer rather than a hub's: there is no hub, and the ceiling still holds.
    /// </param>
    public static Task<(SoloHost Host, IDisposable Cleanup)> SoloAsync(
        string? seamRepair,
        string[] workerArguments) => SoloAsync(seamRepair, false, workerArguments);

    public static async Task<(SoloHost Host, IDisposable Cleanup)> SoloAsync(
        string? seamRepair,
        bool video,
        string[] workerArguments)
    {
        var manifests = Manifests(video, workerArguments);
        var scratch = new ToolWorkerFixture.TempDirectory("inferhub-image-scratch");

        var host = await SoloHost.StartAsync(
            settings:
            [
                "--Tools:Enabled=true",
                "--Tools:Allowed:0=diffusion",
                $"--Tools:ManifestDirectory={manifests.Path}",
                $"--Tools:ScratchDirectory={scratch.Path}",
                "--Tools:QueueMaxWaitSeconds=5",
                .. seamRepair is null ? Array.Empty<string>() : new[] { $"--Tools:Image:SeamRepair={seamRepair}" }
            ]);

        return (host, new Cleanups(manifests, scratch));
    }

    private sealed class Cleanups(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
            {
                item.Dispose();
            }
        }
    }
}

/// <summary>
/// A coordinator and a node, both real, joined by a real SignalR connection, with the image route
/// mapped on the hub and a real echo child process on the node.
/// </summary>
internal sealed class ImageMesh : IAsyncDisposable
{
    private const string Secret = "image-mesh-secret";

    private WebApplication app = null!;
    private CoordinatorConnection node = null!;
    private ProcessToolRuntime runtime = null!;
    private ToolWorkerFixture.TempDirectory manifests = null!;

    /// <summary>
    /// The node's other on-disk state, kept out of the scratch tree on purpose: <c>ScratchEntryCount</c>
    /// asserts that <b>nothing at all</b> survives a request, and a replica directory sharing the
    /// root would make that assertion pass for the wrong reason forever.
    /// </summary>
    private ToolWorkerFixture.TempDirectory nodeData = null!;

    /// <summary>
    /// <c>Tools:Image:SeamWarnThreshold</c>, which the node states into the worker's environment
    /// (phase-41 D3 clears it first) — so setting it here is also how <c>SeamMetricTests</c> proves
    /// the key travels rather than assuming it.
    /// </summary>
    private double? seamWarnThreshold;

    /// <summary>
    /// <c>Tools:Image:SeamRepair</c> (phase 55), stated into the worker's environment by the same
    /// mechanism — so a test that sets it here is also proving the ceiling travels rather than
    /// assuming it. Unset means the default, <c>off</c>.
    /// </summary>
    private string? seamRepair;

    public HttpClient Client { get; private set; } = null!;

    public NodeRegistry Registry { get; } = new();

    public InMemoryUsageLedger Ledger { get; } = new();

    public AdmissionControl Admission { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>The node's scratch root. After every request it must be empty (phase-41 D5).</summary>
    public ToolWorkerFixture.TempDirectory Scratch { get; private set; } = null!;

    /// <summary>The phase-47 job registry, so a test can reach the store directly where a route cannot.</summary>
    public ImageJobRegistry Jobs { get; private set; } = null!;

    /// <summary>The phase-47 job knobs, so a retention test does not have to wait five minutes.</summary>
    public Action<InferHub.Shared.Images.ImageEdgeOptions>? ConfigureImages { get; set; }

    /// <summary>Who the requests come from. Phase 47's routes are client-scoped and this is the scope.</summary>
    public string ClientId { get; private set; } = "image-client";

    public static async Task<ImageMesh> StartAsync(
        long? maxAttachmentBytes = null,
        ClientLimits? limits = null,
        int? maxBatch = null,
        long? maxResponseBytes = null,
        Action<InferHub.Shared.Images.ImageEdgeOptions>? configureImages = null,
        string clientId = "image-client",
        double? seamWarnThreshold = null,
        string? seamRepair = null,
        bool video = false,
        params string[] workerArguments)
    {
        var mesh = new ImageMesh
        {
            seamRepair = seamRepair,
            manifests = ImageFixture.Manifests(video, workerArguments),
            nodeData = new ToolWorkerFixture.TempDirectory("inferhub-image-node"),
            Scratch = new ToolWorkerFixture.TempDirectory("inferhub-image-scratch"),
            ConfigureImages = configureImages,
            ClientId = clientId,
            seamWarnThreshold = seamWarnThreshold
        };

        await mesh.StartCoordinatorAsync(maxAttachmentBytes, limits, maxBatch, maxResponseBytes);
        await mesh.StartNodeAsync(maxAttachmentBytes);

        return mesh;
    }

    private async Task StartCoordinatorAsync(
        long? maxAttachmentBytes,
        ClientLimits? limits,
        int? maxBatch,
        long? maxResponseBytes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        // A real logger provider that keeps every line, so ImagePrivacyTests can assert on what the
        // hub actually wrote rather than on what it was expected to write.
        builder.Logging.AddProvider(Logs);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        builder.Services.AddSignalR(o =>
            o.MaximumReceiveMessageSize = NodeHubLimits.ReceiveSizeFor(
                maxAttachmentBytes ?? ToolAttachmentLimits.DefaultMaxBytes));
        builder.Services.AddSingleton<IOptionsMonitor<ApiKeyOptions>>(
            new StaticApiKeys(new ApiKeyOptions { NodeEnrollmentSecret = Secret }));
        builder.Services.AddSingleton<NodeAuthFilter>();
        builder.Services.AddSingleton<INodeRegistry>(Registry);
        builder.Services.AddSingleton<InferHub.Coordinator.Services.IRouter, Router>();
        builder.Services.AddSingleton<IConversationAffinity>(
            new ConversationAffinity(Options.Create(new RouterOptions()), new NoAffinity(), TimeProvider.System));
        builder.Services.AddSingleton<INodeConnectionTracker, NodeConnectionTracker>();
        builder.Services.AddSingleton<Metrics>();
        builder.Services.AddSingleton<ThroughputTracker>();
        builder.Services.AddSingleton<IUsageLedger>(Ledger);
        builder.Services.AddSingleton(Admission);
        builder.Services.AddSingleton(services => new UsageMeter(
            Ledger,
            Admission,
            services.GetRequiredService<ILogger<UsageMeter>>()));
        builder.Services.AddSingleton(services => TestUsage.Queue(services.GetRequiredService<INodeRegistry>()));
        builder.Services.Configure<DispatcherOptions>(_ => { });
        builder.Services.Configure<RouterOptions>(_ => { });
        builder.Services.Configure<ToolEdgeOptions>(options =>
        {
            if (maxAttachmentBytes is { } cap)
            {
                options.MaxAttachmentBytes = cap;
            }
        });
        builder.Services.Configure<InferHub.Shared.Images.ImageEdgeOptions>(options =>
        {
            if (maxBatch is { } batch)
            {
                options.MaxBatch = batch;
            }

            if (maxResponseBytes is { } bytes)
            {
                options.MaxResponseBytes = bytes;
            }

            ConfigureImages?.Invoke(options);
        });
        builder.Services.AddSingleton<Dispatcher>();
        builder.Services.AddSingleton<IDispatcher>(sp => sp.GetRequiredService<Dispatcher>());
        builder.Services.AddSingleton<IToolDispatcher>(sp => sp.GetRequiredService<Dispatcher>());

        // Phase 56, and built from the same factory the real composition root uses so a test that
        // turns persistence on through ConfigureImages gets the archive the product would give it —
        // not a second one written for the suite.
        builder.Services.AddSingleton<InferHub.Shared.Images.IImageJobArchive>(sp =>
            InferHub.Shared.Images.ImageJobArchives.Create(
                sp.GetRequiredService<IOptions<InferHub.Shared.Images.ImageEdgeOptions>>().Value.Jobs));
        builder.Services.AddSingleton<ImageJobRegistry>();
        builder.Services.AddSingleton<InferHub.Coordinator.Cluster.IClusterMembership,
            InferHub.Coordinator.Cluster.SingleCoordinatorMembership>();

        app = builder.Build();
        Jobs = app.Services.GetRequiredService<ImageJobRegistry>();

        // Stand in for BearerApiKeyMiddleware, which is not in this pipeline: the identity is what
        // phase 47's routes scope on, and ImageEndpointTests separately asserts that the real prefix
        // guard covers /v1/images and /api.
        var client = new ResolvedClient(ClientId, limits, null);
        app.Use(async (context, next) =>
        {
            context.Items[BearerApiKeyMiddleware.ClientItemKey] = client;
            await next();
        });

        app.MapImageEndpoints();
        app.MapImageJobEndpoints();
        app.MapVideoEndpoints();
        app.MapHub<NodeHub>("/hubs/node");

        await app.StartAsync();
        Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    private async Task StartNodeAsync(long? maxAttachmentBytes)
    {
        var toolOptions = ToolWorkerFixture.Options(Scratch.Path, "diffusion");
        toolOptions.ManifestDirectory = manifests.Path;

        if (seamWarnThreshold is { } threshold)
        {
            toolOptions.Image.SeamWarnThreshold = threshold;
        }

        if (seamRepair is { } ceiling)
        {
            toolOptions.Image.SeamRepair = ceiling;
        }

        if (maxAttachmentBytes is { } cap)
        {
            toolOptions.MaxAttachmentBytes = cap;
        }

        runtime = new ProcessToolRuntime(
            ToolWorkerFixture.Wrap(toolOptions),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            NullLogger<ProcessToolRuntime>.Instance);

        await runtime.StartAsync(CancellationToken.None);

        var backend = new NoBackend();
        var replicas = new ReplicaStore(
            Options.Create(new VectorReplicaOptions { ReplicaDirectory = Path.Combine(nodeData.Path, "replicas") }),
            NullLogger<ReplicaStore>.Instance);

        node = new CoordinatorConnection(
            Options.Create(new CoordinatorOptions
            {
                Url = app.Urls.First(),
                EnrollmentSecret = Secret,
                HeartbeatInterval = TimeSpan.FromSeconds(30),
                ModelRefreshInterval = TimeSpan.FromSeconds(30)
            }),
            Options.Create(new NodeOptions { Name = "image-node" }),
            new FixedNodeId("image-node"),
            backend,
            new InferenceExecutor(backend, replicas, TestProfiles.IdleRetrieval(), NullLogger<InferenceExecutor>.Instance),
            new ModelCommandExecutor(backend, NullLogger<ModelCommandExecutor>.Instance),
            new ToolExecutor(runtime, ToolWorkerFixture.Wrap(toolOptions), NullLogger<ToolExecutor>.Instance),
            runtime,
            ToolWorkerFixture.Wrap(toolOptions),
            TestProfiles.Applier(backend, runtime),
            TestProfiles.IdleRetrieval(),
            replicas,
            new NoBackendSupervisor(),
            NullLogger<CoordinatorConnection>.Instance);

        await node.StartAsync(CancellationToken.None);

        for (var i = 0; i < 200 && !Registry.Snapshot(DateTimeOffset.UtcNow)
                 .Any(n => (n.Capabilities ?? []).Any(c => c.Kind == "image")); i++)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>Every per-request scratch directory, deleted in a <c>finally</c>, leaves nothing behind.</summary>
    public int ScratchEntryCount() =>
        Directory.Exists(Scratch.Path)
            ? Directory.GetFileSystemEntries(Scratch.Path).Length
            : 0;

    /// <summary>
    /// Takes the node away mid-job, which is the only honest way to test phase-47 D7: a running job
    /// fails with <c>node_lost</c> and is <b>not</b> silently retried.
    /// </summary>
    public async Task StopNodeAsync()
    {
        await node.StopAsync(CancellationToken.None);

        for (var i = 0; i < 100 && NodeIsRegistered(); i++)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>Whether the node is still connected — the assertion the 32 KB bug needed.</summary>
    public bool NodeIsRegistered() =>
        Registry.Snapshot(DateTimeOffset.UtcNow).Any(n => n.NodeId == "image-node");

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
        nodeData.Dispose();
        Scratch.Dispose();
    }

    /// <summary>A node with no inference backend at all — a diffusion-only box, which is the real shape.</summary>
    private sealed class NoBackend : IInferenceBackend
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

    private sealed class FixedNodeId(string id) : INodeIdentity
    {
        public string GetOrCreateNodeId() => id;
    }

    private sealed class NoAffinity : IAffinityStore
    {
        public IReadOnlyCollection<PersistedAffinity> Load() => [];

        public void Record(string conversationKey, string nodeId, DateTimeOffset lastUsed)
        {
        }

        public void Forget(string conversationKey)
        {
        }
    }

    private sealed class StaticApiKeys(ApiKeyOptions value) : IOptionsMonitor<ApiKeyOptions>
    {
        public ApiKeyOptions CurrentValue { get; } = value;

        public ApiKeyOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ApiKeyOptions, string?> listener) => null;
    }
}
