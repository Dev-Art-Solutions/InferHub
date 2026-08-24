using InferHub.Coordinator.Cluster;
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
            IConversationAffinity affinity,
            IClusterMembership membership,
            IServiceProvider services) =>
        {
            var now = DateTimeOffset.UtcNow;
            var nodes = registry.Snapshot(now);
            var models = registry.DistinctModels();
            var snapshot = metrics.Snapshot(now);
            var vectorBlock = BuildVectorBlock(services, nodes);
            var throughput = services.GetService(typeof(ThroughputTracker)) as ThroughputTracker;
            var profiles = services.GetService(typeof(IProfileRegistry)) as IProfileRegistry;
            var corpora = services.GetService(typeof(NodeCorpusRegistry)) as NodeCorpusRegistry;
            var tools = services.GetService(typeof(NodeToolRegistry)) as NodeToolRegistry;

            return Results.Ok(new StatusResponse(
                version,
                now,
                snapshot.UptimeSeconds,
                affinity.Count,
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
                    throughput?.NodeAverage(node.NodeId),
                    (node.Capabilities ?? []).Select(capability => capability.Kind).ToArray(),
                    node.MaxConcurrency,
                    BuildProfileBlock(profiles, node),
                    corpora?.Of(node.NodeId),
                    tools?.Of(node.NodeId))).ToArray(),
                models,
                registry.CapabilitySummary(),
                snapshot,
                vectorBlock,
                BuildFallbackBlock(fallback.Value, snapshot),
                BuildProviderBlocks(services.GetService(typeof(IProviderRegistry)) as IProviderRegistry, snapshot),
                BuildQueueBlock(queue),
                BuildClusterBlock(membership)));
        });

        return app;
    }

    /// <summary>
    /// Cloud burst is visible whether or not it is on: a deployment that has never bursted still
    /// reports <c>enabled: false</c>, so "is this thing sending my prompts anywhere?" is a
    /// question the status page answers rather than one you have to go and read the config for.
    /// </summary>
    /// <summary>
    /// A node's profile, as far as the hub can see it (phase 43): which one applies, which revision,
    /// whether the node took it, and what it refused.
    /// </summary>
    /// <remarks>
    /// Null when no profile applies and none ever did, so a fleet that never writes one keeps the
    /// v3.10 status payload byte for byte. <c>conflict</c> is the hub's own answer and never a
    /// node's: a node cannot know that a second profile matches it, because the whole point of D4 is
    /// that it was never sent one.
    /// </remarks>
    private static NodeProfileStatusBlock? BuildProfileBlock(IProfileRegistry? profiles, NodeSnapshot node)
    {
        if (profiles is null)
        {
            return null;
        }

        var assignment = profiles.MatchFor(node.NodeId, node.Labels);
        var state = profiles.StateOf(node.NodeId);

        if (assignment.Profile is null && !assignment.IsConflict && state?.ProfileName is null)
        {
            return null;
        }

        return new NodeProfileStatusBlock(
            assignment.Profile?.Name ?? state?.ProfileName,
            assignment.Profile?.Revision ?? state?.Revision ?? 0,
            assignment.IsConflict ? "conflict" : state?.Status() ?? "pending",
            assignment.Conflicts,
            state?.Refusals ?? Array.Empty<NodeProfileRefusal>());
    }

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

    /// <summary>
    /// Null for a single-coordinator deployment, the same way the vector block is null when the
    /// store is off: a status consumer that never opted into HA never sees a "cluster" key, so
    /// <c>Cluster:Enabled=false</c> stays byte-identical to v2.13.
    /// </summary>
    internal static ClusterStatusBlock? BuildClusterBlock(IClusterMembership membership)
        => membership.Enabled
            ? new ClusterStatusBlock(
                membership.InstanceId,
                membership.IsActive ? ClusterRoleMiddleware.ActiveRole : ClusterRoleMiddleware.StandbyRole,
                membership.Fence,
                membership.ActiveSinceUtc,
                membership.Detail)
            : null;

    internal static FallbackStatusBlock BuildFallbackBlock(FallbackOptions options, MetricsSnapshot metrics)
        => new(
            options.Enabled && !string.IsNullOrWhiteSpace(options.BaseUrl),
            options.NormalizedTrigger(),
            options.ModelMap.Keys.OrderBy(model => model, StringComparer.OrdinalIgnoreCase).ToArray(),
            metrics.FallbackDispatched,
            metrics.LastFallbackModel,
            metrics.LastFallbackAtUtc);

    /// <summary>
    /// The named providers (phase 61), each with its counter — <b>null</b> where none is configured,
    /// so a hub that only ever had a <c>Fallback:</c> section keeps the exact v3.28 payload.
    /// </summary>
    /// <remarks>
    /// Reported whether or not anything has been dispatched, for 22 D5's reason: "is this thing
    /// sending my prompts anywhere" is a question the status page answers, not one an operator has
    /// to go and read the config for. A provider that has served nothing shows a zero here and
    /// <em>no</em> metric series (phase-28 D5) — the two surfaces answer different questions.
    /// </remarks>
    internal static IReadOnlyList<ProviderStatusBlock>? BuildProviderBlocks(
        IProviderRegistry? providers,
        MetricsSnapshot metrics)
    {
        if (providers is null || providers.Configured.Count == 0)
        {
            return null;
        }

        var dispatched = (metrics.PerProvider ?? [])
            .ToDictionary(provider => provider.Provider, StringComparer.Ordinal);

        return providers.Configured
            .Select(route =>
            {
                dispatched.TryGetValue(route.Id, out var counter);

                return new ProviderStatusBlock(
                    route.Id,
                    route.Definition.NormalizedType(),
                    // Phase 65 D7: `policy` replaces `trigger` rather than joining it. Two spellings
                    // of one thing on a status payload is how a dashboard ends up believing
                    // whichever key it read first, and this block has no console panel until 66.
                    route.Definition.NormalizedPolicy(),
                    ModelPolicies(route.Definition),
                    string.IsNullOrWhiteSpace(route.Definition.ApiKey) ? "absent" : "configured",
                    route.Definition.ModelMap.Keys.OrderBy(model => model, StringComparer.OrdinalIgnoreCase).ToArray(),
                    counter?.Dispatched ?? 0,
                    counter?.LastModel,
                    counter?.LastAtUtc);
            })
            .ToArray();
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
        // Under an external provider (postgres, qdrant) there is no node replication: placement is
        // zeroed for every collection so the replica formula can't false-flag under-replication
        // against zero holders. The wire string still reports which external provider is in use.
        var isExternal = VectorStoreProviderExtensions.IsExternal(provider);
        var wire = VectorStoreProviderExtensions.NormalizeWire(provider);

        if (isExternal)
        {
            var externalItems = collections.Select(c => new VectorStatusCollection(
                c.Name, c.Dimension, c.Distance, c.RecordCount,
                TargetReplicas: 0, LiveReplicas: 0, ReplicaNodes: Array.Empty<string>(), UnderReplicated: false,
                Ingestion: IngestionOf(metrics, c.Name))).ToArray();
            return new VectorStatusBlock(wire, externalItems);
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
        // Live sticky-conversation hints (phase 30). A gauge, reported even when zero.
        int AffinityEntries,
        IReadOnlyList<StatusNode> Nodes,
        IReadOnlyCollection<ModelInfo> Models,
        // What the fleet can do, and with which models (phase 40). Always present — an empty
        // array on a fleet with no nodes is a statement, not an absence.
        IReadOnlyCollection<CapabilitySummary> Capabilities,
        MetricsSnapshot Metrics,
        VectorStatusBlock? Vector,
        FallbackStatusBlock Fallback,
        // Phase 61. Null when no `Providers:` entry is configured, and **omitted** rather than
        // written as null — every other optional block here (vector, cluster) predates a consumer,
        // but this one would appear in a payload that had no such key in v3.28. The projected legacy
        // provider is never listed here either: it is already reported one field up.
        [property: System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ProviderStatusBlock>? Providers,
        QueueStatusBlock Queue,
        ClusterStatusBlock? Cluster);

    internal sealed record ClusterStatusBlock(
        string Instance,
        string Role,
        long Fence,
        DateTimeOffset? ActiveSinceUtc,
        string? Detail);

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

    /// <param name="Credential">
    /// <c>configured</c> or <c>absent</c> — never a prefix, never a length, never a hash. A status
    /// page that renders four characters of somebody's API key has published four characters of
    /// somebody's API key.
    /// </param>
    /// <summary>
    /// The per-model overrides (65 D2), or null where there are none — a deployment that sets one
    /// policy for a provider sees no key rather than an empty object.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ModelPolicies(ProviderDefinition definition)
    {
        var overrides = definition.ModelPolicy
            .Where(entry => ProviderPolicy.Normalize(entry.Value) is not null)
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                entry => entry.Key,
                entry => ProviderPolicy.Normalize(entry.Value)!,
                StringComparer.OrdinalIgnoreCase);

        return overrides.Count == 0 ? null : overrides;
    }

    internal sealed record ProviderStatusBlock(
        string Id,
        string Type,
        string Policy,
        IReadOnlyDictionary<string, string>? ModelPolicies,
        string Credential,
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
        double? TokensPerSecond,
        // Resolved, so this is what the router will actually match on for this node — not what
        // it declared (phase-40 D1). A node that declared nothing shows chat + embed.
        IReadOnlyList<string> Capabilities,
        // The cap the node is running at, after a profile may have lowered it (phase 43).
        int? MaxConcurrency,
        NodeProfileStatusBlock? Profile,
        // What this node last *said* about the corpus it hosts (phase 44, D6) — never the answer to
        // a query the hub ran against it. Null for a node that has never reported one, so a fleet
        // with no node corpora keeps the v3.11 payload exactly.
        NodeCorpusState? Corpus = null,
        // Likewise for the tool runtime (phase 45). Null for a node running v3.12 or earlier, or one
        // that has simply never reported — which is the honest answer and not "no tools".
        NodeToolState? Tools = null);

    internal sealed record NodeProfileStatusBlock(
        string? Name,
        long Revision,
        /// <c>applied</c> | <c>pending</c> | <c>refused</c> | <c>conflict</c> | <c>none</c>.
        string Status,
        IReadOnlyList<string>? Conflicts,
        IReadOnlyList<NodeProfileRefusal> Refusals);

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
