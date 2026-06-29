namespace InferHub.Shared.Contracts;

public sealed record NodeRegistration(
    string NodeId,
    string Name,
    string OllamaEndpoint,
    string Version,
    IReadOnlyDictionary<string, string>? Labels = null,
    int? MaxConcurrency = null,
    IReadOnlyList<NodeReplicaInventoryItem>? Replicas = null);

/// <summary>
/// One row of a node's on-disk vector replica inventory, reported at registration so the
/// coordinator can skip re-pushing replicas that already match the hub's latest seqNo.
/// </summary>
public sealed record NodeReplicaInventoryItem(string Collection, long LastSeq);
