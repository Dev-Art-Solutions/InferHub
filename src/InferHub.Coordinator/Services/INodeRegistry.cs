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

    string? FindConnectionIdByNodeId(string nodeId);

    IReadOnlyCollection<NodeSnapshot> Snapshot(DateTimeOffset now);

    IReadOnlyCollection<ModelInfo> DistinctModels();

    /// <summary>Per-node model lists (phase 26) — what the fleet-wide model matrix is built from.</summary>
    IReadOnlyCollection<NodeModelInventory> ModelInventory();

    IReadOnlyCollection<RoutableNode> FindNodesWithModel(string model);

    int IncrementInFlight(string connectionId);

    int DecrementInFlight(string connectionId);

    int GetLocalInFlight(string connectionId);

    IReadOnlyCollection<NodeSnapshot> EvictStale(DateTimeOffset cutoffUtc, DateTimeOffset now);
}

/// <summary>One node's model inventory, for the phase-26 fleet model matrix.</summary>
public sealed record NodeModelInventory(
    string ConnectionId,
    string NodeId,
    string Name,
    bool Cordoned,
    bool SupportsModelManagement,
    IReadOnlyList<ModelInfo> Models);
