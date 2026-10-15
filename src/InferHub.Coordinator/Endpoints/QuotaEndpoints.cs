using InferHub.Coordinator.Services;
using InferHub.Shared.Quota;

namespace InferHub.Coordinator.Endpoints;

/// <summary>
/// <c>/api/admin/quota</c> (phase 106): how close each cloud provider is to a limit — the coordinator's
/// own, and each node's own, side by side and never added up.
/// </summary>
/// <remarks>
/// Admin-only and not on <c>/api/status</c>, which an inference key reads: a month's spend and a budget
/// are the operator's business, not every client's (106 D5).
/// </remarks>
public static class QuotaEndpoints
{
    public static IEndpointRouteBuilder MapQuotaEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/quota");

        admin.MapGet("/", (QuotaTracker tracker, CoordinatorQuotaService service, INodeRegistry registry, NodeQuotaRegistry nodes)
            => Results.Ok(Build(tracker, service, registry, nodes)));

        // Asks the coordinator's vendors now. A node refreshes on its own loop — the hub never asks (44 D6).
        admin.MapPost("/refresh", async (QuotaTracker tracker, CoordinatorQuotaService service, INodeRegistry registry, NodeQuotaRegistry nodes, CancellationToken cancellationToken) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await service.RefreshAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A vendor that takes thirty seconds is answered with what is known so far.
            }

            return Results.Ok(Build(tracker, service, registry, nodes));
        });

        return app;
    }

    private static QuotaResponse Build(QuotaTracker tracker, CoordinatorQuotaService service, INodeRegistry registry, NodeQuotaRegistry nodes)
    {
        var now = DateTimeOffset.UtcNow;
        service.Declare();

        return new QuotaResponse(
            now,
            service.Options.RefreshInterval().TotalSeconds is var seconds ? (int)seconds : 0,
            service.LastRefreshUtc,
            tracker.WarnPercent,
            tracker.Snapshot(),
            registry.Snapshot(now)
                .Select(node => (node, state: nodes.Of(node.NodeId)))
                .Where(pair => pair.state is { Providers.Count: > 0 })
                .Select(pair => new NodeQuotaBlock(pair.node.NodeId, pair.node.Name, pair.state!.AtUtc, pair.state.Providers))
                .ToArray());
    }

    internal sealed record QuotaResponse(
        DateTimeOffset AtUtc,
        int RefreshSeconds,
        DateTimeOffset? LastRefreshUtc,
        int WarnPercent,
        IReadOnlyList<ProviderQuota> Coordinator,
        IReadOnlyList<NodeQuotaBlock> Nodes);

    internal sealed record NodeQuotaBlock(string NodeId, string Name, DateTimeOffset AtUtc, IReadOnlyList<ProviderQuota> Providers);
}
