using InferHub.Shared.Vector;

namespace InferHub.Coordinator.Vector;

/// <summary>
/// The vector-query router used under the postgres provider, where there are no node
/// replicas to route to. Always returns <c>null</c>, so <c>VectorEndpoints.RouteQueryAsync</c>
/// falls through to the store exactly as it already does when no replica holds a collection —
/// no endpoint change is needed for reads.
/// </summary>
public sealed class NullVectorQueryRouter : IVectorQueryRouter
{
    public Task<IReadOnlyList<VectorMatch>?> TryQueryOnNodeAsync(
        string collection,
        VectorQuery query,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<VectorMatch>?>(null);
}
