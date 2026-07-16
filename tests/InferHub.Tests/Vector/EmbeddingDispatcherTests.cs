using System.Text.Json;
using System.Threading.Channels;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests.Vector;

public class EmbeddingDispatcherTests
{
    [Fact]
    public async Task EmbedSingleAsyncReturnsFirstVectorFromResponse()
    {
        var router = new FakeRouter(model => new RoutableNode("conn-1", "node-1", "n1"));
        var dispatcher = new FakeDispatcher(_ => InferenceResult.Succeeded(
            jobId: Guid.NewGuid(),
            responseJson: JsonSerializer.Serialize(new EmbedResponse
            {
                Model = "nomic-embed-text",
                Embeddings = new() { new() { 0.1f, 0.2f, 0.3f } }
            })));

        var subject = new EmbeddingDispatcher(
            router,
            dispatcher,
            Options.Create(new VectorStoreOptions { Enabled = true, DefaultEmbeddingModel = "nomic-embed-text" }),
            TestUsage.Meter(),
            TestUsage.NoHttpContext(),
            NullLogger<EmbeddingDispatcher>.Instance);

        var vector = await subject.EmbedSingleAsync("hello", model: null, CancellationToken.None);

        Assert.Equal(3, vector.Length);
        Assert.Equal(0.1f, vector[0]);
        Assert.Equal("nomic-embed-text", router.LastModelRouted);
    }

    [Fact]
    public async Task EmbedSingleAsyncHonoursExplicitModelOverDefault()
    {
        var router = new FakeRouter(model => new RoutableNode("conn-1", "node-1", "n1"));
        var dispatcher = new FakeDispatcher(_ => InferenceResult.Succeeded(
            jobId: Guid.NewGuid(),
            responseJson: JsonSerializer.Serialize(new EmbedResponse
            {
                Embeddings = new() { new() { 1f, 2f } }
            })));

        var subject = new EmbeddingDispatcher(
            router,
            dispatcher,
            Options.Create(new VectorStoreOptions { Enabled = true, DefaultEmbeddingModel = "default" }),
            TestUsage.Meter(),
            TestUsage.NoHttpContext(),
            NullLogger<EmbeddingDispatcher>.Instance);

        _ = await subject.EmbedSingleAsync("hello", model: "explicit", CancellationToken.None);

        Assert.Equal("explicit", router.LastModelRouted);
    }

    [Fact]
    public async Task DispatchEmbedAsyncThrowsWhenNoNodeAdvertisesModel()
    {
        var router = new FakeRouter(_ => null);
        var dispatcher = new FakeDispatcher(_ => throw new InvalidOperationException("should not be called"));

        var subject = new EmbeddingDispatcher(
            router,
            dispatcher,
            Options.Create(new VectorStoreOptions { Enabled = true, DefaultEmbeddingModel = "nomic" }),
            TestUsage.Meter(),
            TestUsage.NoHttpContext(),
            NullLogger<EmbeddingDispatcher>.Instance);

        await Assert.ThrowsAsync<NoEmbeddingNodeException>(() =>
            subject.EmbedSingleAsync("hello", model: "ghost-model", CancellationToken.None));
    }

    [Fact]
    public async Task DispatchEmbedAsyncRejectsBodyWithNoModel()
    {
        var router = new FakeRouter(_ => new RoutableNode("conn-1", "node-1", "n1"));
        var dispatcher = new FakeDispatcher(_ => InferenceResult.Succeeded(Guid.NewGuid(), "{}"));

        var subject = new EmbeddingDispatcher(
            router,
            dispatcher,
            Options.Create(new VectorStoreOptions { Enabled = true }),
            TestUsage.Meter(),
            TestUsage.NoHttpContext(),
            NullLogger<EmbeddingDispatcher>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            subject.DispatchEmbedAsync("{\"input\":\"hi\"}", modelOverride: null, CancellationToken.None));
    }

    private sealed class FakeRouter(Func<string, RoutableNode?> route) : InferHub.Coordinator.Services.IRouter
    {
        public string? LastModelRouted { get; private set; }

        public RoutableNode? Route(string model, string? conversationKey = null, string? excludeConnectionId = null)
        {
            LastModelRouted = model;
            return route(model);
        }
    }

    private sealed class FakeDispatcher(Func<InferenceJob, InferenceResult> dispatch) : IDispatcher
    {
        public Task<InferenceResult> DispatchAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
        {
            return Task.FromResult(dispatch(job));
        }

        public Task<ChannelReader<InferenceChunk>> DispatchStreamAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public bool Complete(InferenceResult result) => throw new NotImplementedException();

        public bool WriteChunk(InferenceChunk chunk) => throw new NotImplementedException();

        public void FailForConnection(string connectionId, Exception? exception) => throw new NotImplementedException();
    }
}
