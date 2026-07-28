using InferHub.Shared.Ingestion;
using InferHub.Shared.Vector;

namespace InferHub.Node.Configuration;

/// <summary>
/// Retrieval in solo mode (phase 38): a standalone node ingests documents, indexes them and
/// grounds its own answers, with no coordinator and no network.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This only exists where there is no coordinator.</strong> Turning it on together with
/// <c>Coordinator:Enabled=true</c> is a startup failure, not a silent skip — see
/// <c>LocalRetrievalOptionsValidator</c> for the reasoning, which is design rule 4 and not a
/// preference.
/// </para>
/// <para>
/// The keys are deliberately <em>not</em> <c>VectorStore:*</c> (D4). A node has one provider —
/// the local one — and no `Provider`, `Postgres` or `Qdrant` section, because `Npgsql` is scoped to
/// the coordinator by name (rule 5). Reading the hub's section here would also mean a box with both
/// <c>appsettings.json</c> files silently picking up the coordinator's store settings.
/// </para>
/// </remarks>
public sealed class LocalRetrievalOptions
{
    public const string SectionName = "LocalApi:Retrieval";

    public bool Enabled { get; set; }

    /// <summary>
    /// Where the collections live. Same Docker trap as the vector store (phase-21 D7) and the
    /// affinity store (phase-30 D3): the default resolves under the content root, which is
    /// <c>/app</c> in the image and not writable by <c>USER app</c>. The node Dockerfile therefore
    /// sets this to <c>/data/retrieval</c>, under the existing <c>chown app:app /data</c>.
    /// </summary>
    public string DataDirectory { get; set; } = "./data/retrieval";

    /// <summary>Distance metric for collections this node creates: <c>cosine</c> | <c>dot</c> | <c>l2</c>.</summary>
    public string Distance { get; set; } = "cosine";

    /// <summary>Ops appended to a collection's log before a compacted snapshot is written.</summary>
    public int SnapshotEveryOps { get; set; } = 5000;

    /// <summary>
    /// Embedding model, resolved against this node's own backend. A model the backend does not
    /// serve is a <c>NoEmbeddingNodeException</c>, exactly as an unheld model is on the hub.
    /// </summary>
    public string DefaultEmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>Retrieval behaviour — the phase-24 keys, unchanged in name, meaning and default.</summary>
    public RetrievalOptions Retrieval { get; set; } = new();

    /// <summary>Ingestion behaviour — the phase-23 keys, unchanged in name, meaning and default.</summary>
    public IngestionOptions Ingestion { get; set; } = new();

    /// <summary>
    /// Projects the node's keys onto the shared <see cref="VectorStoreOptions"/> the moved pipelines
    /// take. Provider is pinned to <c>local</c> because there is no other one here (D4).
    /// </summary>
    public VectorStoreOptions ToVectorStoreOptions() => new()
    {
        Enabled = Enabled,
        Provider = VectorStoreProviderExtensions.Local,
        DataDirectory = DataDirectory,
        Distance = Distance,
        SnapshotEveryOps = SnapshotEveryOps,
        DefaultEmbeddingModel = DefaultEmbeddingModel,
        Retrieval = Retrieval
    };
}
