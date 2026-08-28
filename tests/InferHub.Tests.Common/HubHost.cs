using System.Threading.Channels;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.OpenAi;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InferHub.Tests;

/// <summary>
/// A coordinator on a real Kestrel port, mapping the <em>real</em> inference and OpenAI endpoints
/// over a scripted dispatcher — one node, always routable, answering with canned Ollama JSON.
/// </summary>
/// <remarks>
/// It is the other half of <see cref="SoloParityTests"/>. Everything above the dispatcher is the
/// shipped code — translators, formatters, the SSE and NDJSON writers, the error envelope — and
/// only the fleet below it is scripted. Which is the point: a fleet of one is what a solo node is,
/// so if the two hosts disagree, the disagreement is in the layer this phase touched.
/// </remarks>
internal sealed class HubHost : IAsyncDisposable
{
    private WebApplication app = null!;

    public ScriptedDispatcher Dispatcher { get; } = new();

    public HttpClient Client { get; private set; } = null!;

    /// <summary>The Ollama-shaped job body the hub handed the node, for cross-checking translation.</summary>
    public string? LastJobJson => Dispatcher.LastJobJson;

    /// <summary>The corpus directory, when this hub was started with retrieval (phase 38).</summary>
    private string? dataDirectory;

    public static async Task<HubHost> StartAsync(
        string blockingResponse,
        string[]? streamChunks = null,
        string? failure = null,
        IReadOnlyList<ModelInfo>? models = null,
        bool retrieval = false,
        ProviderOptions? providers = null)
    {
        var host = new HubHost();
        host.Dispatcher.BlockingResponse = blockingResponse;
        host.Dispatcher.Failure = failure;
        host.Dispatcher.StreamChunks = streamChunks ?? [];

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();


        // The real registry with one real registration: /v1/models and /api/tags then run the
        // shipped code path rather than a stub's idea of it.
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("conn-1", new NodeRegistration("node-1", "parity-node", "http://localhost:11434/", "test"), now);
        registry.ReportModels(
            "conn-1",
            new NodeModels("node-1", models ?? [new ModelInfo("llama3", "sha256:abc", 4661211808)], now),
            now);

        builder.Services.AddSingleton<INodeRegistry>(registry);
        builder.Services.AddSingleton<IRouter>(new AlwaysRoutes());
        builder.Services.AddSingleton<IDispatcher>(host.Dispatcher);
        if (providers is null)
        {
            builder.Services.AddSingleton<IProviderDispatcher>(new NoProvider());
        }
        else
        {
            // Phase 65: the real registry, the real dispatcher and a real HttpClient, so a routing
            // test crosses a socket to a vendor rather than asking a stub what it believes.
            builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(providers));
            builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new FallbackOptions()));
            builder.Services.AddSingleton<IProviderRegistry, ProviderRegistry>();
            builder.Services.AddHttpClient(ProviderDispatcher.HttpClientName);
            builder.Services.AddSingleton<IProviderDispatcher, ProviderDispatcher>();
        }
        builder.Services.AddSingleton<IEmbeddingDispatcher>(new ScriptedEmbeddings(retrieval));
        builder.Services.AddSingleton<Metrics>();
        builder.Services.AddSingleton<AdmissionControl>();
        builder.Services.AddSingleton(services => TestUsage.Meter(
            admission: services.GetRequiredService<AdmissionControl>()));
        builder.Services.AddSingleton(services => TestUsage.Queue(
            services.GetRequiredService<INodeRegistry>()));

        if (retrieval)
        {
            host.dataDirectory = Path.Combine(Path.GetTempPath(), "inferhub-hub-rag-" + Guid.NewGuid().ToString("N"));
            AddRetrieval(builder, host.dataDirectory);
        }

        host.app = builder.Build();
        host.app.MapInferenceEndpoints();
        host.app.MapOpenAiEndpoints();

        if (retrieval)
        {
            host.app.MapIngestionEndpoints();
            host.app.MapSearchEndpoints();

            // Phase 44: the admin collection routes are mapped too, because the hub-side create is
            // where the ownership refusal lives (D1). `supportsReplication: false` keeps the fleet
            // half out of a fixture that has no replication services.
            host.app.MapVectorEndpoints(supportsReplication: false);
        }

        await host.app.StartAsync();

        host.Client = new HttpClient { BaseAddress = new Uri(host.app.Urls.First()) };

        return host;
    }

    /// <summary>
    /// Creates a collection through the store directly.
    /// </summary>
    /// <remarks>
    /// On the hub, collection lifecycle is an admin action (<c>/api/admin/vector/collections</c>)
    /// and ingesting into a name that does not exist is a 404 for an unscoped client — phase-23's
    /// refusal to auto-create, relaxed in phase-31 D5 only for a client whose config names its
    /// scope. A solo node auto-provisions instead, because its own config is that grant. That
    /// difference is recorded rather than papered over, and it is why the parity suite creates the
    /// hub's collection explicitly here and lets the node's first ingest create its own.
    /// </remarks>
    public Task<CollectionInfo> CreateCollectionAsync(string name, int dimension)
        => app.Services.GetRequiredService<IVectorStore>().CreateCollectionAsync(name, dimension, distance: null);

    /// <summary>Who owns which collection (phase 44). Empty unless a test assigns something.</summary>
    public InferHub.Coordinator.Vector.CollectionOwnership Ownership
        => app.Services.GetRequiredService<InferHub.Coordinator.Vector.CollectionOwnership>();

    /// <summary>What the hub's own store holds under a name, which for a node-owned one is nothing.</summary>
    public Task<CollectionInfo?> StoreCollectionAsync(string name)
        => app.Services.GetRequiredService<IVectorStore>().GetCollectionAsync(name);

    /// <summary>
    /// The hub's half of the phase-38 retrieval stack, composed exactly as the shipped
    /// <c>AddInferHubVectorStore</c> composes it for the <c>local</c> provider — same store, same
    /// pipelines, same seams. Only the fleet underneath is scripted.
    /// </summary>
    private static void AddRetrieval(WebApplicationBuilder builder, string dataDirectory)
    {
        var options = new VectorStoreOptions
        {
            Enabled = true,
            DataDirectory = dataDirectory,
            Distance = "cosine",
            DefaultEmbeddingModel = "test-embed"
        };

        builder.Services.AddSingleton(_ => new LocalVectorStore(options, NullVectorLog.Instance));
        builder.Services.AddSingleton<IVectorStore>(sp => sp.GetRequiredService<LocalVectorStore>());

        // Phase 44. Ingestion and search ask who owns a collection before they touch a store (D1/D5).
        // Empty here, which is the shape every pre-44 assertion in this suite depends on: no node
        // owns anything, so both endpoints take exactly the hub-owned path they always took.
        builder.Services.AddSingleton<InferHub.Coordinator.Vector.CollectionOwnership>();
        builder.Services.AddSingleton<InferHub.Coordinator.Vector.NodeCorpusDispatcher>();
        builder.Services.AddSingleton<IAuditLog, AuditLog>();

        // Mapping the admin collection routes brings their whole parameter list with it — minimal
        // APIs build every endpoint in the table on the first request, so one unregistered service
        // is a 500 on unrelated routes rather than on the route that wanted it.
        builder.Services.AddSingleton<InferHub.Coordinator.Vector.ReplicaRegistry>();
        builder.Services.AddSingleton<IClientRegistry, ClientRegistry>();
        builder.Services.Configure<ApiKeyOptions>(_ => { });
        builder.Services.Configure<VectorStoreOptions>(_ => { });
        builder.Services.AddSingleton<IVectorQueryRouter, NullVectorQueryRouter>();
        builder.Services.AddSingleton<IReranker, KeepOrderReranker>();
        builder.Services.AddSingleton<IRetrievalMetrics>(sp => sp.GetRequiredService<Metrics>());
        builder.Services.AddSingleton(_ => new TextExtractor());
        builder.Services.AddSingleton<DocumentIndex>();
        builder.Services.Configure<InferHub.Shared.Ingestion.IngestionOptions>(_ => { });

        builder.Services.AddSingleton(sp => new RetrievalPipeline(
            options,
            sp.GetRequiredService<IVectorStore>(),
            sp.GetRequiredService<IEmbeddingDispatcher>(),
            sp.GetRequiredService<IVectorQueryRouter>(),
            sp.GetRequiredService<IReranker>(),
            sp.GetRequiredService<IRetrievalMetrics>(),
            NullVectorLog.Instance));

        builder.Services.AddSingleton(sp => new IngestionPipeline(
            sp.GetRequiredService<IVectorStore>(),
            sp.GetRequiredService<DocumentIndex>(),
            sp.GetRequiredService<TextExtractor>(),
            sp.GetRequiredService<IEmbeddingDispatcher>(),
            new InferHub.Shared.Ingestion.IngestionOptions { EmbeddingModel = "test-embed" },
            options,
            sp.GetRequiredService<IRetrievalMetrics>(),
            NullVectorLog.Instance));
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();

        try
        {
            if (dataDirectory is not null && Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Reranking is not what the parity suite is measuring; keep the fused order.</summary>
    private sealed class KeepOrderReranker : IReranker
    {
        public Task<IReadOnlyList<VectorMatch>> RerankAsync(
            string query,
            IReadOnlyList<VectorMatch> candidates,
            string? model,
            CancellationToken cancellationToken)
            => Task.FromResult(candidates);
    }

    // ---- the scripted fleet ------------------------------------------------------------------

    internal sealed class ScriptedDispatcher : IDispatcher
    {
        public string BlockingResponse { get; set; } = "{}";

        public string[] StreamChunks { get; set; } = [];

        public string? Failure { get; set; }

        public string? LastJobJson { get; private set; }

        /// <summary>The kind of the last job dispatched — phase 44 added two that are not inference.</summary>
        public string? LastJobKind { get; private set; }

        /// <summary>The node the last job was routed to, which is the whole assertion for a node-owned collection.</summary>
        public string? LastNodeId { get; private set; }

        /// <summary>Canned answer for a <c>corpus-*</c> job, so the hub half can be tested without a node.</summary>
        public string? CorpusResponse { get; set; }

        public Task<InferenceResult> DispatchAsync(
            RoutableNode node,
            InferenceJob job,
            CancellationToken cancellationToken)
        {
            LastJobJson = job.RequestJson;
            LastJobKind = job.Kind;
            LastNodeId = node.NodeId;

            if (Failure is not null)
            {
                return Task.FromResult(InferenceResult.Failed(job.JobId, Failure));
            }

            var body = job.Kind.StartsWith("corpus-", StringComparison.Ordinal) && CorpusResponse is not null
                ? CorpusResponse
                : BlockingResponse;

            return Task.FromResult(InferenceResult.Succeeded(job.JobId, body));
        }

        public Task<ChannelReader<InferenceChunk>> DispatchStreamAsync(
            RoutableNode node,
            InferenceJob job,
            CancellationToken cancellationToken)
        {
            LastJobJson = job.RequestJson;

            var channel = Channel.CreateUnbounded<InferenceChunk>();

            foreach (var chunk in StreamChunks)
            {
                channel.Writer.TryWrite(new InferenceChunk(job.JobId, chunk, IsTerminal(chunk)));
            }

            channel.Writer.TryComplete();
            return Task.FromResult(channel.Reader);
        }

        internal static bool IsTerminal(string chunkJson)
            => chunkJson.Replace(" ", string.Empty).Contains("\"done\":true");

        public bool Complete(InferenceResult result) => true;

        public bool WriteChunk(InferenceChunk chunk) => true;

        public void FailForConnection(string connectionId, Exception? exception)
        {
        }
    }

    private sealed class AlwaysRoutes : IRouter
    {
        public RoutableNode? Route(
            string model,
            string? conversationKey = null,
            string? excludeConnectionId = null,
            string? capability = null,
            bool requireStreamedAttachments = false,
            bool requireStreamedSpeech = false)
            => new("conn-1", "node-1", "parity-node");
    }

    private sealed class NoProvider : IProviderDispatcher
    {
        public ProviderDecision Decide(string model, bool hasCapableNode, ProviderSteer steer) => ProviderDecision.No;

        public Task<ProviderResult> DispatchAsync(
            string kind,
            string rawJson,
            string model,
            bool stream,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// The fleet's embedding path, scripted. <see cref="TestEmbeddings"/> is the same function the
    /// node's scripted backend runs, so a difference between the two hosts can only be ours.
    /// </summary>
    private sealed class ScriptedEmbeddings(bool deterministic) : IEmbeddingDispatcher
    {
        public Task<string> DispatchEmbedAsync(string rawJson, string? modelOverride, CancellationToken cancellationToken)
            => Task.FromResult(deterministic
                ? TestEmbeddings.RespondTo(rawJson)
                : """{"model":"nomic","embeddings":[[0.1,0.2,0.3]]}""");

        public Task<float[]> EmbedSingleAsync(string text, string? model, CancellationToken cancellationToken)
            => Task.FromResult(deterministic ? TestEmbeddings.Of(text) : [0.1f, 0.2f, 0.3f]);
    }
}
