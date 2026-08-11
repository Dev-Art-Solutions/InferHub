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
/// The two hosts phase 42 has to keep in step, each with a <b>real child process</b> behind it.
/// </summary>
/// <remarks>
/// <para>
/// The worker is the echo fixture in its phase-42 audio mode: it answers a <c>transcribe</c> request
/// with a canned verbose transcript and a <c>speak</c> request with a real RIFF/WAVE file written
/// into the scratch directory. That is enough to prove every path this phase owns — the formats, the
/// statuses, the metering, the parity — without either suite depending on Whisper weights, a GPU, or
/// four gigabytes of Python. The weights are what the published-image verification is for; a unit
/// suite that needed them would simply not run.
/// </para>
/// <para>
/// Both halves cross a real wire, which is not decoration: <c>SoloParityTests</c> exists because
/// handler-level comparison proves the handlers agree and says nothing about the response, and
/// <c>NodeHubStreamingTests</c> exists because streaming was broken end-to-end for several releases
/// while every test stubbed the dispatcher.
/// </para>
/// </remarks>
internal static class AudioFixture
{
    public const string TranscribeModel = "whisper-test";

    public const string SpeakModel = "voice-test";

    /// <summary>The phrase the echo worker transcribes. <c>AudioPrivacyTests</c> hunts for it.</summary>
    public const string KnownPhrase = "The quick brown fox jumps over the lazy dog.";

    public static ToolWorkerFixture.TempDirectory Manifests(params string[] workerArguments)
    {
        var directory = new ToolWorkerFixture.TempDirectory("inferhub-audio-manifests");

        directory.WriteManifest("audio.json", new
        {
            id = "audio",
            capabilities = new object[]
            {
                new { kind = "transcribe", models = new[] { TranscribeModel } },
                new { kind = "speak", models = new[] { SpeakModel } }
            },
            command = ToolWorkerFixture.Command(workerArguments),
            requestTimeoutSeconds = 30,
            startTimeoutSeconds = 30
        });

        return directory;
    }

