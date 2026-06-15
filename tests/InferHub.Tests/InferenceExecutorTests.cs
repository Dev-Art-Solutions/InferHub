using System.Runtime.CompilerServices;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

public class InferenceExecutorTests
{
    [Fact]
    public async Task StreamAsyncWrapsBackendChunksAndMarksDone()
    {
        var backend = new FakeBackend(
            """{"response":"hel","done":false}""",
            """{"response":"lo","done":true}""");
        var executor = new InferenceExecutor(backend, NullLogger<InferenceExecutor>.Instance);
        var job = new InferenceJob(Guid.NewGuid(), "generate", "{}");

        var chunks = new List<InferenceChunk>();

        await foreach (var chunk in executor.StreamAsync(job, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Collection(
            chunks,
            chunk =>
            {
                Assert.Equal(job.JobId, chunk.JobId);
                Assert.False(chunk.Done);
            },
            chunk =>
            {
                Assert.Equal(job.JobId, chunk.JobId);
                Assert.True(chunk.Done);
                Assert.Contains("\"done\":true", chunk.ResponseJson);
            });
    }

    [Fact]
    public async Task StreamAsyncEmitsFinalDoneChunkWhenBackendEndsEarly()
    {
        var backend = new FakeBackend("""{"response":"hello","done":false}""");
        var executor = new InferenceExecutor(backend, NullLogger<InferenceExecutor>.Instance);
        var job = new InferenceJob(Guid.NewGuid(), "generate", "{}");

        var chunks = new List<InferenceChunk>();

        await foreach (var chunk in executor.StreamAsync(job, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, chunks.Count);
        Assert.True(chunks[^1].Done);
        Assert.Equal("""{"done":true}""", chunks[^1].ResponseJson);
    }

    private sealed class FakeBackend(params string[] chunks) : IInferenceBackend
    {
        public string Name => "fake";

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>());
        }

        public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async IAsyncEnumerable<string> StreamAsync(
            string kind,
            string requestJson,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return chunk;
            }
        }
    }
}
