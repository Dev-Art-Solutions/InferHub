namespace InferHub.Shared.Contracts;

public sealed record NodeRegistration(
    string NodeId,
    string Name,
    /// Since phase 22 this is the *backend's* endpoint, whatever the backend is — an
    /// OpenAI-backed node reports its upstream BaseUrl here. The name is kept because it is a
    /// SignalR payload field and a /api/status field, and renaming it would break both a
    /// mixed-version fleet and every existing status consumer for a cosmetic gain.
    string OllamaEndpoint,
    string Version,
    IReadOnlyDictionary<string, string>? Labels = null,
    int? MaxConcurrency = null,
    IReadOnlyList<NodeReplicaInventoryItem>? Replicas = null,
    /// Whether this node's backend can pull/delete/warm models (phase 26). Ollama can; an
    /// OpenAI-compatible upstream cannot. Reported at registration so the coordinator can gate the
    /// model-management endpoints and the console can grey out controls a node cannot honour.
    bool SupportsModelManagement = false,
    /// What this node can *do* (phase 40). **Null means "not declared"**, which the coordinator
    /// reads as chat + embed over every reported model — exactly the pre-v3.8 semantics, so a
    /// v3.7 node against a v3.8 hub is routed as it always was. An empty list is a declaration
    /// that this node serves nothing.
    ///
    /// The node itself declares on the model report rather than here (it has not asked its
    /// backend what it holds at registration time, and asking first would mean a node with a dead
    /// backend never registers at all — phase-36 D7). The field exists because registration is
    /// where a node with a fixed, backend-independent capability set would declare it.
    IReadOnlyList<NodeCapability>? Capabilities = null,
    /// Whether this node can take an attachment as a pulled stream rather than as bytes on the job
    /// (phase 53, D5). **Null means "no"**, which is what every node before v3.21 is: it has no
    /// `StreamAttachments` to call, so a hub that sent it a streamed job would fail every request
    /// with something unreadable. The router filters on this for streamed jobs only, so buffered
    /// traffic keeps routing to the whole fleet — phase-40 D1's mixed-fleet rule, again.
    bool? SupportsStreamedAttachments = null);

/// <summary>
/// One row of a node's on-disk vector replica inventory, reported at registration so the
/// coordinator can skip re-pushing replicas that already match the hub's latest seqNo.
/// </summary>
public sealed record NodeReplicaInventoryItem(string Collection, long LastSeq);
