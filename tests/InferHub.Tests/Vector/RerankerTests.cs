using System.Text.Json;
using System.Threading.Channels;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests.Vector;

public class RerankerTests
{
    private static VectorMatch Chunk(string id, string text) =>
        new(id, 0.0, JsonSerializer.SerializeToElement(new { text }), null);

    [Fact]
    public async Task ReordersCandidatesByModelScores()
    {
        // Scores put passage 3 first, then 1, then 2 -> expected order c, a, b.
        var reranker = NewReranker(ScoresResponse([3, 1, 8]));
        var candidates = new[] { Chunk("a", "alpha"), Chunk("b", "beta"), Chunk("c", "gamma") };

        var result = await reranker.RerankAsync("q", candidates, "llama3", CancellationToken.None);

        Assert.Equal(["c", "a", "b"], result.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task TimeoutPreservesOriginalOrder()
    {
        // The node never answers within the 1s rerank deadline; the reranker's own CTS trips and the
        // original (un-reranked) order is returned.
        var reranker = new LlmReranker(
            new StubRouter(routeReturnsNode: true),
            new StubDispatcher(async (job, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return InferenceResult.Succeeded(job.JobId, MessageJson("[9, 1]"));
            }),
            Options.Create(new VectorStoreOptions { Retrieval = { RerankTimeoutSeconds = 1 } }),
            NullLogger<LlmReranker>.Instance);
        var candidates = new[] { Chunk("a", "alpha"), Chunk("b", "beta") };

        var result = await reranker.RerankAsync("q", candidates, "llama3", CancellationToken.None);

        Assert.Equal(["a", "b"], result.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task UnparseableAnswerPreservesOriginalOrder()
    {
        var reranker = NewReranker(MessageResponse("I think the second one is best, actually."));
        var candidates = new[] { Chunk("a", "alpha"), Chunk("b", "beta") };

        var result = await reranker.RerankAsync("q", candidates, "llama3", CancellationToken.None);

        Assert.Equal(["a", "b"], result.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task NoNodeForModelPreservesOriginalOrder()
    {
        var reranker = new LlmReranker(
            new StubRouter(routeReturnsNode: false),
            new StubDispatcher((_, _) => throw new InvalidOperationException("should not be called")),
            Options.Create(new VectorStoreOptions()),
            NullLogger<LlmReranker>.Instance);
        var candidates = new[] { Chunk("a", "alpha"), Chunk("b", "beta") };

        var result = await reranker.RerankAsync("q", candidates, "llama3", CancellationToken.None);

        Assert.Equal(["a", "b"], result.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task SingleCandidateShortCircuitsWithoutDispatch()
    {
        var reranker = new LlmReranker(
            new StubRouter(routeReturnsNode: true),
            new StubDispatcher((_, _) => throw new InvalidOperationException("should not be called")),
            Options.Create(new VectorStoreOptions()),
            NullLogger<LlmReranker>.Instance);

        var result = await reranker.RerankAsync("q", new[] { Chunk("a", "alpha") }, "llama3", CancellationToken.None);

        Assert.Equal(["a"], result.Select(r => r.Id).ToArray());
    }

    [Theory]
    [InlineData("[3, 1, 8]", 3, new[] { 3.0, 1.0, 8.0 })]
    [InlineData("Here are the scores: [5, 5, 5].", 3, new[] { 5.0, 5.0, 5.0 })]
    public void ParseScoresExtractsArray(string content, int expected, double[] want)
    {
        var scores = LlmReranker.ParseScores(content, expected);
        Assert.NotNull(scores);
        Assert.Equal(want, scores!);
    }

    [Theory]
    [InlineData("[1, 2]", 3)]        // wrong length -> reject rather than misalign
    [InlineData("no array here", 2)]
    [InlineData("[1, \"x\"]", 2)]    // non-numeric element
    public void ParseScoresRejectsAmbiguousOutput(string content, int expected)
    {
        Assert.Null(LlmReranker.ParseScores(content, expected));
    }

    [Fact]
    public void BuildPromptListsEveryPassage()
    {
        var prompt = LlmReranker.BuildPrompt("what is x?", new[] { Chunk("a", "alpha text"), Chunk("b", "beta text") });
        Assert.Contains("PASSAGE 1: alpha text", prompt);
        Assert.Contains("PASSAGE 2: beta text", prompt);
        Assert.Contains("QUESTION: what is x?", prompt);
    }

    private static LlmReranker NewReranker(Func<InferenceJob, InferenceResult> respond, Action<VectorStoreOptions>? configure = null)
    {
        var opts = new VectorStoreOptions();
        configure?.Invoke(opts);
        return new LlmReranker(
            new StubRouter(routeReturnsNode: true),
            new StubDispatcher((job, _) => Task.FromResult(respond(job))),
            Options.Create(opts),
            NullLogger<LlmReranker>.Instance);
    }

    private static Func<InferenceJob, InferenceResult> ScoresResponse(int[] scores)
        => job => InferenceResult.Succeeded(job.JobId, MessageJson(JsonSerializer.Serialize(scores)));

    private static Func<InferenceJob, InferenceResult> MessageResponse(string content)
        => job => InferenceResult.Succeeded(job.JobId, MessageJson(content));

    private static string MessageJson(string content)
    {
        var response = new ChatResponse { Message = new ChatMessage { Role = "assistant", Content = content } };
        return JsonSerializer.Serialize(response);
    }

    private sealed class StubRouter(bool routeReturnsNode) : IRouter
    {
        public RoutableNode? Route(string model, string? conversationKey = null, string? excludeConnectionId = null)
            => routeReturnsNode ? new RoutableNode("conn", "node-1", "node") : null;
    }

    private sealed class StubDispatcher(Func<InferenceJob, CancellationToken, Task<InferenceResult>> respond) : IDispatcher
    {
        public async Task<InferenceResult> DispatchAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
        {
            // Honour cancellation so the timeout test can trip us — the reranker's linked CTS cancels
            // this token when its deadline passes.
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return await respond(job, cancellationToken);
        }

        public Task<ChannelReader<InferenceChunk>> DispatchStreamAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public bool Complete(InferenceResult result) => true;
        public bool WriteChunk(InferenceChunk chunk) => true;
        public void FailForConnection(string connectionId, Exception? exception) { }
    }
}
