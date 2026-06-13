using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

public interface INodeRegistry
{
    void Upsert(string connectionId, NodeRegistration registration, DateTimeOffset now);

    bool Touch(string connectionId, Heartbeat heartbeat, DateTimeOffset now);

    bool ReportModels(string connectionId, NodeModels models, DateTimeOffset now);

    bool Remove(string connectionId);

    IReadOnlyCollection<NodeSnapshot> Snapshot(DateTimeOffset now);

    IReadOnlyCollection<ModelInfo> DistinctModels();

    IReadOnlyCollection<NodeSnapshot> EvictStale(DateTimeOffset cutoffUtc, DateTimeOffset now);
}
