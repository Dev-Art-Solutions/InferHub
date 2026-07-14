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

    private static (RetrievalPipeline Pipeline, LocalVectorStore Store, FakeEmbeddings Embeddings) NewPipeline(
        Action<VectorStoreOptions>? configure = null)
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

        var store = new LocalVectorStore(options, NullLogger<LocalVectorStore>.Instance);
        var embeddings = new FakeEmbeddings();
        var pipeline = new RetrievalPipeline(
            options,
            store,
            embeddings,
            new NullQueryRouter(),
            new Metrics(),
            NullLogger<RetrievalPipeline>.Instance);

        return (pipeline, store, embeddings);
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
}
