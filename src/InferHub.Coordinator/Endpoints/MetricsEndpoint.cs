using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Cluster;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;

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
            IClusterMembership membership) =>
        {
            var now = DateTimeOffset.UtcNow;

            var scrape = new PrometheusScrape(
                version,
                metrics.Snapshot(now),
                // Ordered so a scrape's output is stable between polls; Prometheus does not care,
                // but a human diffing two curls does.
                registry.Snapshot(now).OrderBy(node => node.NodeId, StringComparer.Ordinal).ToArray(),
                throughput.Snapshot(),
                queue.Snapshot(),
                ClientSamples(clients, admission),
                affinity.Count,
                membership.Enabled
                    ? new ClusterScrapeSample(membership.InstanceId, membership.IsActive, membership.Fence)
                    : null);

            return Results.Text(PrometheusFormatter.Format(scrape), PrometheusFormatter.ContentType);
        });

        return app;
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
