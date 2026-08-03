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
/// The keys are deliberately <em>not</em> <c>VectorStore:*</c> (D4). Reading the hub's section here
/// would mean a box with both <c>appsettings.json</c> files silently picking up the coordinator's
/// store settings.
/// </para>
/// <para>
/// <b>Phase 44 amends this in two places and only two.</b> A node now has a
/// <see cref="Provider"/> — <c>local</c> or <c>qdrant</c>, never <c>postgres</c>, because
/// <c>Npgsql</c> is scoped to the coordinator by name (rule 5) — and a
/// <see cref="Credentials"/> map, because a hub-assigned corpus names a credential and the node
/// resolves it (phase-44 D4). Everything else means what it did in v3.10, and a node that sets none
/// of the new keys behaves exactly as it did.
/// </para>
/// </remarks>
public sealed class LocalRetrievalOptions
{
    public const string SectionName = "LocalApi:Retrieval";

    public bool Enabled { get; set; }

    /// <summary>
    /// <c>local</c> (default) or <c>qdrant</c>. <c>postgres</c> is refused by name — see
    /// <c>LocalRetrievalOptionsValidator</c>, which says so rather than reporting an unknown value.
    /// </summary>
    public string Provider { get; set; } = VectorStoreProviderExtensions.Local;

    /// <summary>Where the external engine is. Meaningless, and ignored, under <c>local</c>.</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// Which entry of <see cref="Credentials"/> to authenticate the external engine with. A hub
    /// assigning a corpus sends this <em>name</em> and never a secret (phase-44 D4).
    /// </summary>
    public string? CredentialRef { get; set; }

    /// <summary>
    /// Credential name → secret, read from this box's configuration or environment
    /// (<c>LocalApi__Retrieval__Credentials__sofia-qdrant</c>). The hub can name one of these; it can
    /// never add one, and a name that is not here is a refusal rather than an unauthenticated
    /// connection.
    /// </summary>
    public Dictionary<string, string> Credentials { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
    /// Qdrant knobs for a node running the <c>qdrant</c> provider. The same
    /// <see cref="QdrantStoreOptions"/> the coordinator uses, because phase 44 moved the store into
    /// <c>InferHub.Shared</c> rather than writing a second one — the collection prefix, the HNSW
    /// build parameters and the payload index keys therefore mean exactly what they mean on a hub.
    /// </summary>
    public QdrantStoreOptions Qdrant { get; set; } = new();

    /// <summary>
    /// Projects the node's keys onto the shared <see cref="VectorStoreOptions"/> the moved pipelines
    /// take.
    /// </summary>
    /// <param name="provider">
    /// The engine to project, which is not always <see cref="Provider"/>: a corpus the hub assigned
    /// carries its own, and the node's file is the default rather than the answer.
    /// </param>
    /// <param name="url">Overrides <see cref="Url"/> for an assigned corpus.</param>
    /// <param name="apiKey">
    /// The <em>resolved</em> secret — resolution happens in one place, on the node, before this is
    /// called (D4). Nothing here reads <see cref="Credentials"/>.
    /// </param>
    public VectorStoreOptions ToVectorStoreOptions(
        string? provider = null,
        string? url = null,
        string? apiKey = null) => new()
    {
        Enabled = Enabled,
        Provider = provider ?? Provider,
        DataDirectory = DataDirectory,
        Distance = Distance,
        SnapshotEveryOps = SnapshotEveryOps,
        DefaultEmbeddingModel = DefaultEmbeddingModel,
        Retrieval = Retrieval,
        Qdrant = Qdrant.WithConnection(url ?? Url, apiKey)
    };

    /// <summary>
    /// Resolves a credential <em>name</em> to the secret on this box. Returns false for a name this
    /// node does not have, which the caller turns into a refusal naming the key — the one behaviour
    /// D4 is most concerned with, because the alternative is a corpus that silently comes up
    /// unauthenticated against somebody's shared Qdrant.
    /// </summary>
    public bool TryResolveCredential(string? credentialRef, out string secret)
    {
        secret = string.Empty;

        if (string.IsNullOrWhiteSpace(credentialRef))
        {
            return false;
        }

        if (!Credentials.TryGetValue(credentialRef.Trim(), out var found) || string.IsNullOrWhiteSpace(found))
        {
            return false;
        }

        secret = found;
        return true;
    }
}
