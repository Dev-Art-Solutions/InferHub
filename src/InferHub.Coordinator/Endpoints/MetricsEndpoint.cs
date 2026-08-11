using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Cluster;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Endpoints;

public static class MetricsEndpoint
{
    /// <summary>
    /// The scrape path. Guarded by <c>AdminApiKeyMiddleware</c> unless <c>Metrics:OpenScrape</c>
    /// is set — deliberately <b>not</b> under the bearer inference guard, because a scraper is
    /// not a client and giving Prometheus an inference key would be handing a monitoring system
    /// a token that can spend GPU time.
    /// </summary>
    public const string Path = "/metrics";

    public static IEndpointRouteBuilder MapMetricsEndpoint(this IEndpointRouteBuilder app, string version)
    {
        app.MapGet(Path, (
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
            IServiceProvider services) =>
        {
            var now = DateTimeOffset.UtcNow;

            // Ordered so a scrape's output is stable between polls; Prometheus does not care, but a
            // human diffing two curls does.
            var nodes = registry.Snapshot(now).OrderBy(node => node.NodeId, StringComparer.Ordinal).ToArray();

            var scrape = new PrometheusScrape(
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
                    : null);

            return Results.Text(PrometheusFormatter.Format(scrape), PrometheusFormatter.ContentType);
        });

        return app;
    }

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
