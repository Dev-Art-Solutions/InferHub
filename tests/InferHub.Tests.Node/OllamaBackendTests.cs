using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using OllamaClient;
using OllamaClient.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

public class OllamaBackendTests
{
    [Fact]
    public async Task ListModelsAsyncMapsOllamaModels()
    {
        var client = new FakeOllamaHttpClient(
            new GetModelsResponse
            {
                Models =
                [
                    new ModelResponse
                    {
                        Name = "llama3",
                        Digest = "digest-1",
                        Size = 123
                    }
                ]
            });
        var backend = new OllamaBackend(client, Options.Create(new OllamaOptions()), NullLogger<OllamaBackend>.Instance);

        var models = await backend.ListModelsAsync(CancellationToken.None);

        var model = Assert.Single(models!);
        Assert.Equal("llama3", model.Name);
        Assert.Equal("digest-1", model.Digest);
        Assert.Equal(123, model.SizeBytes);
    }

    /// <summary>
    /// <b>Amended in v3.36.2, and the amendment is the point.</b> This used to assert an empty
    /// list, which meant the node reported a <em>failure</em> to the coordinator <em>as data</em>:
    /// "could not ask" and "has none" arrived as the same message, and a hub cannot tell an
    /// unreachable server from a box whose weights were deleted. That is what turned v3.36's
    /// <c>503</c> naming the backend back into a <c>404 model not found</c> one refresh later.
    /// Phase-23 D1's rule, one project over: <em>"not fetched" must not be confusable with "not
    /// there"</em>.
    /// </summary>
    [Fact]
    public async Task ListModelsAsyncReturnsNullWhenOllamaCannotBeAskedRatherThanAnEmptyList()
    {
        var backend = new OllamaBackend(
            new FakeOllamaHttpClient(new InvalidOperationException("offline")),
            Options.Create(new OllamaOptions()),
            NullLogger<OllamaBackend>.Instance);

        Assert.Null(await backend.ListModelsAsync(CancellationToken.None));
    }

    /// <summary>And a server that genuinely holds nothing still says so, distinctly.</summary>
    [Fact]
    public async Task AnOllamaThatHoldsNoModelsStillReportsAnEmptyListRatherThanNull()
    {
        var backend = new OllamaBackend(
            new FakeOllamaHttpClient(new GetModelsResponse { Models = [] }),
            Options.Create(new OllamaOptions()),
            NullLogger<OllamaBackend>.Instance);

        var models = await backend.ListModelsAsync(CancellationToken.None);

        Assert.NotNull(models);
        Assert.Empty(models);
    }

    /// <summary>
    /// Phase 107. OllamaClient 2.0.1 buffered a whole stream before returning its first chunk, so an
    /// Ollama node's streamed answer arrived in one piece at the end. Here the second line is never
    /// written until the first chunk has been read — a buffering reader cannot get past the first.
    /// </summary>
    [Fact]
    public async Task AStreamedChatYieldsEachLineAsOllamaWritesIt()
    {
        var secondLine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new LineByLineHandler(
            """{"model":"llama3","message":{"role":"assistant","content":"Hel"},"done":false}""",
            secondLine.Task,
            """{"model":"llama3","message":{"role":"assistant","content":"lo"},"done":true,"eval_count":2}""");
        var backend = new OllamaBackend(
            new FakeOllamaHttpClient(new GetModelsResponse { Models = [] }),
            Options.Create(new OllamaOptions { Endpoint = "http://ollama.test:11434" }),
            NullLogger<OllamaBackend>.Instance,
            new SingleClientFactory(handler));

        await using var chunks = backend
            .StreamAsync("chat", """{"model":"llama3","messages":[],"stream":true}""", CancellationToken.None)
            .GetAsyncEnumerator();

        Assert.True(await chunks.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("Hel", chunks.Current);

        secondLine.SetResult();

        Assert.True(await chunks.MoveNextAsync());
        Assert.Contains("lo", chunks.Current);
        Assert.False(await chunks.MoveNextAsync());
        Assert.Equal("http://ollama.test:11434/api/chat", handler.Requested?.ToString());
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class LineByLineHandler(string first, Task gate, string second) : HttpMessageHandler
    {
        public Uri? Requested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = request.RequestUri;
            var pipe = new System.IO.Pipelines.Pipe();

            _ = Task.Run(async () =>
            {
                await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(first + "\n"));
                await gate;
                await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(second + "\n"));
                await pipe.Writer.CompleteAsync();
            });

            var content = new StreamContent(pipe.Reader.AsStream());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-ndjson");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class FakeOllamaHttpClient : IOllamaHttpClient
    {
        private readonly GetModelsResponse? response;
        private readonly Exception? exception;

        public FakeOllamaHttpClient(GetModelsResponse response)
        {
            this.response = response;
        }

        public FakeOllamaHttpClient(Exception exception)
        {
            this.exception = exception;
        }

        public Task<GetModelsResponse> GetModels(CancellationToken cancellationToken)
        {
            if (exception is not null)
            {
                throw exception;
            }

            return Task.FromResult(response!);
        }

        public Task<CreateModelResponse> Create(CreateModelRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public IAsyncEnumerable<CreateModelResponse> Create(CreateModelStreamRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<GenerateResponse> Generate(GenerateRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public IAsyncEnumerable<GenerateResponse> Generate(GenerateStreamRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<ChatResponse> SendChat(ChatRequest chatRequest, CancellationToken cancellationToken) => throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponse> SendChat(ChatStreamRequest chatRequest, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<CopyResponse> Copy(CopyRequest copyRequest, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<DeleteResponse> Delete(DeleteRequest deleteRequest, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<ShowResponse> Show(ShowRequest showRequest, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<PullResponse> Pull(PullRequest pullRequest, CancellationToken cancellationToken) => throw new NotImplementedException();

        public IAsyncEnumerable<PullResponse> Pull(PullStreamRequest pullRequest, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<PushResponse> Push(PushRequest pushRequest, CancellationToken cancellationToken) => throw new NotImplementedException();

        public IAsyncEnumerable<PushResponse> Push(PushStreamRequest pushRequest, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<EmbeddingsResponse> GetEmbeddings(EmbeddingsRequest embeddingsRequest, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<VersionResponse> GetVersion(CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<PsResponse> GetRunningModels(CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<EmbedResponse> Embed(EmbedRequest embedRequest, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
