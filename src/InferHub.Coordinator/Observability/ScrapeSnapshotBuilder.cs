using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Cluster;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Observability;

/// <summary>
/// Assembles a <see cref="PrometheusScrape"/> from every registry that has a piece of it — the one
/// place that knows how (phase 81, D2's own argument applied one level up): before this phase, this
/// body lived only inside <see cref="Endpoints.MetricsEndpoint"/>'s handler, which was fine when
/// there was exactly one caller. <see cref="OtlpMetricsExporterService"/> is the second.
/// </summary>
public static class ScrapeSnapshotBuilder
{
    public static PrometheusScrape Build(
        string version,
        INodeRegistry registry,
        Metrics metrics,
        ThroughputTracker throughput,
        IRequestQueue queue,
        IClientRegistry clients,
        AdmissionControl admission,
        IConversationAffinity affinity,
        IClusterMembership membership,
        IProfileRegistry profiles,
        NodeToolRegistry toolStates,
        NodeCorpusRegistry corpora,
        IServiceProvider services)
    {
        var now = DateTimeOffset.UtcNow;

        // Ordered so a scrape's output is stable between polls; Prometheus does not care, but a
        // human diffing two curls does.
        var nodes = registry.Snapshot(now).OrderBy(node => node.NodeId, StringComparer.Ordinal).ToArray();

        return new PrometheusScrape(
            version,
            metrics.Snapshot(now),
            nodes,
            throughput.Snapshot(),
            queue.Snapshot(),
            ClientSamples(clients, admission),
            affinity.Count,
            membership.Enabled
                ? new ClusterScrapeSample(membership.InstanceId, membership.IsActive, membership.Fence)
                : null,
            registry.CapabilitySummary().ToArray(),
            // Only for nodes that are still here. A report from a box that has gone away is not
            // a gauge, it is a memory — and the fleet counters already say a node is missing.
            nodes.Select(node => toolStates.Of(node.NodeId)).OfType<NodeToolState>().ToArray(),
            ProfileSamples(nodes, profiles),
            nodes.Select(node => corpora.Of(node.NodeId)).OfType<NodeCorpusState>().ToArray(),

            // Resolved rather than injected, so a host that maps /metrics without the image
            // surface — every test fixture that predates phase 51 — keeps working and simply
            // emits no queue gauges.
            services.GetService(typeof(ImageJobRegistry)) is ImageJobRegistry images
                ? new ImageQueueScrapeSample(
                    images.Store.Queued().Count,
                    images.Store.ActiveCount(),
                    images.Store.RetainedBytes())
                : null,

            // Phase 66. Resolved the same way, for the same reason: a host that maps /metrics
            // without the provider seam — every fixture that predates phase 61 — keeps working
            // and simply describes no vendor.
            ProviderSamples(services.GetService(typeof(IProviderRegistry)) as IProviderRegistry));
    }

    /// <summary>
    /// The configured providers, described for the scrape (phase 66, D5). <b>The projected
    /// <c>Fallback:</c> upstream is not among them</b> — <c>Configured</c> has never carried it,
    /// and `inferhub_fallback_dispatched_total` is already that deployment's series.
    /// </summary>
    private static IReadOnlyList<ProviderScrapeSample> ProviderSamples(IProviderRegistry? providers)
        => providers is null
            ? []
            : providers.Configured
                .Select(route => new ProviderScrapeSample(
                    route.Id,
                    route.Definition.NormalizedType(),
                    route.Definition.NormalizedPolicy(),
                    string.IsNullOrWhiteSpace(route.Definition.ApiKey) ? "absent" : "configured"))
                .ToArray();

    /// <summary>
    /// Nodes per (profile, state), counted the same way <c>/api/status</c> and the console count
    /// them — <c>conflict</c> is the hub's own answer, everything else is what the node reported.
    /// A profile that matches nothing produces no series (D2): it is a document, not a fleet state.
    /// </summary>
    private static IReadOnlyList<ProfileScrapeSample> ProfileSamples(
        IReadOnlyList<NodeSnapshot> nodes,
        IProfileRegistry profiles)
    {
        var counts = new Dictionary<(string Profile, string State), int>();

        foreach (var node in nodes)
        {
            var assignment = profiles.MatchFor(node.NodeId, node.Labels);
            var state = profiles.StateOf(node.NodeId);
            var name = assignment.Profile?.Name ?? state?.ProfileName;

            if (name is null)
            {
                continue;
            }

            var key = (name, assignment.IsConflict ? "conflict" : state?.Status() ?? "pending");
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts
            .Select(pair => new ProfileScrapeSample(pair.Key.Profile, pair.Key.State, pair.Value))
            .OrderBy(sample => sample.Profile, StringComparer.Ordinal)
            .ThenBy(sample => sample.State, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ClientScrapeSample> ClientSamples(IClientRegistry clients, AdmissionControl admission) =>
        clients.NamedClients
            .Where(client => !string.IsNullOrWhiteSpace(client.Id))
            .Select(client =>
            {
                var live = admission.LiveUsageOf(client.Id);
                var limits = client.Limits;

                return new ClientScrapeSample(
                    client.Id,
                    live.InFlight,
                    live.RequestsLastMinute,
                    live.TokensLastMinute,
                    live.TokensToday,
                    limits?.MaxConcurrent,
                    limits?.RequestsPerMinute,
                    limits?.TokensPerMinute,
                    limits?.TokensPerDay);
            })
            .OrderBy(sample => sample.ClientId, StringComparer.Ordinal)
            .ToArray();
}
