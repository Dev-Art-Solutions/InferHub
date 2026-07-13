using System.Runtime.CompilerServices;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using InferHub.Node.Vector;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

public class InferenceExecutorTests : IDisposable
{
    private readonly string _replicaDir = Path.Combine(Path.GetTempPath(), "inferhub-ie-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_replicaDir)) Directory.Delete(_replicaDir, recursive: true);
    }

    private InferenceExecutor BuildExecutor(IInferenceBackend backend)
    {
        var replicas = new ReplicaStore(
            Options.Create(new VectorReplicaOptions { ReplicaDirectory = _replicaDir }),
            NullLogger<ReplicaStore>.Instance);
        return new InferenceExecutor(backend, replicas, NullLogger<InferenceExecutor>.Instance);
    }

    [Fact]
    public async Task StreamAsyncWrapsBackendChunksAndMarksDone()
    {
        var backend = new FakeBackend(
            """{"response":"hel","done":false}""",
            """{"response":"lo","done":true}""");
        var executor = BuildExecutor(backend);
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
    public async Task RunAsyncRoutesEmbedKindToBackendEmbed()
    {
        var backend = new RecordingBackend(embedResponse: """{"model":"nomic","embeddings":[[0.1,0.2]]}""");
        var executor = BuildExecutor(backend);
        var job = new InferenceJob(Guid.NewGuid(), "embed", """{"model":"nomic","input":"hi"}""");

        var result = await executor.RunAsync(job, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("""{"model":"nomic","embeddings":[[0.1,0.2]]}""", result.ResponseJson);
        Assert.Equal("""{"model":"nomic","input":"hi"}""", backend.LastEmbedRequest);
    }

    [Fact]
    public async Task RunAsyncVectorQueryDispatchesToReplicaStore()
    {
        var replicas = new ReplicaStore(
            Options.Create(new VectorReplicaOptions { ReplicaDirectory = _replicaDir }),
            NullLogger<ReplicaStore>.Instance);
        replicas.Apply(new InferHub.Shared.Vector.Replication.VectorReplicaAssignment(
            "docs",
            Dimension: 2,
            Distance: "cosine",
            Records: new[]
            {
                new InferHub.Shared.Vector.VectorRecord("a", [1f, 0f], null, null, 1, DateTimeOffset.UtcNow),
                new InferHub.Shared.Vector.VectorRecord("b", [0f, 1f], null, null, 2, DateTimeOffset.UtcNow)
            },
            LastSeq: 2));

        var executor = new InferenceExecutor(new RecordingBackend(), replicas, NullLogger<InferenceExecutor>.Instance);
        var requestJson = """{"collection":"docs","vector":[1.0,0.0],"k":1}""";
        var job = new InferenceJob(Guid.NewGuid(), "vector-query", requestJson);

        var result = await executor.RunAsync(job, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("\"id\":\"a\"", result.ResponseJson);
    }

    [Fact]
    public async Task StreamAsyncEmitsFinalDoneChunkWhenBackendEndsEarly()
    {
        var backend = new FakeBackend("""{"response":"hello","done":false}""");
        var executor = BuildExecutor(backend);
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

    private sealed class RecordingBackend(string? embedResponse = null) : IInferenceBackend
    {
        public string Name => "recording";

        public string Endpoint => "http://localhost/recording";

        public string? LastEmbedRequest { get; private set; }

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>());

        public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken)
        {
            LastEmbedRequest = requestJson;
            return Task.FromResult(embedResponse ?? "{}");
        }

        public IAsyncEnumerable<string> StreamAsync(string kind, string requestJson, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private sealed class FakeBackend(params string[] chunks) : IInferenceBackend
    {
        public string Name => "fake";

        public string Endpoint => "http://localhost/fake";

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

        public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken)
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