    /// <summary>A solo node with the audio worker loaded, on a real Kestrel port.</summary>
    public static async Task<(SoloHost Host, IDisposable Cleanup)> SoloAsync(params string[] workerArguments)
    {
        var manifests = Manifests(workerArguments);
        var scratch = new ToolWorkerFixture.TempDirectory("inferhub-audio-scratch");

        var host = await SoloHost.StartAsync(
            settings:
            [
                "--Tools:Enabled=true",
                "--Tools:Allowed:0=audio",
                $"--Tools:ManifestDirectory={manifests.Path}",
                $"--Tools:ScratchDirectory={scratch.Path}",
                "--Tools:QueueMaxWaitSeconds=5"
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
/// A coordinator and a node, both real, joined by a real SignalR connection, with the audio routes
/// mapped on the hub and a real echo child process on the node.
/// </summary>
internal sealed class AudioMesh : IAsyncDisposable
{
    private const string Secret = "audio-mesh-secret";

    private WebApplication app = null!;
    private CoordinatorConnection node = null!;
    private ProcessToolRuntime runtime = null!;
    private ToolWorkerFixture.TempDirectory manifests = null!;
    private ToolWorkerFixture.TempDirectory scratch = null!;

    public HttpClient Client { get; private set; } = null!;

    public NodeRegistry Registry { get; } = new();

    public InMemoryUsageLedger Ledger { get; } = new();

    public AdmissionControl Admission { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    public static async Task<AudioMesh> StartAsync(
        long? maxAttachmentBytes = null,
        ClientLimits? limits = null,
        params string[] workerArguments)
    {
        var mesh = new AudioMesh
        {
            manifests = AudioFixture.Manifests(workerArguments),
            scratch = new ToolWorkerFixture.TempDirectory("inferhub-audio-scratch")
        };

        await mesh.StartCoordinatorAsync(maxAttachmentBytes, limits);
        await mesh.StartNodeAsync();

        return mesh;
    }

    private async Task StartCoordinatorAsync(long? maxAttachmentBytes, ClientLimits? limits)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        // A real logger provider that keeps every line, so AudioPrivacyTests can assert on what the
        // hub actually wrote rather than on what it was expected to write.
        builder.Logging.AddProvider(Logs);
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        builder.Services.AddSignalR(o =>
            o.MaximumReceiveMessageSize = InferHub.Coordinator.Hubs.NodeHubLimits.ReceiveSizeFor(
                maxAttachmentBytes ?? InferHub.Shared.Contracts.ToolAttachmentLimits.DefaultMaxBytes));
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
        builder.Services.AddSingleton<Dispatcher>();
        builder.Services.AddSingleton<IDispatcher>(sp => sp.GetRequiredService<Dispatcher>());
        builder.Services.AddSingleton<IToolDispatcher>(sp => sp.GetRequiredService<Dispatcher>());
        builder.Services.AddSingleton<InferHub.Coordinator.Cluster.IClusterMembership,
            InferHub.Coordinator.Cluster.SingleCoordinatorMembership>();

        app = builder.Build();

        if (limits is not null)
        {
            // Stand in for BearerApiKeyMiddleware, which is not in this pipeline: the limits are
            // what the test is about, not the key parsing, and AudioEndpointTests separately
            // asserts that the real prefix guard covers /v1/audio.
            var client = new ResolvedClient("audio-client", limits, null);
            app.Use(async (context, next) =>
            {
                context.Items[BearerApiKeyMiddleware.ClientItemKey] = client;
                await next();
            });
        }

        app.MapAudioEndpoints();
        app.MapHub<NodeHub>("/hubs/node");

        await app.StartAsync();
        Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    private async Task StartNodeAsync()
    {
        var toolOptions = ToolWorkerFixture.Options(scratch.Path, "audio");
        toolOptions.ManifestDirectory = manifests.Path;

        runtime = new ProcessToolRuntime(
            ToolWorkerFixture.Wrap(toolOptions),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            NullLogger<ProcessToolRuntime>.Instance);

        await runtime.StartAsync(CancellationToken.None);

        var backend = new NoBackend();
        var replicas = new ReplicaStore(
            Options.Create(new VectorReplicaOptions { ReplicaDirectory = Path.Combine(scratch.Path, "replicas") }),
            NullLogger<ReplicaStore>.Instance);

        node = new CoordinatorConnection(
            Options.Create(new CoordinatorOptions
            {
                Url = app.Urls.First(),
                EnrollmentSecret = Secret,
                HeartbeatInterval = TimeSpan.FromSeconds(30),
                ModelRefreshInterval = TimeSpan.FromSeconds(30)
            }),
            Options.Create(new NodeOptions { Name = "audio-node" }),
            new FixedNodeId("audio-node"),
            backend,
            new InferenceExecutor(backend, replicas, TestProfiles.IdleRetrieval(), NullLogger<InferenceExecutor>.Instance),
            new ModelCommandExecutor(backend, NullLogger<ModelCommandExecutor>.Instance),
            new ToolExecutor(runtime, ToolWorkerFixture.Wrap(toolOptions), NullLogger<ToolExecutor>.Instance),
            runtime,
            TestProfiles.Applier(backend, runtime),
            TestProfiles.IdleRetrieval(),
            replicas,
            new NoBackendSupervisor(),
            NullLogger<CoordinatorConnection>.Instance);

        await node.StartAsync(CancellationToken.None);

        for (var i = 0; i < 200 && !Registry.Snapshot(DateTimeOffset.UtcNow)
                 .Any(n => (n.Capabilities ?? []).Any(c => c.Kind == "transcribe")); i++)
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

    /// <summary>A node with no inference backend at all — a tools-only box, which is a real shape.</summary>
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
