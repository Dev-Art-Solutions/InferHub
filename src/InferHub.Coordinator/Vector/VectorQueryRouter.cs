using System.Text.Json;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Vector;
using InferHub.Shared.Vector.Replication;

namespace InferHub.Coordinator.Vector;

public sealed class VectorQueryRouter(
    ReplicaRegistry replicas,
    INodeRegistry registry,
    IDispatcher dispatcher,
    ILogger<VectorQueryRouter> logger) : IVectorQueryRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private int _cursor;

    public async Task<IReadOnlyList<VectorMatch>?> TryQueryOnNodeAsync(
        string collection,
        VectorQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Vector is null || query.Vector.Length == 0)
        {
            return null;
        }

        var holders = replicas.Holders(collection);
        if (holders.Count == 0)
        {
            return null;
        }

        var nodes = registry.Snapshot(DateTimeOffset.UtcNow)
            .Where(n => !n.Cordoned && holders.Contains(n.ConnectionId))
            .OrderBy(n => n.LocalInFlight)
            .ToArray();

        if (nodes.Length == 0)
        {
            return null;
        }

        // Cheap rotation between equally-loaded holders so a hot collection doesn't pin
        // to a single node.
        var minLoad = nodes.Min(n => n.LocalInFlight);
        var tied = nodes.Where(n => n.LocalInFlight == minLoad).ToArray();
        var pick = tied[(int)((uint)Interlocked.Increment(ref _cursor) % tied.Length)];

        var request = new VectorQueryRequest(collection, query.Vector, Math.Max(1, query.K), query.Filter);
        var job = new InferenceJob(Guid.NewGuid(), "vector-query", JsonSerializer.Serialize(request, JsonOptions));
        var route = new RoutableNode(pick.ConnectionId, pick.NodeId, pick.Name);

        try
        {
            var result = await dispatcher.DispatchAsync(route, job, cancellationToken);
            if (!result.Success || string.IsNullOrEmpty(result.ResponseJson))
            {
                logger.LogWarning("Vector-query job {JobId} on {NodeId} failed: {Error}", job.JobId, pick.NodeId, result.Error);
                return null;
            }

            var response = JsonSerializer.Deserialize<VectorQueryResponse>(result.ResponseJson, JsonOptions);
            return response?.Matches;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Vector-query job {JobId} on {NodeId} threw", job.JobId, pick.NodeId);
            return null;
        }
    }
}
