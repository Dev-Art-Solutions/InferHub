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
    bool SupportsModelManagement = false);

/// <summary>
/// One row of a node's on-disk vector replica inventory, reported at registration so the
/// coordinator can skip re-pushing replicas that already match the hub's latest seqNo.
/// </summary>
public sealed record NodeReplicaInventoryItem(string Collection, long LastSeq);
