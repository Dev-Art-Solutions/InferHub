using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Endpoints;

public static class StatusEndpoint
{
    public static IEndpointRouteBuilder MapStatusEndpoint(this IEndpointRouteBuilder app, string version)
    {
        app.MapGet("/api/status", (INodeRegistry registry, Metrics metrics) =>
        {
            var now = DateTimeOffset.UtcNow;
            var nodes = registry.Snapshot(now);
            var models = registry.DistinctModels();
            var snapshot = metrics.Snapshot(now);

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
                snapshot));
        });

        return app;
    }

    private sealed record StatusResponse(
        string CoordinatorVersion,
        DateTimeOffset NowUtc,
        double UptimeSeconds,
        IReadOnlyList<StatusNode> Nodes,
        IReadOnlyCollection<ModelInfo> Models,
        MetricsSnapshot Metrics);

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
}
