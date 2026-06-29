using InferHub.Shared.Vector;

namespace InferHub.Coordinator.Vector;

public interface IVectorQueryRouter
{
    /// <summary>
    /// Try to answer a query from a node replica. Returns null when no replica is available
    /// (caller falls back to the hub-local index).
    /// </summary>
    Task<IReadOnlyList<VectorMatch>?> TryQueryOnNodeAsync(
        string collection,
        VectorQuery query,
        CancellationToken cancellationToken);
}
