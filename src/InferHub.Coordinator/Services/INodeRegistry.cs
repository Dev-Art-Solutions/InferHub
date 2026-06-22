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

    IReadOnlyCollection<RoutableNode> FindNodesWithModel(string model);

    int IncrementInFlight(string connectionId);

    int DecrementInFlight(string connectionId);

    int GetLocalInFlight(string connectionId);

    IReadOnlyCollection<NodeSnapshot> EvictStale(DateTimeOffset cutoffUtc, DateTimeOffset now);
}
