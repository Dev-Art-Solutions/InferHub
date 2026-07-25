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
    ILogger<QdrantBootstrapper> logger) : IVectorStoreBootstrapper
{
    private readonly QdrantStoreOptions _q = options.Value.Qdrant;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        WarnIfRemoteAndUnauthenticated();

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

        logger.LogInformation(
            "Qdrant vector store ready (url={Url}, collections={Count}, quantization={Quantization}, onDisk={OnDisk})",
            _q.Url, count, _q.Quantization, _q.OnDisk);
    }

    /// <summary>
    /// A Qdrant that is not on this machine and has no API key is readable and writable by anything
    /// that can reach the port — every chunk of text you ingested, available to a scanner. Qdrant
    /// itself ships unauthenticated by default, which is fine for localhost and a data leak anywhere
    /// else, so say so at startup. It is a warning and not a hard failure on purpose: a private
    /// network with its own controls is a legitimate deployment, and refusing to boot would be us
    /// overruling an operator about their own network.
    /// </summary>
    private void WarnIfRemoteAndUnauthenticated()
    {
        if (!string.IsNullOrWhiteSpace(_q.ApiKey)) return;
        if (!Uri.TryCreate(_q.Url, UriKind.Absolute, out var uri)) return;
        if (uri.IsLoopback) return;

        logger.LogWarning(
            "Qdrant at {Url} is not loopback and VectorStore:Qdrant:ApiKey is not set. Anything that can " +
            "reach that address can read and delete your vectors and the chunk text stored with them. " +
            "Set an API key (env VectorStore__Qdrant__ApiKey or user-secrets) and enable it on the Qdrant side.",
            _q.Url);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
