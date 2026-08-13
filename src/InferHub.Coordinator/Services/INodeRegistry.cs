using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

public interface INodeRegistry
{
    event Action? Changed;

    void Upsert(string connectionId, NodeRegistration registration, DateTimeOffset now);

    bool Touch(string connectionId, Heartbeat heartbeat, DateTimeOffset now);

    bool ReportModels(string connectionId, NodeModels models, DateTimeOffset now);

    bool Remove(string connectionId);

    bool Cordon(string nodeId);

    bool Uncordon(string nodeId);

    /// <summary>
    /// The concurrency cap a node is running at after clamping a coordinator profile (phase 43).
    /// Applied here rather than by a re-registration, so lowering a cap takes effect on the next
    /// dispatch without a node having to reconnect. Null restores the registered value.
    /// </summary>
    bool SetEffectiveConcurrency(string connectionId, int? maxConcurrency);

    string? FindConnectionIdByNodeId(string nodeId);

    IReadOnlyCollection<NodeSnapshot> Snapshot(DateTimeOffset now);

    IReadOnlyCollection<ModelInfo> DistinctModels();

    /// <summary>Per-node model lists (phase 26) — what the fleet-wide model matrix is built from.</summary>
    IReadOnlyCollection<NodeModelInventory> ModelInventory();

    /// <summary>
    /// Candidate nodes for a model, optionally narrowed to those that declare a capability for it
    /// (phase 40). <paramref name="capability"/> null means "any" — the pre-v3.8 question, still
    /// asked by saturation and placement, which care about which nodes hold a model at all.
    /// </summary>
    /// <param name="requireStreamedAttachments">
    /// Phase 53, D5. Narrows to nodes that declared they can pull an attachment as a stream. False
    /// for every request that existed before v3.21, so buffered traffic keeps routing to the whole
    /// fleet and only a streamed job can be starved of candidates.
    /// </param>
    IReadOnlyCollection<RoutableNode> FindNodesWithModel(
        string model,
        string? capability = null,
        bool requireStreamedAttachments = false);

    /// <summary>Fleet-wide capability roll-up (phase 40), for <c>/api/status</c> and <c>/v1/models</c>.</summary>
    IReadOnlyCollection<CapabilitySummary> CapabilitySummary();

    int IncrementInFlight(string connectionId);

    int DecrementInFlight(string connectionId);

    int GetLocalInFlight(string connectionId);

    IReadOnlyCollection<NodeSnapshot> EvictStale(DateTimeOffset cutoffUtc, DateTimeOffset now);
}

/// <summary>
/// One capability across the fleet (phase 40): how many live nodes provide it, and for which
/// models. Cordoned nodes are excluded — a capability only its cordoned nodes provide is not one
/// the fleet can currently serve, and reporting otherwise would explain a 503 as a 200.
/// </summary>
public sealed record CapabilitySummary(string Capability, int Nodes, IReadOnlyList<string> Models);

/// <summary>One node's model inventory, for the phase-26 fleet model matrix.</summary>
public sealed record NodeModelInventory(
    string ConnectionId,
    string NodeId,
    string Name,
    bool Cordoned,
    bool SupportsModelManagement,
    IReadOnlyList<ModelInfo> Models);
