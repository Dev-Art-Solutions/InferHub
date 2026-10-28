using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Fit;

namespace InferHub.Coordinator.Endpoints;

/// <summary>
/// Phase 120 — the node's own <c>/api/control/fit…</c> answered for an admin, under <c>/api/admin/nodes/{id}/fit…</c>:
/// <c>GET …/fit/catalog</c> (what is trending and popular, ordered by what fits that node), <c>POST …/fit</c> (a link and
/// a context → that node's options, with the arithmetic and whether its owner lets the hub install each), and
/// <c>POST …/fit/install</c> (the node agrees to one option, then the hub sends its pull as every other — 26's progress).
/// </summary>
public static class ManagementEndpoints
{
    public sealed record FitBody(string? Link, int? Context, string? Engine, string? Variant, bool Force = false);

    public static RouteGroupBuilder MapNodeManagement(this RouteGroupBuilder group, Func<HttpContext, string> actorOf)
    {
        group.MapGet("/nodes/{nodeId}/fit/catalog", async (
            string nodeId, string? source, string? sort, string? kind, string? q, int? limit, bool? fitsOnly,
            NodeManagementControl control, CancellationToken cancellationToken) =>
            Render(await control.AskAsync(
                nodeId,
                ManagementJob.Catalog,
                new ManagementRequest(Source: source, Sort: sort, Kind: kind, Q: q, Limit: limit, FitsOnly: fitsOnly ?? false),
                cancellationToken)));

        group.MapPost("/nodes/{nodeId}/fit", async (
            string nodeId, FitBody body, NodeManagementControl control, CancellationToken cancellationToken) =>
            Render(await control.AskAsync(nodeId, ManagementJob.Estimate, new ManagementRequest(body.Link, body.Context), cancellationToken)));

        group.MapPost("/nodes/{nodeId}/fit/install", async (
            string nodeId, FitBody body, HttpContext context, NodeManagementControl control, ModelCommandCoordinator commands,
            IAuditLog audit, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            var answer = await control.AskAsync(
                nodeId,
                ManagementJob.Install,
                new ManagementRequest(body.Link, body.Context, body.Engine, body.Variant, body.Force),
                cancellationToken);

            if (!answer.Ok)
            {
                return Render(answer);
            }

            if (NodeManagementControl.ReadInstall(answer.Payload) is not { } install)
            {
                return Results.Json(new { error = "the node agreed to the install but its answer named no pull" }, statusCode: StatusCodes.Status502BadGateway);
            }

            var pull = install.Command;
            var started = await commands.SendAsync(nodeId, pull.Kind, pull.ModelName, cancellationToken, engine: pull.Engine, context: pull.Context);

            if (started is null)
            {
                return Results.NotFound(new { error = $"node '{nodeId}' is no longer connected" });
            }

            audit.Record(nodeId, "model.install.fit", actorOf(context), DateTimeOffset.UtcNow);
            loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Admin").LogInformation(
                "Install of {Variant} for {Engine} on node {NodeId} as '{Model}' (context {Context}) → command {CommandId} (reused={Reused})",
                install.Variant, install.Engine, nodeId, pull.ModelName, pull.Context, started.CommandId, started.Reused);

            return Results.Accepted($"/api/admin/nodes/{nodeId}/models", new
            {
                nodeId,
                model = pull.ModelName,
                kind = pull.Kind,
                engine = pull.Engine,
                context = pull.Context,
                variant = install.Variant,
                diskMiB = install.DiskMiB,
                commandId = started.CommandId,
                reused = started.Reused
            });
        });

        return group;
    }

    /// <summary>The node's JSON as it sent it — one estimate, the node's, not a hub's copy of it (118's invariant).</summary>
    private static IResult Render(NodeManagementControl.Answer answer) =>
        answer.Ok
            ? Results.Content(answer.Payload ?? "{}", "application/json")
            : Results.Json(new { error = answer.Error }, statusCode: answer.Status);
}
