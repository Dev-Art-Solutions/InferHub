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
            Microsoft.Extensions.Options.IOptions<FallbackOptions> fallback,
            IRequestQueue queue,
            IServiceProvider services) =>
        {
            var now = DateTimeOffset.UtcNow;
            var nodes = registry.Snapshot(now);
            var models = registry.DistinctModels();
            var snapshot = metrics.Snapshot(now);
            var vectorBlock = BuildVectorBlock(services, nodes);
            var throughput = services.GetService(typeof(ThroughputTracker)) as ThroughputTracker;

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
                    node.Cordoned,
                    throughput?.NodeAverage(node.NodeId))).ToArray(),
                models,
                snapshot,
                vectorBlock,
                BuildFallbackBlock(fallback.Value, snapshot),
                BuildQueueBlock(queue)));
        });

        return app;
    }

    /// <summary>
    /// Cloud burst is visible whether or not it is on: a deployment that has never bursted still
    /// reports <c>enabled: false</c>, so "is this thing sending my prompts anywhere?" is a
    /// question the status page answers rather than one you have to go and read the config for.
    /// </summary>
    /// <summary>
    /// A queue you cannot see is a queue you will not notice filling (phase 25, D5). Reported
    /// even when nothing has ever queued, so a zero is a statement rather than an absence.
    /// </summary>
    internal static QueueStatusBlock BuildQueueBlock(IRequestQueue queue)
    {
        var snapshot = queue.Snapshot();
        return new QueueStatusBlock(
            snapshot.Depth,
            snapshot.Queued,
            snapshot.Admitted,
            snapshot.TimedOut,
            snapshot.Rejected,
            snapshot.MedianWaitMs);
    }

    internal static FallbackStatusBlock BuildFallbackBlock(FallbackOptions options, MetricsSnapshot metrics)
        => new(
            options.Enabled && !string.IsNullOrWhiteSpace(options.BaseUrl),
            options.NormalizedTrigger(),
            options.ModelMap.Keys.OrderBy(model => model, StringComparer.OrdinalIgnoreCase).ToArray(),
            metrics.FallbackDispatched,
            metrics.LastFallbackModel,
            metrics.LastFallbackAtUtc);

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
        return BuildVectorBlock(
            collections, replicas, nodes,
            options.Value.ReplicationFactor, options.Value.Provider,
            services.GetService<Metrics>());
    }

    internal static VectorStatusBlock BuildVectorBlock(
        IReadOnlyList<CollectionInfo> collections,
        ReplicaRegistry replicas,
        IReadOnlyCollection<NodeSnapshot> nodes,
        int replicationFactor,
        string provider = VectorStoreProviderExtensions.Local,
        Metrics? metrics = null)
    {
        // Under postgres there is no node replication: placement is zeroed for every collection
        // so the replica formula can't false-flag under-replication against zero holders.
        var isPostgres = VectorStoreProviderExtensions.IsPostgres(provider);
        var wire = isPostgres ? VectorStoreProviderExtensions.Postgres : VectorStoreProviderExtensions.Local;

        if (isPostgres)
        {
            var postgresItems = collections.Select(c => new VectorStatusCollection(
                c.Name, c.Dimension, c.Distance, c.RecordCount,
                TargetReplicas: 0, LiveReplicas: 0, ReplicaNodes: Array.Empty<string>(), UnderReplicated: false,
                Ingestion: IngestionOf(metrics, c.Name))).ToArray();
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
                holderNodeIds.Length < desired,
                IngestionOf(metrics, c.Name));
        }).ToArray();

        return new VectorStatusBlock(wire, items);
    }

    // Omitted entirely for a collection nothing has been ingested into this run — an all-zero
    // block on every collection of a deployment that never uses ingestion is noise.
    private static IngestionStatusBlock? IngestionOf(Metrics? metrics, string collection)
    {
        if (metrics is null) return null;

        var snapshot = metrics.GetVectorCollectionSnapshot(collection);
        if (snapshot.DocumentsIngested == 0 && snapshot.ChunksEmbedded == 0 && snapshot.IngestionFailures == 0)
        {
            return null;
        }

        return new IngestionStatusBlock(
            snapshot.DocumentsIngested,
            snapshot.ChunksEmbedded,
            snapshot.IngestionFailures,
            snapshot.LastIngestAtUtc,
            snapshot.LastEmbeddingModel);
    }

    private sealed record StatusResponse(
        string CoordinatorVersion,
        DateTimeOffset NowUtc,
        double UptimeSeconds,
        IReadOnlyList<StatusNode> Nodes,
        IReadOnlyCollection<ModelInfo> Models,
        MetricsSnapshot Metrics,
        VectorStatusBlock? Vector,
        FallbackStatusBlock Fallback,
        QueueStatusBlock Queue);

    internal sealed record QueueStatusBlock(
        int Depth,
        long Queued,
        long Admitted,
        long TimedOut,
        long Rejected,
        double? MedianWaitMs);

    internal sealed record FallbackStatusBlock(
        bool Enabled,
        string Trigger,
        IReadOnlyList<string> MappedModels,
        long Dispatched,
        string? LastModel,
        DateTimeOffset? LastAtUtc);

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
        bool Cordoned,
        double? TokensPerSecond);

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
        bool UnderReplicated,
        IngestionStatusBlock? Ingestion = null);

    /// <summary>
    /// What this coordinator has ingested into the collection **since it started**. The names say
    /// "ingested"/"embedded" rather than "documents"/"chunks" because that is what they are: a
    /// restart zeroes them, exactly like every other counter in <c>Metrics</c>. The collection's
    /// real chunk count is <c>recordCount</c> above, and its real document count is whatever
    /// <c>GET /api/collections/{name}/documents</c> reads back.
    /// </summary>
    internal sealed record IngestionStatusBlock(
        long DocumentsIngested,
        long ChunksEmbedded,
        long Failures,
        DateTimeOffset? LastIngestAtUtc,
        string? EmbeddingModel);
}
