using System.Text.Json;
using System.Threading.Channels;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests.Vector;

public class CrossEncoderRerankerTests
{
    private static VectorMatch Chunk(string id, string text) =>
        new(id, 0.0, JsonSerializer.SerializeToElement(new { text }), null);

    [Fact]
    public async Task ReordersCandidatesByWorkerScores()
    {
        // Scores put passage c first, then a, then b.
        var reranker = NewReranker(job => ToolResult.Succeeded(job.JobId, ScoresPayload(3, 1, 8)));
        var candidates = new[] { Chunk("a", "alpha"), Chunk("b", "beta"), Chunk("c", "gamma") };

        var result = await reranker.RerankAsync("q", candidates, "bge-reranker-v2-m3", CancellationToken.None);

        Assert.Equal(["c", "a", "b"], result.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task RequestCarriesQueryAndDocumentText()
    {
        ToolJob? seen = null;
        var reranker = NewReranker(job =>
        {
            seen = job;
            return ToolResult.Succeeded(job.JobId, ScoresPayload(1, 2));
        });
        var candidates = new[] { Chunk("a", "alpha text"), Chunk("b", "beta text") };

        await reranker.RerankAsync("what is x?", candidates, "bge-reranker-v2-m3", CancellationToken.None);

        Assert.NotNull(seen);
        Assert.Equal(CapabilityKinds.Rerank, seen!.Capability);
        using var doc = JsonDocument.Parse(seen.Payload);
        Assert.Equal("what is x?", doc.RootElement.GetProperty("query").GetString());
        Assert.Equal(
            ["alpha text", "beta text"],
            doc.RootElement.GetProperty("documents").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task TimeoutPreservesOriginalOrder()
    {
        var reranker = new CrossEncoderReranker(
            new StubRouter(routeReturnsNode: true),
            new StubToolDispatcher(async (job, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return ToolResult.Succeeded(job.JobId, ScoresPayload(9, 1));
            }),
            Options.Create(new VectorStoreOptions { Retrieval = { RerankTimeoutSeconds = 1 } }),
            NullLogger<CrossEncoderReranker>.Instance);
        var candidates = new[] { Chunk("a", "alpha"), Chunk("b", "beta") };

        var result = await reranker.RerankAsync("q", candidates, "bge-reranker-v2-m3", CancellationToken.None);

        Assert.Equal(["a", "b"], result.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task WrongLengthScoreArrayPreservesOriginalOrder()
    {
        var reranker = NewReranker(job => ToolResult.Succeeded(job.JobId, ScoresPayload(1)));
        var candidates = new[] { Chunk("a", "alpha"), Chunk("b", "beta") };

        var result = await reranker.RerankAsync("q", candidates, "bge-reranker-v2-m3", CancellationToken.None);

        Assert.Equal(["a", "b"], result.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task UnparseablePayloadPreservesOriginalOrder()
    {
        var reranker = NewReranker(job => ToolResult.Succeeded(job.JobId, "{\"not-scores\": true}"));
        var candidates = new[] { Chunk("a", "alpha"), Chunk("b", "beta") };

        var result = await reranker.RerankAsync("q", candidates, "bge-reranker-v2-m3", CancellationToken.None);

        Assert.Equal(["a", "b"], result.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task WorkerErrorPreservesOriginalOrder()
    {
        // The worker's usual shape for "this is not one of my models" — e.g. a chat model landed
        // in RerankModel by mistake and never routes to the rerank tool at all, or the tool refused
        // it by name once it did.
        var reranker = NewReranker(job => ToolResult.Refused(job.JobId, "model 'llama3' is not one this worker serves", ToolErrorCodes.InvalidRequest));
        var candidates = new[] { Chunk("a", "alpha"), Chunk("b", "beta") };

        var result = await reranker.RerankAsync("q", candidates, "llama3", CancellationToken.None);

        Assert.Equal(["a", "b"], result.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task NoNodeForModelPreservesOriginalOrder()
    {
        var reranker = new CrossEncoderReranker(
            new StubRouter(routeReturnsNode: false),
            new StubToolDispatcher((_, _) => throw new InvalidOperationException("should not be called")),
            Options.Create(new VectorStoreOptions()),
            NullLogger<CrossEncoderReranker>.Instance);
        var candidates = new[] { Chunk("a", "alpha"), Chunk("b", "beta") };

        var result = await reranker.RerankAsync("q", candidates, "bge-reranker-v2-m3", CancellationToken.None);

        Assert.Equal(["a", "b"], result.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task SingleCandidateShortCircuitsWithoutDispatch()
    {
        var reranker = new CrossEncoderReranker(
            new StubRouter(routeReturnsNode: true),
            new StubToolDispatcher((_, _) => throw new InvalidOperationException("should not be called")),
            Options.Create(new VectorStoreOptions()),
            NullLogger<CrossEncoderReranker>.Instance);

        var result = await reranker.RerankAsync("q", new[] { Chunk("a", "alpha") }, "bge-reranker-v2-m3", CancellationToken.None);

        Assert.Equal(["a"], result.Select(r => r.Id).ToArray());
    }

    private static CrossEncoderReranker NewReranker(Func<ToolJob, ToolResult> respond, Action<VectorStoreOptions>? configure = null)
    {
        var opts = new VectorStoreOptions();
        configure?.Invoke(opts);
        return new CrossEncoderReranker(
            new StubRouter(routeReturnsNode: true),
            new StubToolDispatcher((job, _) => Task.FromResult(respond(job))),
            Options.Create(opts),
            NullLogger<CrossEncoderReranker>.Instance);
    }

    private static string ScoresPayload(params double[] scores)
        => JsonSerializer.Serialize(new { scores });

    private sealed class StubRouter(bool routeReturnsNode) : IRouter
    {
        public RoutableNode? Route(string model, string? conversationKey = null, string? excludeConnectionId = null, string? capability = null, bool requireStreamedAttachments = false, bool requireStreamedSpeech = false)
            => routeReturnsNode ? new RoutableNode("conn", "node-1", "node") : null;
    }

    private sealed class StubToolDispatcher(Func<ToolJob, CancellationToken, Task<ToolResult>> respond) : IToolDispatcher
    {
        public async Task<ToolResult> DispatchToolAsync(RoutableNode node, ToolJob job, CancellationToken cancellationToken)
        {
            // Honour cancellation so the timeout test can trip us — the reranker's linked CTS cancels
            // this token when its deadline passes.
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return await respond(job, cancellationToken);
        }

        public Task<ToolResult> DispatchToolAsync(RoutableNode node, ToolJob job, IProgress<ToolChunk>? progress, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<ChannelReader<ToolChunk>> DispatchToolStreamAsync(RoutableNode node, ToolJob job, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public bool CompleteTool(ToolResult result) => true;
        public bool WriteToolChunk(ToolChunk chunk) => true;
        public IDisposable RegisterUpload(Guid jobId, StreamedUpload upload) => throw new NotImplementedException();
        public IAsyncEnumerable<AttachmentChunk> ReadUploadAsync(Guid jobId, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
