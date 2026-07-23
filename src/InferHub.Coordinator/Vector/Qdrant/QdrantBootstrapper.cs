using InferHub.Coordinator.Vector;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Vector.Qdrant;

/// <summary>
/// Prepares the Qdrant vector store at startup: confirms Qdrant is reachable and warms the store's
/// metadata cache from the collections already there. Fails fast with an actionable message rather
/// than starting a coordinator that would 500 on every vector call.
/// </summary>
internal sealed class QdrantBootstrapper(
    QdrantVectorStore store,
    IOptions<VectorStoreOptions> options,
    ILogger<QdrantBootstrapper> logger) : IHostedService
{
    private readonly QdrantStoreOptions _q = options.Value.Qdrant;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        int count;
        try
        {
            // Listing collections doubles as the reachability probe — one round trip that also warms
            // the metadata cache, rather than a separate health ping.
            count = await store.LoadRegistryCacheAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to reach Qdrant at {_q.Url} for the vector store. Check VectorStore:Qdrant:Url " +
                "(and VectorStore:Qdrant:ApiKey if the instance requires one). Underlying error: " + ex.Message, ex);
        }

        logger.LogInformation("Qdrant vector store ready (url={Url}, collections={Count})", _q.Url, count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
