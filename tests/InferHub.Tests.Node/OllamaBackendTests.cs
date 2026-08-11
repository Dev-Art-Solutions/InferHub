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

        var model = Assert.Single(models);
        Assert.Equal("llama3", model.Name);
        Assert.Equal("digest-1", model.Digest);
        Assert.Equal(123, model.SizeBytes);
    }

    [Fact]
    public async Task ListModelsAsyncReturnsEmptyWhenOllamaFails()
    {
        var backend = new OllamaBackend(
            new FakeOllamaHttpClient(new InvalidOperationException("offline")),
            Options.Create(new OllamaOptions()),
            NullLogger<OllamaBackend>.Instance);

        var models = await backend.ListModelsAsync(CancellationToken.None);

        Assert.Empty(models);
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
