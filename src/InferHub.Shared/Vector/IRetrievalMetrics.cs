namespace InferHub.Shared.Vector;

/// <summary>
/// The counters the shared retrieval and ingestion pipelines feed (phase-38 D3).
/// </summary>
/// <remarks>
/// The coordinator's <c>Metrics</c> implements this and is otherwise unchanged — it already had
/// these four methods with these signatures, which is why the seam is shaped the way it is rather
/// than being designed fresh. A solo node has no <c>/metrics</c> surface (phase-37 D5) and no
/// fleet-wide counters, so it registers <see cref="NullRetrievalMetrics"/> and reads its
/// collection and record counts off the store itself.
/// </remarks>
public interface IRetrievalMetrics
{
    void RecordVectorQuery(string collection, TimeSpan elapsed);

    void RecordDocumentIngested(string collection, string embeddingModel);

    void RecordChunksEmbedded(string collection, int count);

    void RecordIngestionFailure(string collection);
}

public sealed class NullRetrievalMetrics : IRetrievalMetrics
{
    public static readonly NullRetrievalMetrics Instance = new();

    public void RecordVectorQuery(string collection, TimeSpan elapsed) { }

    public void RecordDocumentIngested(string collection, string embeddingModel) { }

    public void RecordChunksEmbedded(string collection, int count) { }

    public void RecordIngestionFailure(string collection) { }
}
