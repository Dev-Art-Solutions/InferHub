using System.Text.Json;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Ollama;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests.Vector;

public class RetrievalPipelineTests
{
    [Fact]
    public async Task AugmentChatInjectsSystemMessageWithRetrievedContext()
    {
        var (pipeline, store, embeddings) = NewPipeline();
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f], Payload: JsonSerializer.SerializeToElement(new { text = "Sofia is Bulgaria's capital." })));
        await store.UpsertAsync("docs", new VectorUpsert("b", [0f, 1f], Payload: JsonSerializer.SerializeToElement(new { text = "Paris is the capital of France." })));
        embeddings.Vector = [1f, 0f];

        const string rawJson = """
        {"model":"llama3","messages":[{"role":"user","content":"What's the capital of Bulgaria?"}],"stream":false}
        """;
        var request = JsonSerializer.Deserialize<ChatRequest>(rawJson)!;

        var outcome = await pipeline.AugmentChatAsync(rawJson, request, new RetrievalRequest("docs", null, null), CancellationToken.None);

        Assert.True(outcome.WasAugmented);
        Assert.Contains(outcome.Sources, s => s.Id == "a");

        var augmented = JsonSerializer.Deserialize<ChatRequest>(outcome.RawJson)!;
        Assert.NotNull(augmented.Messages);
        Assert.Equal("system", augmented.Messages![0].Role);
        Assert.Contains("Sofia", augmented.Messages[0].Content);
        Assert.Equal("user", augmented.Messages[1].Role);
        Assert.Equal("What's the capital of Bulgaria?", augmented.Messages[1].Content);
    }

    [Fact]
    public async Task AugmentChatPreservesLeadingOperatorSystemMessage()
    {
        var (pipeline, store, embeddings) = NewPipeline();
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f], Payload: JsonSerializer.SerializeToElement(new { text = "context" })));
        embeddings.Vector = [1f, 0f];

        const string rawJson = """
        {"model":"llama3","messages":[{"role":"system","content":"You are helpful."},{"role":"user","content":"Hi"}]}
        """;
        var request = JsonSerializer.Deserialize<ChatRequest>(rawJson)!;

        var outcome = await pipeline.AugmentChatAsync(rawJson, request, new RetrievalRequest("docs", null, null), CancellationToken.None);

        var augmented = JsonSerializer.Deserialize<ChatRequest>(outcome.RawJson)!;
        Assert.Equal("You are helpful.", augmented.Messages![0].Content);
        Assert.Equal("system", augmented.Messages[1].Role);
        Assert.Contains("context", augmented.Messages[1].Content);
        Assert.Equal("user", augmented.Messages[2].Role);
    }

    [Fact]
    public async Task AugmentGeneratePrependsContextToPrompt()
    {
        var (pipeline, store, embeddings) = NewPipeline();
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f], Payload: JsonSerializer.SerializeToElement(new { text = "context-line" })));
        embeddings.Vector = [1f, 0f];

        const string rawJson = """
        {"model":"llama3","prompt":"Answer this:","stream":false}
        """;
        var request = JsonSerializer.Deserialize<GenerateRequest>(rawJson)!;

        var outcome = await pipeline.AugmentGenerateAsync(rawJson, request, new RetrievalRequest("docs", null, null), CancellationToken.None);

        Assert.True(outcome.WasAugmented);
        var augmented = JsonSerializer.Deserialize<GenerateRequest>(outcome.RawJson)!;
        Assert.NotNull(augmented.Prompt);
        Assert.Contains("context-line", augmented.Prompt);
        Assert.EndsWith("Answer this:", augmented.Prompt);
    }

    [Fact]
    public async Task PassthroughReturnsOriginalWhenCollectionMissing()
    {
        var (pipeline, _, embeddings) = NewPipeline(options => options.Retrieval.OnMissing = "passthrough");
        embeddings.Vector = [1f, 0f];

        const string rawJson = """
        {"model":"llama3","messages":[{"role":"user","content":"hello"}]}
        """;
        var request = JsonSerializer.Deserialize<ChatRequest>(rawJson)!;

        var outcome = await pipeline.AugmentChatAsync(rawJson, request, new RetrievalRequest("nope", null, null), CancellationToken.None);

        Assert.False(outcome.WasAugmented);
        Assert.Empty(outcome.Sources);
        Assert.Equal(rawJson, outcome.RawJson);
    }

    [Fact]
    public async Task ErrorModeThrowsWhenCollectionMissing()
    {
        var (pipeline, _, embeddings) = NewPipeline();
        embeddings.Vector = [1f, 0f];

        const string rawJson = """
        {"model":"llama3","messages":[{"role":"user","content":"hello"}]}
        """;
        var request = JsonSerializer.Deserialize<ChatRequest>(rawJson)!;

        await Assert.ThrowsAsync<RetrievalUnavailableException>(() =>
            pipeline.AugmentChatAsync(rawJson, request, new RetrievalRequest("nope", null, null), CancellationToken.None));
    }

    [Fact]
    public async Task HeaderKIsRespectedAndClampedByMaxRecords()
    {
        var (pipeline, store, embeddings) = NewPipeline(options => options.Retrieval.MaxRecords = 2);
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        for (var i = 0; i < 5; i++)
        {
            var text = $"line-{i}";
            await store.UpsertAsync("docs", new VectorUpsert($"id-{i}", [1f, i * 0.01f], Payload: JsonSerializer.SerializeToElement(new { text })));
        }
        embeddings.Vector = [1f, 0f];

        const string rawJson = """
        {"model":"llama3","messages":[{"role":"user","content":"q"}]}
        """;
        var request = JsonSerializer.Deserialize<ChatRequest>(rawJson)!;

        // Request k=10 but MaxRecords caps at 2.
        var outcome = await pipeline.AugmentChatAsync(rawJson, request, new RetrievalRequest("docs", K: 10, Model: null), CancellationToken.None);

        Assert.True(outcome.WasAugmented);
        Assert.Equal(2, outcome.Sources.Count);
    }

    [Fact]
    public async Task PassthroughWhenChatHasNoUserContent()
    {
        var (pipeline, store, _) = NewPipeline(options => options.Retrieval.OnMissing = "passthrough");
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");

        const string rawJson = """
        {"model":"llama3","messages":[{"role":"system","content":"only system"}]}
        """;
        var request = JsonSerializer.Deserialize<ChatRequest>(rawJson)!;

        var outcome = await pipeline.AugmentChatAsync(rawJson, request, new RetrievalRequest("docs", null, null), CancellationToken.None);

        Assert.False(outcome.WasAugmented);
        Assert.Equal(rawJson, outcome.RawJson);
    }

    [Fact]
    public async Task PassthroughWhenNoMatchesInCollection()
    {
        var (pipeline, store, embeddings) = NewPipeline();
        await store.CreateCollectionAsync("empty", dimension: 2, distance: "cosine");
        embeddings.Vector = [1f, 0f];

        const string rawJson = """
        {"model":"llama3","messages":[{"role":"user","content":"hi"}]}
        """;
        var request = JsonSerializer.Deserialize<ChatRequest>(rawJson)!;

        var outcome = await pipeline.AugmentChatAsync(rawJson, request, new RetrievalRequest("empty", null, null), CancellationToken.None);

        Assert.False(outcome.WasAugmented);
        Assert.Empty(outcome.Sources);
        Assert.Equal(rawJson, outcome.RawJson);
    }

    // --- Phase 24: retrieval modes & reranking -----------------------------------------------

    [Fact]
    public async Task DefaultModeIsVectorOnly_ByteIdenticalToPreV26()
    {
        // No X-InferHub-Retrieve-Mode header (Mode = null) must behave exactly like vector mode.
        var (pipeline, store, embeddings) = await SeedExactTermCorpusAsync();
        embeddings.Vector = [1f, 0f]; // nearest to the prose chunk, which does NOT contain the code

        const string rawJson = """
        {"model":"llama3","messages":[{"role":"user","content":"What does error E-4021 mean?"}]}
        """;
        var request = JsonSerializer.Deserialize<ChatRequest>(rawJson)!;

        var defaultOutcome = await pipeline.AugmentChatAsync(rawJson, request, new RetrievalRequest("docs", K: 1, Model: null), CancellationToken.None);
        var vectorOutcome = await pipeline.AugmentChatAsync(rawJson, request, new RetrievalRequest("docs", K: 1, Model: null, Mode: "vector"), CancellationToken.None);

        Assert.Equal("prose", Assert.Single(defaultOutcome.Sources).Id);
        Assert.Equal(
            vectorOutcome.Sources.Select(s => s.Id),
            defaultOutcome.Sources.Select(s => s.Id));
    }

    [Fact]
    public async Task HybridRetrievesExactTermThatVectorSearchMisses()
    {
        // The whole reason hybrid exists: an error code the embedding cannot find, but BM25 can.
        var (pipeline, store, embeddings) = await SeedExactTermCorpusAsync();
        embeddings.Vector = [1f, 0f]; // vector search prefers the prose chunk

        const string rawJson = """
        {"model":"llama3","messages":[{"role":"user","content":"What does error E-4021 mean?"}]}
        """;
        var request = JsonSerializer.Deserialize<ChatRequest>(rawJson)!;

        var vector = await pipeline.AugmentChatAsync(rawJson, request, new RetrievalRequest("docs", K: 1, Model: null, Mode: "vector"), CancellationToken.None);
        var hybrid = await pipeline.AugmentChatAsync(rawJson, request, new RetrievalRequest("docs", K: 1, Model: null, Mode: "hybrid"), CancellationToken.None);

        Assert.Equal("prose", Assert.Single(vector.Sources).Id);   // vector alone gets it wrong
        Assert.Equal("code", Assert.Single(hybrid.Sources).Id);    // hybrid recovers the exact chunk
    }

    [Fact]
    public async Task KeywordModeRanksByBm25RegardlessOfEmbedding()
    {
        var (pipeline, store, embeddings) = await SeedExactTermCorpusAsync();
        embeddings.Vector = [1f, 0f]; // would put the prose chunk first under vector search

        const string rawJson = """
        {"model":"llama3","messages":[{"role":"user","content":"error E-4021"}]}
        """;
        var request = JsonSerializer.Deserialize<ChatRequest>(rawJson)!;

        var keyword = await pipeline.AugmentChatAsync(rawJson, request, new RetrievalRequest("docs", K: 1, Model: null, Mode: "keyword"), CancellationToken.None);

        Assert.Equal("code", Assert.Single(keyword.Sources).Id);
    }

    [Fact]
    public async Task RerankerNotCalledUnlessRequested()
    {
        var reranker = new PassthroughReranker();
        var (pipeline, store, embeddings) = NewPipeline(reranker: reranker);
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f], Payload: JsonSerializer.SerializeToElement(new { text = "alpha" })));
        await store.UpsertAsync("docs", new VectorUpsert("b", [0f, 1f], Payload: JsonSerializer.SerializeToElement(new { text = "beta" })));
        embeddings.Vector = [1f, 0f];

        const string rawJson = """
        {"model":"llama3","messages":[{"role":"user","content":"q"}]}
        """;
        var request = JsonSerializer.Deserialize<ChatRequest>(rawJson)!;

        await pipeline.AugmentChatAsync(rawJson, request, new RetrievalRequest("docs", null, null), CancellationToken.None);
        Assert.False(reranker.WasCalled);
    }

    [Fact]
    public async Task RerankAppliedWhenHeaderTrue()
    {
        var (pipeline, store, embeddings) = NewPipeline(reranker: new ReversingReranker());
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f], Payload: JsonSerializer.SerializeToElement(new { text = "alpha" })));
        await store.UpsertAsync("docs", new VectorUpsert("b", [0.9f, 0.1f], Payload: JsonSerializer.SerializeToElement(new { text = "beta" })));
        embeddings.Vector = [1f, 0f]; // vector order: a, then b

        const string rawJson = """
        {"model":"llama3","messages":[{"role":"user","content":"q"}]}
        """;
        var request = JsonSerializer.Deserialize<ChatRequest>(rawJson)!;

        var outcome = await pipeline.AugmentChatAsync(rawJson, request, new RetrievalRequest("docs", K: 2, Model: null, Rerank: true), CancellationToken.None);

        // The reversing reranker flips the vector order a,b -> b,a.
        Assert.Equal(["b", "a"], outcome.Sources.Select(s => s.Id).ToArray());
    }

    [Fact]
    public async Task RerankCandidateCapLimitsWhatReachesTheReranker()
    {
        var reranker = new PassthroughReranker();
        var (pipeline, store, embeddings) = NewPipeline(
            options => { options.Retrieval.MaxRecords = 5; options.Retrieval.RerankCandidates = 2; },
            reranker);
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        for (var i = 0; i < 4; i++)
        {
            await store.UpsertAsync("docs", new VectorUpsert($"id-{i}", [1f, i * 0.01f], Payload: JsonSerializer.SerializeToElement(new { text = $"line-{i}" })));
        }
        embeddings.Vector = [1f, 0f];

        const string rawJson = """
        {"model":"llama3","messages":[{"role":"user","content":"q"}]}
        """;
        var request = JsonSerializer.Deserialize<ChatRequest>(rawJson)!;

        await pipeline.AugmentChatAsync(rawJson, request, new RetrievalRequest("docs", K: 4, Model: null, Rerank: true), CancellationToken.None);

        Assert.True(reranker.WasCalled);
        Assert.Equal(2, reranker.LastSeenIds!.Count);
    }

    [Fact]
    public async Task HybridPrefersServerSideFusionWhenTheStoreSupportsIt()
    {
        var (options, inner) = NewLocalStore();
        await inner.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await SeedExactTermCorpusInto(inner);
        var store = new HybridCapableStore(inner) { SupportsResult = true };
        var embeddings = new FakeEmbeddings { Vector = [1f, 0f] };
        var pipeline = NewPipelineOver(store, embeddings, options);

        const string rawJson = """
        {"model":"llama3","messages":[{"role":"user","content":"What does error E-4021 mean?"}]}
        """;
        var request = JsonSerializer.Deserialize<ChatRequest>(rawJson)!;

        var outcome = await pipeline.AugmentChatAsync(rawJson, request, new RetrievalRequest("docs", K: 2, Model: null, Mode: "hybrid"), CancellationToken.None);

        // The one fused server-side call was taken; the hub did not run two branches and RRF them.
        Assert.True(store.HybridCalled);
        Assert.True(outcome.WasAugmented);
    }

    [Fact]
    public async Task HybridFallsBackToHubFusionWhenTheStoreIsDenseOnly()
    {
        var (options, inner) = NewLocalStore();
        await inner.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await SeedExactTermCorpusInto(inner);
        // A store that advertises the capability interface but reports this collection dense-only.
        var store = new HybridCapableStore(inner) { SupportsResult = false };
        var embeddings = new FakeEmbeddings { Vector = [1f, 0f] };
        var pipeline = NewPipelineOver(store, embeddings, options);

        const string rawJson = """
        {"model":"llama3","messages":[{"role":"user","content":"What does error E-4021 mean?"}]}
        """;
        var request = JsonSerializer.Deserialize<ChatRequest>(rawJson)!;

        var outcome = await pipeline.AugmentChatAsync(rawJson, request, new RetrievalRequest("docs", K: 1, Model: null, Mode: "hybrid"), CancellationToken.None);

        Assert.False(store.HybridCalled);
        // Hub RRF fusion still recovers the exact-term chunk, exactly as under a plain store.
        Assert.Equal("code", Assert.Single(outcome.Sources).Id);
    }

    private static async Task<(RetrievalPipeline Pipeline, LocalVectorStore Store, FakeEmbeddings Embeddings)> SeedExactTermCorpusAsync()
    {
        var tuple = NewPipeline();
        await tuple.Store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await SeedExactTermCorpusInto(tuple.Store);
        return tuple;
    }

    private static async Task SeedExactTermCorpusInto(LocalVectorStore store)
    {
        // "prose" is semantically near the query embedding but never mentions the code.
        await store.UpsertAsync("docs", new VectorUpsert("prose", [1f, 0f],
            Payload: JsonSerializer.SerializeToElement(new { text = "General information about the payment subsystem and its many features and error handling." })));
        // "code" is semantically far but contains the literal identifier the user asked for.
        await store.UpsertAsync("docs", new VectorUpsert("code", [0f, 1f],
            Payload: JsonSerializer.SerializeToElement(new { text = "Error E-4021 indicates a checksum mismatch on the uploaded batch." })));
    }

    private static (IOptions<VectorStoreOptions> Options, LocalVectorStore Store) NewLocalStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inferhub-retrieval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var options = Options.Create(new VectorStoreOptions
        {
            Enabled = true,
            DataDirectory = dir,
            Distance = "cosine",
            DefaultEmbeddingModel = "test-embed"
        });
        return (options, new LocalVectorStore(options.Value, NullVectorLog.Instance));
    }

    private static RetrievalPipeline NewPipelineOver(IVectorStore store, IEmbeddingDispatcher embeddings, IOptions<VectorStoreOptions> options)
        => new(options.Value, store, embeddings, new NullQueryRouter(), new PassthroughReranker(), new Metrics(), NullVectorLog.Instance);

    private static (RetrievalPipeline Pipeline, LocalVectorStore Store, FakeEmbeddings Embeddings) NewPipeline(
        Action<VectorStoreOptions>? configure = null,
        IReranker? reranker = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "inferhub-retrieval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var opts = new VectorStoreOptions
        {
            Enabled = true,
            DataDirectory = dir,
            Distance = "cosine",
            DefaultEmbeddingModel = "test-embed"
        };
        configure?.Invoke(opts);
        var options = Options.Create(opts);

        var store = new LocalVectorStore(options.Value, NullVectorLog.Instance);
        var embeddings = new FakeEmbeddings();
        var pipeline = new RetrievalPipeline(
            options.Value,
            store,
            embeddings,
            new NullQueryRouter(),
            reranker ?? new PassthroughReranker(),
            new Metrics(),
            NullVectorLog.Instance);

        return (pipeline, store, embeddings);
    }

    // Records the order it was asked to rerank, then returns the candidates unchanged — enough to
    // prove the pipeline hands the reranker the fused pool only when reranking is switched on.
    private sealed class PassthroughReranker : IReranker
    {
        public List<string>? LastSeenIds { get; private set; }
        public bool WasCalled { get; private set; }

        public Task<IReadOnlyList<VectorMatch>> RerankAsync(string query, IReadOnlyList<VectorMatch> candidates, string? model, CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastSeenIds = candidates.Select(c => c.Id).ToList();
            return Task.FromResult(candidates);
        }
    }

    // Reverses the candidate order, so a test can assert the pipeline actually applied the reranker's
    // verdict rather than the incoming order.
    private sealed class ReversingReranker : IReranker
    {
        public Task<IReadOnlyList<VectorMatch>> RerankAsync(string query, IReadOnlyList<VectorMatch> candidates, string? model, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<VectorMatch>>(candidates.Reverse().ToArray());
    }

    private sealed class FakeEmbeddings : IEmbeddingDispatcher
    {
        public float[] Vector { get; set; } = Array.Empty<float>();

        public Task<string> DispatchEmbedAsync(string rawJson, string? modelOverride, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<float[]> EmbedSingleAsync(string text, string? model, CancellationToken cancellationToken)
            => Task.FromResult(Vector);
    }

    private sealed class NullQueryRouter : IVectorQueryRouter
    {
        public Task<IReadOnlyList<VectorMatch>?> TryQueryOnNodeAsync(string collection, VectorQuery query, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<VectorMatch>?>(null);
    }

    // A store that advertises server-side hybrid fusion (like Qdrant) by wrapping a local store. It
    // records whether the pipeline routed hybrid to SearchHybridAsync, and can report a collection as
    // dense-only via SupportsResult to exercise the hub-fusion fallback.
    private sealed class HybridCapableStore(IVectorStore inner) : IVectorStore, IServerSideHybridSearch
    {
        public bool HybridCalled { get; private set; }
        public bool SupportsResult { get; init; } = true;

        public Task<bool> SupportsServerSideHybridAsync(string collection, CancellationToken cancellationToken = default)
            => Task.FromResult(SupportsResult);

        public async Task<IReadOnlyList<VectorMatch>> SearchHybridAsync(
            string collection, float[] denseVector, string queryText, int k,
            IReadOnlyDictionary<string, string>? filter, CancellationToken cancellationToken = default)
        {
            HybridCalled = true;
            // Stand in for engine-side fusion: the lexical branch alone is enough to prove the fork
            // was taken and to augment the prompt.
            return await inner.SearchKeywordAsync(collection, queryText, k, cancellationToken);
        }

        public Task<CollectionInfo> CreateCollectionAsync(string name, int dimension, string? distance, CancellationToken cancellationToken = default)
            => inner.CreateCollectionAsync(name, dimension, distance, cancellationToken);
        public Task<bool> DropCollectionAsync(string name, CancellationToken cancellationToken = default)
            => inner.DropCollectionAsync(name, cancellationToken);
        public Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(CancellationToken cancellationToken = default)
            => inner.ListCollectionsAsync(cancellationToken);
        public Task<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken = default)
            => inner.GetCollectionAsync(name, cancellationToken);
        public Task<VectorRecord> UpsertAsync(string collection, VectorUpsert upsert, CancellationToken cancellationToken = default)
            => inner.UpsertAsync(collection, upsert, cancellationToken);
        public Task<VectorRecord?> GetAsync(string collection, string id, CancellationToken cancellationToken = default)
            => inner.GetAsync(collection, id, cancellationToken);
        public Task<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(collection, id, cancellationToken);
        public Task<IReadOnlyList<VectorMatch>> QueryAsync(string collection, VectorQuery query, CancellationToken cancellationToken = default)
            => inner.QueryAsync(collection, query, cancellationToken);
        public Task<IReadOnlyList<VectorMatch>> SearchKeywordAsync(string collection, string query, int k, CancellationToken cancellationToken = default)
            => inner.SearchKeywordAsync(collection, query, k, cancellationToken);
        public Task<IReadOnlyList<VectorEntry>> ScanAsync(string collection, IReadOnlyDictionary<string, string>? filter, int limit, string? afterId = null, CancellationToken cancellationToken = default)
            => inner.ScanAsync(collection, filter, limit, afterId, cancellationToken);
        public Task<IReadOnlyList<VectorRecord>> ScanWithVectorsAsync(string collection, IReadOnlyDictionary<string, string>? filter, int limit, string? afterId = null, CancellationToken cancellationToken = default)
            => inner.ScanWithVectorsAsync(collection, filter, limit, afterId, cancellationToken);
        public Task<int> DeleteByFilterAsync(string collection, IReadOnlyDictionary<string, string> filter, CancellationToken cancellationToken = default)
            => inner.DeleteByFilterAsync(collection, filter, cancellationToken);
    }
}
