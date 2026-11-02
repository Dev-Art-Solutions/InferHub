using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Auth;

/// <summary>
/// Phase 127. The admin's side (tokens, the list, revoke) behind the admin key, and the node's side (enroll with a token,
/// renew with the current certificate). With <c>Identity:Enabled</c> off every route answers 404 naming the key.
/// </summary>
public static class NodeIdentityEndpoints
{
    public const string AdminPath = "/api/admin/identity";

    public static IEndpointRouteBuilder MapNodeIdentity(this IEndpointRouteBuilder app)
    {
        app.MapGet(AdminPath, (NodeCertificateAuthority authority) =>
        {
            if (!authority.Enabled)
            {
                return Off();
            }

            var now = DateTimeOffset.UtcNow;
            return Results.Ok(new
            {
                enabled = true,
                require = authority.Require,
                authority = authority.AuthorityFingerprint(),
                certificates = authority.Certificates()
                    .OrderByDescending(c => c.IssuedUtc)
                    .Select(c => new { c.NodeId, c.Name, c.Serial, c.IssuedUtc, c.ExpiresUtc, state = c.State(now), c.RevokedUtc, c.RevokedBy, c.Reason })
            });
        });

        app.MapPost(AdminPath + "/tokens", (HttpContext context, NodeCertificateAuthority authority, TokenRequest? body) =>
        {
            if (!authority.Enabled)
            {
                return Off();
            }

            var (token, expires) = authority.CreateToken(body?.Label, By(context));
            return Results.Ok(new
            {
                token,
                expiresUtc = expires,
                use = "set Coordinator__EnrollmentToken on the node; it is spent on first use"
            });
        });

        app.MapPost(AdminPath + "/nodes/{nodeId}/revoke", (
            string nodeId,
            HttpContext context,
            NodeCertificateAuthority authority,
            INodeRegistry registry,
            INodeConnectionTracker connections,
            RevokeRequest? body) =>
        {
            if (!authority.Enabled)
            {
                return Off();
            }

            var revoked = authority.Revoke(nodeId, By(context), body?.Reason);

            if (revoked == 0)
            {
                return Results.NotFound(new { error = $"node '{nodeId}' holds no active certificate from this coordinator" });
            }

            // D5: a revoked node is dropped now, not at its next reconnect.
            var dropped = registry.FindConnectionIdByNodeId(nodeId) is { } connectionId && connections.Abort(connectionId);
            return Results.Ok(new { nodeId, revoked, disconnected = dropped });
        });

        app.MapPost(NodeProof.EnrollPath, (NodeEnrollRequest request, NodeCertificateAuthority authority) =>
        {
            if (!authority.Enabled)
            {
                return Off();
            }

            try
            {
                return Results.Ok(authority.Enroll(request));
            }
            catch (NodeIdentityException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
        });

        app.MapPost(NodeProof.RenewPath, (HttpContext context, NodeRenewRequest request, NodeCertificateAuthority authority) =>
        {
            if (!authority.Enabled)
            {
                return Off();
            }

            var proof = authority.Verify(name => context.Request.Headers.TryGetValue(name, out var v) ? v.ToString() : null);

            if (proof is not { Valid: true, NodeId: { } nodeId })
            {
                return Results.Json(new { error = proof?.Reason ?? "renewing needs the current certificate's proof" }, statusCode: 401);
            }

            try
            {
                return Results.Ok(authority.Renew(nodeId, request));
            }
            catch (NodeIdentityException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
        });

        return app;
    }

    private static IResult Off()
        => Results.NotFound(new { error = "this coordinator issues no node certificates (Identity:Enabled=false)" });

    private static string By(HttpContext context)
        => context.Items.TryGetValue(AuditMiddleware.WhoItemKey, out var who) && who is string named ? named : "admin";

    public sealed record TokenRequest(string? Label);

    public sealed record RevokeRequest(string? Reason);
}
