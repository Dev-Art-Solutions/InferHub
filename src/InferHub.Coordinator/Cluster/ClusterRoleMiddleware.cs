using System.Text.Json;
using InferHub.Shared.OpenAi;

namespace InferHub.Coordinator.Cluster;

/// <summary>
/// Stamps <c>X-InferHub-Role</c> on every response and refuses work on a standby.
/// </summary>
/// <remarks>
/// <b>The hub does not become a load balancer.</b> Client failover is a TCP/HTTP load balancer or
/// DNS in front of both hubs; this middleware exists so that front has something honest to read.
/// A naive round-robin LB with no health check still works, because a standby answers `503` with
/// `Retry-After` rather than a hang or a misleading 404, and a health-checking LB drains the
/// standby out of rotation on `/health`'s role field.
///
/// Unlike the phase-25 admission check (which lives in <c>InferenceCore</c> because it needs the
/// model name), the role decision needs nothing from the body — so it belongs in the pipeline,
/// before routing, deserialization or a queue wait can happen.
/// </remarks>
internal sealed class ClusterRoleMiddleware(RequestDelegate next, IClusterMembership membership)
{
    public const string RoleHeader = "X-InferHub-Role";

    public const string ActiveRole = "active";

    public const string StandbyRole = "standby";

    /// <summary>
    /// What a standby refuses. Deliberately the routes that need the fleet or write through it —
    /// <c>/health</c>, <c>/api/status</c>, <c>/metrics</c>, the status page and <c>/api/admin/*</c>
    /// all stay served, because "what is this standby doing?" must be answerable from the standby.
    /// </summary>
    private static readonly string[] WorkPrefixes =
    [
        "/api/generate",
        "/api/chat",
        "/api/embed",
        "/api/collections",
        "/api/vector",
        "/v1",
        // The node hub, and this is where node failover is actually enforced. An exception thrown
        // from NodeHub.OnConnectedAsync does *not* fail the client's StartAsync — by then the
        // handshake has completed and the node believes it is connected, only to be dropped a beat
        // later with no reason attached. Refusing the negotiate is what lets a node tell "standby,
        // try the next endpoint" from "hub is down", and it does so before SignalR is involved at
        // all. (NodeHub keeps its own check as defence in depth.)
        "/hubs/node"
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!membership.Enabled)
        {
            await next(context);
            return;
        }

        var active = membership.IsActive;
        context.Response.Headers[RoleHeader] = active ? ActiveRole : StandbyRole;

        if (active || !IsWork(context.Request.Path))
        {
            await next(context);
            return;
        }

        await WriteStandbyRefusalAsync(context);
    }

    private static bool IsWork(PathString path)
    {
        foreach (var prefix in WorkPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task WriteStandbyRefusalAsync(HttpContext context)
    {
        const string message =
            "this coordinator is a standby and does not accept inference; retry against the active coordinator";

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "5";
        context.Response.ContentType = "application/json";

        // Each dialect gets its own envelope, for the same reason phase 29's node errors do: an
        // SDK that reads error.message must find a sentence there, not an unknown-error placeholder.
        var body = context.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(OpenAiErrorEnvelope.Create(message, OpenAiErrorTypes.ApiError, "standby_coordinator"))
            : JsonSerializer.Serialize(new { error = message });

        await context.Response.WriteAsync(body);
    }
}
