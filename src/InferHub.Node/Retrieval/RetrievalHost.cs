using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ingestion;
using InferHub.Shared.Vector;
using InferHub.Shared.Vector.Qdrant;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Retrieval;

/// <summary>
/// The corpus, started and stopped while the node keeps serving (phase 44, D3). <b>The only place on
/// a node that constructs a vector store.</b>
/// </summary>
/// <remarks>
/// <para>
/// Phase 38 built the corpus in DI, which was right when the only way to have one was to boot with
/// <c>LocalApi:Retrieval:Enabled=true</c>. From v3.12 a coordinator can assign one to a running node,
/// and DI cannot answer that: a singleton is constructed once and ASP.NET cannot map an endpoint
/// after the application has started. So the store, the pipelines and the document index live here,
/// behind <see cref="StartCorpusAsync"/> / <see cref="StopCorpusAsync"/>, and the routes are mapped
/// unconditionally and answer <b>501</b> while nothing is running.
/// </para>
/// <para>
/// <b>Considered and rejected: the node restarts itself to apply retrieval config.</b> It is much
/// less code. It also kills in-flight inference, turns a wrong config into a restart loop, and makes
/// a hub instruction indistinguishable from a crash in an operator's logs — which is phase-43 D6,
/// already decided.
/// </para>
/// <para>
/// <b>A start that fails leaves no corpus at all.</b> Not a half-started one that answers some
/// queries: an unreachable Qdrant, a bad dimension or an unresolvable credential produces a refusal
/// the node reports and a node that still serves chat. Retrieval that works for four collections out
/// of five, silently, is the phase-31 D4 failure — confident, fluent and missing the context that
/// mattered.
/// </para>
/// </remarks>
public sealed class RetrievalHost(
    IServiceProvider services,
    IOptions<LocalRetrievalOptions> options,
    ILogger<RetrievalHost> logger) : IHostedService, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private RunningCorpus? current;
    private string? lastError;
    private int disposed;

    /// <summary>The corpus, or null. Read on every retrieval request, so it is a volatile field and not a lock.</summary>
    public RunningCorpus? Current => Volatile.Read(ref current);

    /// <summary>What this node last failed to start, if anything. Reported to the hub (D6).</summary>
    public string? LastError => Volatile.Read(ref lastError);

    /// <summary>
    /// Takes a lease on the running corpus, or returns null when there is none. A lease is what
    /// <see cref="StopCorpusAsync"/> drains: a request that is already retrieving finishes against
    /// the store it started on, rather than faulting because an operator switched a profile.
    /// </summary>
    public CorpusLease? TryLease()
    {
        var corpus = Volatile.Read(ref current);
        return corpus is null ? null : corpus.TryLease();
    }

    /// <summary>
    /// Solo mode's path, unchanged in behaviour: a node configured with its own corpus brings it up
    /// at startup and is the only authority for it, exactly as in v3.10. A failure here is a failure
    /// to <em>start the node</em>, because the operator asked for grounding explicitly and a node
    /// that quietly answers ungrounded is phase-38 D1's whole argument.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value;

        if (!configured.Enabled)
        {
            return;
        }

        var request = new CorpusRequest(
            configured.Provider,
            configured.Url,
            configured.CredentialRef,
            Collections: Array.Empty<string>(),
            configured.DefaultEmbeddingModel,
            Source: CorpusRequest.LocalConfiguration);

        var result = await StartCorpusAsync(request, cancellationToken);

        if (!result.Started)
        {
            throw new InvalidOperationException(
                $"{LocalRetrievalOptions.SectionName}:{nameof(LocalRetrievalOptions.Enabled)} is true but the corpus could not be started: {result.Error}");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => StopCorpusAsync(cancellationToken);

    /// <summary>
    /// Brings a corpus up, replacing whatever was running. Never throws for a bad request — a node
    /// that fell over on a hub instruction would be a coordinator's denial of service against its own
    /// fleet (phase-43 D1) — so every failure comes back as <see cref="CorpusStartResult.Error"/>.
    /// </summary>
    public async Task<CorpusStartResult> StartCorpusAsync(CorpusRequest request, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            var configured = options.Value;
            var provider = string.IsNullOrWhiteSpace(request.Provider) ? configured.Provider : request.Provider!;

            if (VectorStoreProviderExtensions.IsPostgres(provider))
            {
                return Failed(
                    "a node cannot run the 'postgres' vector provider: Npgsql is scoped to the coordinator by name (design rule 5). Use 'local' or 'qdrant'.");
            }

            var qdrant = VectorStoreProviderExtensions.IsQdrant(provider);

            if (!qdrant && !string.Equals(provider.Trim(), VectorStoreProviderExtensions.Local, StringComparison.OrdinalIgnoreCase))
            {
                return Failed($"unknown vector provider '{provider}'; a node runs 'local' or 'qdrant'.");
            }

            string? secret = null;

            if (!string.IsNullOrWhiteSpace(request.CredentialRef))
            {
                // D4: the hub names a credential and this box resolves it. A name we do not have is
                // a refusal, never a fall back to an unauthenticated connection to somebody's engine.
                if (!configured.TryResolveCredential(request.CredentialRef, out var resolved))
                {
                    return Failed(
                        $"credential '{request.CredentialRef}' is not configured on this node; set {LocalRetrievalOptions.SectionName}:{nameof(LocalRetrievalOptions.Credentials)}:{request.CredentialRef} (or the matching environment variable) on the box.");
                }

                secret = resolved;
            }

            var url = string.IsNullOrWhiteSpace(request.Url) ? configured.Url : request.Url;

            if (qdrant && string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(configured.Qdrant.Url))
            {
                return Failed("the 'qdrant' provider needs a url, and neither the profile nor this node's configuration has one.");
            }

            var storeOptions = configured.ToVectorStoreOptions(provider, url, secret);

            if (!string.IsNullOrWhiteSpace(request.EmbeddingModel))
            {
                storeOptions.DefaultEmbeddingModel = request.EmbeddingModel!;
            }

            // Stop first, and only then build: two stores over one data directory is two writers over
            // one append log, and the second one wins in a way nobody can reconstruct afterwards.
            await StopCurrentAsync(cancellationToken);

            RunningCorpus corpus;

            try
            {
                corpus = await BuildAsync(provider, storeOptions, request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The node is shutting down, or the caller gave up. Not a refusal — nobody is left
                // to read one.
                throw;
            }
            catch (Exception ex)
            {
                // Everything else is a refusal, including an HttpClient timeout — which arrives as a
                // TaskCanceledException and would otherwise escape as an exception nobody turns into
                // a per-item refusal. An engine that is merely slow to refuse connections is the most
                // ordinary way for this to fail.
                return Failed(ex.Message);
            }

            Volatile.Write(ref current, corpus);
            Volatile.Write(ref lastError, null);

            logger.LogInformation(
                "Retrieval is on ({Source}): provider {Provider}, embedding model {Model}{Where}. This node is the authority for {Collections}.",
                request.Source,
                provider,
                storeOptions.DefaultEmbeddingModel,
                qdrant ? $", qdrant at {storeOptions.Qdrant.Url}" : $", corpus at {Path.GetFullPath(storeOptions.DataDirectory)}",
                corpus.Collections.Count == 0 ? "the collections it holds" : string.Join(", ", corpus.Collections));

            WarnIfRemoteAndUnauthenticated(qdrant, storeOptions, secret);

            return new CorpusStartResult(Started: true, Error: null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Takes the corpus down, draining what is already retrieving against it.</summary>
    public async Task StopCorpusAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            await StopCurrentAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// What the hub is told (D6). It is what this node knows about its own corpus — never the answer
    /// to a query the hub ran against it.
    /// </summary>
    public async Task<NodeCorpusState> StateAsync(string nodeId, CancellationToken cancellationToken)
    {
        var corpus = Volatile.Read(ref current);
        var error = Volatile.Read(ref lastError);

        if (corpus is null)
        {
            return new NodeCorpusState(
                nodeId,
                Enabled: error is not null,
                Provider: options.Value.Provider,
                Status: error is null ? NodeCorpusState.Stopped : NodeCorpusState.Failed,
                Collections: Array.Empty<NodeCorpusCollection>(),
                Error: error,
                DateTimeOffset.UtcNow);
        }

        IReadOnlyList<NodeCorpusCollection> collections;

        try
        {
            var listed = await corpus.Store.ListCollectionsAsync(cancellationToken);
            collections = listed
                .Select(c => new NodeCorpusCollection(c.Name, c.Dimension, c.RecordCount))
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The corpus is running; the engine did not answer this one call. Reporting the counts as
            // zero would read as an empty corpus, which is a different and worse claim.
            logger.LogDebug(ex, "Could not list collections for the corpus report");

            return new NodeCorpusState(
                nodeId,
                Enabled: true,
                corpus.Provider,
                NodeCorpusState.Failed,
                Array.Empty<NodeCorpusCollection>(),
                ex.Message,
                DateTimeOffset.UtcNow);
        }

        return new NodeCorpusState(
            nodeId,
            Enabled: true,
            corpus.Provider,
            NodeCorpusState.Running,
            collections,
            Error: null,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Idempotent on purpose: this is registered both as itself and as an
    /// <see cref="IHostedService"/>, so the container disposes it twice, and a second stop over a
    /// disposed gate would fail every host teardown in the suite rather than the one thing that was
    /// actually wrong.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        await StopCorpusAsync(CancellationToken.None);
        gate.Dispose();
    }

    /// <summary>
    /// The one construction site. Everything above <see cref="IVectorStore"/> is the shared stack —
    /// the same pipelines the coordinator runs (phase-38 D2), which is why a dozen retrieval
    /// decisions cannot drift between the two hosts.
    /// </summary>
    private async Task<RunningCorpus> BuildAsync(
        string provider,
        VectorStoreOptions storeOptions,
        CorpusRequest request,
        CancellationToken cancellationToken)
    {
        var qdrant = VectorStoreProviderExtensions.IsQdrant(provider);

        IVectorStore store;
        IDisposable? disposable = null;
        HttpClient? http = null;

        if (qdrant)
        {
            http = QdrantClient.Configure(
                new HttpClient(),
                storeOptions.Qdrant.Url,
                storeOptions.Qdrant.ApiKey,
                storeOptions.Qdrant.TimeoutSeconds);

            var qdrantStore = new QdrantVectorStore(
                new QdrantClient(http),
                storeOptions,
                new NodeVectorLog<QdrantVectorStore>(services.GetRequiredService<ILogger<QdrantVectorStore>>()));

            // One round trip that is also the reachability probe, exactly as QdrantBootstrapper does
            // on the hub: an engine that is not there must fail the *start*, with the address in the
            // message, rather than every query an hour later.
            try
            {
                await qdrantStore.LoadRegistryCacheAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                http.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                http.Dispose();

                // A timeout arrives here as a TaskCanceledException with the caller's token
                // untouched; it is an unreachable engine like any other, and the address is what an
                // operator needs to read.
                throw new InvalidOperationException(
                    $"could not reach Qdrant at {storeOptions.Qdrant.Url}: {ex.Message}", ex);
            }

            store = qdrantStore;
        }
        else
        {
            var local = new LocalVectorStore(
                storeOptions,
                new NodeVectorLog<LocalVectorStore>(services.GetRequiredService<ILogger<LocalVectorStore>>()));

            store = local;
            disposable = local;
        }

        // The embedding path is the node's own backend, through the seam ingestion already had
        // (phase-38 D6). A corpus that names its own model gets it pinned here rather than written
        // into the node's options graph, so two corpora in a node's lifetime cannot bleed into each
        // other and the box's own default survives them both.
        var embeddings = string.IsNullOrWhiteSpace(request.EmbeddingModel)
            ? services.GetRequiredService<IEmbeddingDispatcher>()
            : new PinnedEmbeddingModel(services.GetRequiredService<IEmbeddingDispatcher>(), request.EmbeddingModel!);

        var reranker = services.GetRequiredService<IReranker>();
        var metrics = services.GetRequiredService<IRetrievalMetrics>();
        var router = services.GetRequiredService<IVectorQueryRouter>();

        var retrieval = new RetrievalPipeline(
            storeOptions,
            store,
            embeddings,
            router,
            reranker,
            metrics,
            new NodeVectorLog<RetrievalPipeline>(services.GetRequiredService<ILogger<RetrievalPipeline>>()));

        var documents = new DocumentIndex(store);

        var ingestion = new IngestionPipeline(
            store,
            documents,
            services.GetRequiredService<TextExtractor>(),
            embeddings,
            options.Value.Ingestion,
            storeOptions,
            metrics,
            new NodeVectorLog<IngestionPipeline>(services.GetRequiredService<ILogger<IngestionPipeline>>()));

        return new RunningCorpus(
            provider,
            store,
            retrieval,
            ingestion,
            documents,
            embeddings,
            request.Collections ?? Array.Empty<string>(),
            disposable,
            http,
            logger);
    }

    /// <summary>
    /// Phase-35 D4, on a node. A non-loopback engine with no credential <b>warns and proceeds</b>:
    /// that is the operator's own network, and refusing would be us overruling them about it. The
    /// asymmetry with the hard refusals above is the line the whole track draws — refuse when the risk
    /// is somebody else's, warn when it is theirs.
    /// </summary>
    private void WarnIfRemoteAndUnauthenticated(bool qdrant, VectorStoreOptions storeOptions, string? secret)
    {
        if (!qdrant || !string.IsNullOrWhiteSpace(secret) || !string.IsNullOrWhiteSpace(storeOptions.Qdrant.ApiKey))
        {
            return;
        }

        if (!Uri.TryCreate(storeOptions.Qdrant.Url, UriKind.Absolute, out var uri) || uri.IsLoopback)
        {
            return;
        }

        logger.LogWarning(
            "Qdrant at {Url} is not loopback and this corpus has no credential. Anything that can reach that address can read and delete your vectors and the chunk text stored with them. Name a credential in the profile and set {Key} on this node.",
            storeOptions.Qdrant.Url,
            $"{LocalRetrievalOptions.SectionName}:{nameof(LocalRetrievalOptions.Credentials)}");
    }

    private async Task StopCurrentAsync(CancellationToken cancellationToken)
    {
        var running = Volatile.Read(ref current);

        if (running is null)
        {
            return;
        }

        Volatile.Write(ref current, null);
        await running.DrainAndDisposeAsync(cancellationToken);

        logger.LogInformation("Retrieval is off on this node; the corpus has been stopped and its routes answer 501 again.");
    }

    private CorpusStartResult Failed(string error)
    {
        Volatile.Write(ref lastError, error);
        logger.LogWarning("Could not start the corpus: {Error}", error);
        return new CorpusStartResult(Started: false, error);
    }

    /// <summary>
    /// The embedding model an assigned corpus named, applied only where the caller did not name one
    /// itself. A request that says which model to embed with still wins — this is a default for the
    /// corpus, not an override of the API.
    /// </summary>
    private sealed class PinnedEmbeddingModel(IEmbeddingDispatcher inner, string model) : IEmbeddingDispatcher
    {
        public Task<string> DispatchEmbedAsync(string rawJson, string? modelOverride, CancellationToken cancellationToken) =>
            inner.DispatchEmbedAsync(rawJson, string.IsNullOrWhiteSpace(modelOverride) ? model : modelOverride, cancellationToken);

        public Task<float[]> EmbedSingleAsync(string text, string? requested, CancellationToken cancellationToken) =>
            inner.EmbedSingleAsync(text, string.IsNullOrWhiteSpace(requested) ? model : requested, cancellationToken);
    }
}

/// <summary>What to bring up. Everything a corpus needs and nothing about where its bytes land.</summary>
public sealed record CorpusRequest(
    string? Provider,
    string? Url,
    string? CredentialRef,
    IReadOnlyList<string>? Collections,
    string? EmbeddingModel,
    string Source)
{
    public const string LocalConfiguration = "this node's configuration";

    public const string Profile = "coordinator profile";
}

public sealed record CorpusStartResult(bool Started, string? Error);

/// <summary>
/// A corpus and the pipelines over it, plus the in-flight count that makes stopping it a drain
/// rather than a fault.
/// </summary>
public sealed class RunningCorpus(
    string provider,
    IVectorStore store,
    RetrievalPipeline retrieval,
    IngestionPipeline ingestion,
    DocumentIndex documents,
    IEmbeddingDispatcher embeddings,
    IReadOnlyList<string> collections,
    IDisposable? disposable,
    HttpClient? http,
    ILogger logger)
{
    private readonly object sync = new();
    private int inFlight;
    private bool closed;

    public string Provider => provider;

    public IVectorStore Store => store;

    public RetrievalPipeline Retrieval => retrieval;

    public IngestionPipeline Ingestion => ingestion;

    public DocumentIndex Documents => documents;

    /// <summary>
    /// The corpus's own embedding path. It belongs to the corpus rather than to DI because an
    /// assigned corpus may name its own embedding model, and two corpora in a node's lifetime must
    /// not share one.
    /// </summary>
    public IEmbeddingDispatcher Embeddings => embeddings;

    /// <summary>The collections the hub assigned to this node, if it named any.</summary>
    public IReadOnlyList<string> Collections => collections;

    internal CorpusLease? TryLease()
    {
        lock (sync)
        {
            if (closed)
            {
                return null;
            }

            inFlight++;
        }

        return new CorpusLease(this);
    }

    internal void Release()
    {
        lock (sync)
        {
            inFlight--;
        }
    }

    /// <summary>
    /// Stops accepting new work, waits for what is running, then disposes. The wait is bounded: a
    /// retrieval wedged on an unreachable engine must not hold a profile change open forever, and a
    /// disposed store under a stuck request fails that one request rather than the node.
    /// </summary>
    internal async Task DrainAndDisposeAsync(CancellationToken cancellationToken)
    {
        lock (sync)
        {
            closed = true;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (sync)
            {
                if (inFlight == 0)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(50, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        int remaining;
        lock (sync)
        {
            remaining = inFlight;
        }

        if (remaining > 0)
        {
            logger.LogWarning(
                "Stopped a corpus with {Count} retrievals still in flight after the drain window; they will fail rather than hold the node.",
                remaining);
        }

        disposable?.Dispose();
        http?.Dispose();
    }
}

/// <summary>Held for the length of one retrieval. Disposing it is what lets a stop finish.</summary>
public sealed class CorpusLease(RunningCorpus corpus) : IDisposable
{
    private int released;

    public RunningCorpus Corpus => corpus;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) == 0)
        {
            corpus.Release();
        }
    }
}
