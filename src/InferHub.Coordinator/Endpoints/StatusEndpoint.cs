using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;
using InferHub.Shared.Vector;

namespace InferHub.Coordinator.Endpoints;

public static class StatusEndpoint
{
    public static IEndpointRouteBuilder MapStatusEndpoint(this IEndpointRouteBuilder app, string version)
    {
        app.MapGet("/api/status", (
            INodeRegistry registry,
            Metrics metrics,
            IServiceProvider services) =>
        {
            var now = DateTimeOffset.UtcNow;
            var nodes = registry.Snapshot(now);
            var models = registry.DistinctModels();
            var snapshot = metrics.Snapshot(now);
            var vectorBlock = BuildVectorBlock(services, nodes);

            return Results.Ok(new StatusResponse(
                version,
                now,
                snapshot.UptimeSeconds,
                nodes.Select(node => new StatusNode(
                    node.NodeId,
                    node.Name,
                    node.OllamaEndpoint,
                    node.Version,
                    node.LastSeenUtc,
                    node.AgeSeconds,
                    node.InFlight,
                    node.LocalInFlight,
                    node.ModelCount,
                    node.Cordoned)).ToArray(),
                models,
                snapshot,
                vectorBlock));
        });

        return app;
    }

    // Returns null when the vector store is disabled — matches the phase-13 contract that
    // Enabled=false is byte-for-byte unchanged for existing status consumers who never
    // see a "vector" key.
    private static VectorStatusBlock? BuildVectorBlock(
        IServiceProvider services,
        IReadOnlyCollection<NodeSnapshot> nodes)
    {
        var store = services.GetService<IVectorStore>();
        var replicas = services.GetService<ReplicaRegistry>();
        var options = services.GetService<Microsoft.Extensions.Options.IOptions<VectorStoreOptions>>();
        if (store is null || replicas is null || options is null) return null;

        var collections = store.ListCollectionsAsync().GetAwaiter().GetResult();
        return BuildVectorBlock(collections, replicas, nodes, options.Value.ReplicationFactor, options.Value.Provider);
    }

    internal static VectorStatusBlock BuildVectorBlock(
        IReadOnlyList<CollectionInfo> collections,
        ReplicaRegistry replicas,
        IReadOnlyCollection<NodeSnapshot> nodes,
        int replicationFactor,
        string provider = VectorStoreProviderExtensions.Local)
    {
        // Under postgres there is no node replication: placement is zeroed for every collection
        // so the replica formula can't false-flag under-replication against zero holders.
        var isPostgres = VectorStoreProviderExtensions.IsPostgres(provider);
        var wire = isPostgres ? VectorStoreProviderExtensions.Postgres : VectorStoreProviderExtensions.Local;

        if (isPostgres)
        {
            var postgresItems = collections.Select(c => new VectorStatusCollection(
                c.Name, c.Dimension, c.Distance, c.RecordCount,
                TargetReplicas: 0, LiveReplicas: 0, ReplicaNodes: Array.Empty<string>(), UnderReplicated: false)).ToArray();
            return new VectorStatusBlock(wire, postgresItems);
        }

        var target = Math.Max(1, replicationFactor);
        var connectionToNodeId = nodes.ToDictionary(n => n.ConnectionId, n => n.NodeId, StringComparer.Ordinal);
        var eligibleCount = nodes.Count(n => !n.Cordoned);
        var desired = Math.Min(target, eligibleCount);

        var items = collections.Select(c =>
        {
            var holders = replicas.Holders(c.Name);
            var holderNodeIds = holders
                .Where(connectionToNodeId.ContainsKey)
                .Select(connId => connectionToNodeId[connId])
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            return new VectorStatusCollection(
                c.Name,
                c.Dimension,
                c.Distance,
                c.RecordCount,
                target,
                holderNodeIds.Length,
                holderNodeIds,
                holderNodeIds.Length < desired);
        }).ToArray();

        return new VectorStatusBlock(wire, items);
    }

    private sealed record StatusResponse(
        string CoordinatorVersion,
        DateTimeOffset NowUtc,
        double UptimeSeconds,
        IReadOnlyList<StatusNode> Nodes,
        IReadOnlyCollection<ModelInfo> Models,
        MetricsSnapshot Metrics,
        VectorStatusBlock? Vector);

    private sealed record StatusNode(
        string NodeId,
        string Name,
        string OllamaEndpoint,
        string Version,
        DateTimeOffset LastSeenUtc,
        double AgeSeconds,
        int InFlight,
        int LocalInFlight,
        int ModelCount,
        bool Cordoned);

    internal sealed record VectorStatusBlock(
        string Provider,
        IReadOnlyList<VectorStatusCollection> Collections);

    internal sealed record VectorStatusCollection(
        string Name,
        int Dimension,
        string Distance,
        long RecordCount,
        int TargetReplicas,
        int LiveReplicas,
        IReadOnlyList<string> ReplicaNodes,
        bool UnderReplicated);
}
