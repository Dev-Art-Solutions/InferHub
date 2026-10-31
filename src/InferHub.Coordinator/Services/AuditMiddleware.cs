using System.Diagnostics;
using InferHub.Coordinator.Auth;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Phase 125. Writes one <see cref="AuditRecord"/> per audited request, after it has answered (125 D1/D2). First in the
/// pipeline, so a request the two key guards turn away is on the trail too — a wrong key is the line an auditor most
/// wants. Inert, and not even a scope allocated, unless <c>Audit:Enabled</c>.
/// </summary>
public sealed class AuditMiddleware(RequestDelegate next, AuditTrail trail)
{
    /// <summary>Set by <see cref="AdminApiKeyMiddleware"/>: which admin key it accepted, as a fingerprint, never the key.</summary>
    public const string WhoItemKey = "InferHub.Audit.Who";

    private const int MaxPathLength = 512;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!trail.Enabled || !Audited(context.Request.Path))
        {
            await next(context);
            return;
        }

        var scope = AuditScope.Begin();
        var started = Stopwatch.GetTimestamp();
        int? failed = null;

        try
        {
            await next(context);
        }
        catch
        {
            failed = StatusCodes.Status500InternalServerError;
            throw;
        }
        finally
        {
            var status = failed ?? context.Response.StatusCode;
            var nodes = scope.Close();

            if (Kept(context.Request.Method, context.Request.Path, status))
            {
                trail.Write(new AuditRecord(
                    trail.Now,
                    AuditRecord.Request,
                    WhoOf(context, status),
                    context.Connection.RemoteIpAddress?.ToString(),
                    context.TraceIdentifier,
                    context.Request.Method,
                    PathOf(context),
                    status,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    scope.Model,
                    scope.Capability,
                    nodes,
                    context.Response.Headers.TryGetValue(Endpoints.InferenceCore.ServedByHeader, out var servedBy)
                        ? servedBy.ToString()
                        : null));
            }
        }
    }

    /// <summary>
    /// The surfaces a caller or a node reaches with a key (125 D1): both client dialects, the admin API, the relay a
    /// sealed node downloads through, and ChatGPT pairing. Static files, <c>/health</c>, <c>/metrics</c> and the node
    /// hub's socket are not — the hub's door is recorded as node events instead (D6).
    /// </summary>
    internal static bool Audited(PathString path)
        => path.StartsWithSegments("/api")
            || path.StartsWithSegments(BearerApiKeyMiddleware.OpenAiPathPrefix)
            || path.StartsWithSegments(NodeRelay.PathPrefix)
            || path.StartsWithSegments("/chatgpt-pairing");

    /// <summary>
    /// Reads are not actions (125 D1): the fleet view the console polls every five seconds, and every admin GET, would
    /// bury the lines that matter. Two exceptions: a refused read is kept — a wrong key is not a poll — and a read of the
    /// trail itself is kept, because who looked at who did what is part of who did what.
    /// </summary>
    internal static bool Kept(string method, PathString path, int status)
    {
        if (status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
        {
            return true;
        }

        var read = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

        if (!read)
        {
            return true;
        }

        if (path.StartsWithSegments(AuditEndpoints.Path))
        {
            return true;
        }

        return !path.StartsWithSegments(AdminApiKeyMiddleware.AdminPathPrefix)
            && !path.StartsWithSegments("/api/status");
    }

    /// <summary>
    /// The client id a key resolved to, the admin key's fingerprint, <c>loopback</c> for an exempt local caller,
    /// <c>node</c> for the relay; a refused request is <c>-</c>, because whatever it presented resolved to nobody.
    /// </summary>
    internal static string WhoOf(HttpContext context, int status)
    {
        if (context.Items.TryGetValue(WhoItemKey, out var who) && who is string named)
        {
            return named;
        }

        if (context.Items.TryGetValue(BearerApiKeyMiddleware.ClientItemKey, out var value) && value is ResolvedClient client)
        {
            return client.Id;
        }

        if (status is StatusCodes.Status401Unauthorized)
        {
            return "-";
        }

        return context.Request.Path.StartsWithSegments(NodeRelay.PathPrefix) ? "node" : "-";
    }

    // The path, never the query string: a query is where a caller's own text can ride (rule 7).
    private static string PathOf(HttpContext context)
    {
        var path = (context.Request.PathBase + context.Request.Path).ToString();
        return path.Length <= MaxPathLength ? path : path[..MaxPathLength];
    }
}

/// <summary><c>GET /api/admin/audit</c> — the trail, newest first, behind the admin key (125 D4).</summary>
public static class AuditEndpoints
{
    public const string Path = "/api/admin/audit";

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, async (
            AuditTrail trail,
            string? from,
            string? to,
            string? who,
            string? node,
            string? model,
            string? @event,
            int? status,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            if (!trail.Enabled)
            {
                return Results.Ok(new { enabled = false, message = "this coordinator keeps no audit trail (Audit:Enabled=false)" });
            }

            if (!TryParseTime(from, out var fromUtc) || !TryParseTime(to, out var toUtc))
            {
                return Results.BadRequest(new { error = "from and to are ISO 8601 times, e.g. 2026-10-31T00:00:00Z" });
            }

            var records = await trail.QueryAsync(
                new AuditQuery(fromUtc, toUtc, Blank(who), Blank(node), Blank(model), Blank(@event), status, limit ?? AuditTrail.DefaultLimit),
                cancellationToken);

            return Results.Ok(new { enabled = true, count = records.Count, records });
        });

        return app;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryParseTime(string? value, out DateTimeOffset? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var time))
        {
            parsed = time.ToUniversalTime();
            return true;
        }

        return false;
    }
}
