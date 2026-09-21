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
            // Phase 81: the assembly itself moved to ScrapeSnapshotBuilder, the one place that
            // knows how to build a PrometheusScrape — OtlpMetricsExporterService is the second caller.
            var scrape = ScrapeSnapshotBuilder.Build(
                version, registry, metrics, throughput, queue, clients, admission,
                affinity, membership, profiles, toolStates, corpora, services);

            return Results.Text(PrometheusFormatter.Format(scrape), PrometheusFormatter.ContentType);
        });

        return app;
    }
}
