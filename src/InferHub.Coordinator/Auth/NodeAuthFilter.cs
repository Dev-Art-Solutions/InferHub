using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Auth;

public sealed class NodeAuthFilter(
    IOptionsMonitor<ApiKeyOptions> options,
    ILogger<NodeAuthFilter> logger,
    NodeCertificateAuthority? certificates = null)
{
    public const string EnrollmentSecretHeader = "X-Node-Enrollment-Secret";

    /// <summary>Phase 127: the node id a connection proved with its certificate, kept on the hub context for <c>Register</c>.</summary>
    public const string CertifiedNodeKey = "InferHub.CertifiedNode";

    public bool IsAuthorized(HubCallerContext context)
    {
        var http = context.GetHttpContext();

        switch (Certified(http, context.ConnectionId))
        {
            case { Valid: true, NodeId: { } nodeId }:
                context.Items[CertifiedNodeKey] = nodeId;
                return true;
            case { Valid: false }:
                return false;
        }

        return !RequiresCertificate(context.ConnectionId, http?.Connection.RemoteIpAddress)
            && IsAuthorized(ResolvePresentedSecret(context), context.ConnectionId, http?.Connection.RemoteIpAddress);
    }

    /// <summary>
    /// Phase 122. The same secret over plain HTTP, for the sealed node's relay — header only: a secret in a query
    /// string is a secret in every proxy's access log, and the relay's URLs are long-lived paths.
    /// Phase 127: or a certificate's proof, checked first.
    /// </summary>
    public bool IsAuthorized(HttpContext httpContext)
    {
        switch (Certified(httpContext, httpContext.TraceIdentifier))
        {
            case { Valid: true, NodeId: { } nodeId }:
                httpContext.Items[CertifiedNodeKey] = nodeId;
                return true;
            case { Valid: false }:
                return false;
        }

        if (RequiresCertificate(httpContext.TraceIdentifier, httpContext.Connection.RemoteIpAddress))
        {
            return false;
        }

        var presented = httpContext.Request.Headers.TryGetValue(EnrollmentSecretHeader, out var values)
            ? values.ToString().Trim()
            : null;

        return IsAuthorized(presented, httpContext.TraceIdentifier, httpContext.Connection.RemoteIpAddress);
    }

    // Phase 127 D4: a presented certificate is decided on its own — a failed proof never falls back to the secret, so a
    // revoked node with the shared secret still in its config is refused. Null: no certificate presented.
    private ProofResult? Certified(HttpContext? http, string connectionId)
    {
        if (certificates is not { Enabled: true } authority || http is null)
        {
            return null;
        }

        var result = authority.Verify(name => http.Request.Headers.TryGetValue(name, out var v) ? v.ToString() : null);

        if (result is { Valid: false })
        {
            logger.LogWarning(
                "Refusing node connection {ConnectionId} from {RemoteIp}: {Reason}",
                connectionId,
                http.Connection.RemoteIpAddress,
                result.Reason);
        }

        return result;
    }

    private bool RequiresCertificate(string connectionId, System.Net.IPAddress? remoteIp)
    {
        if (certificates is not { Require: true } authority)
        {
            return false;
        }

        authority.CountRefused();
        logger.LogWarning(
            "Refusing node connection {ConnectionId} from {RemoteIp}: this coordinator requires a node certificate (Identity:Require); "
            + "enroll the node with Coordinator:EnrollmentToken",
            connectionId,
            remoteIp);
        return true;
    }

    private bool IsAuthorized(string? presented, string connectionId, System.Net.IPAddress? remoteIp)
    {
        var expected = options.CurrentValue.NodeEnrollmentSecret;

        if (string.IsNullOrEmpty(expected))
        {
            logger.LogError(
                "Refusing node connection {ConnectionId} from {RemoteIp}: no enrollment secret is configured",
                connectionId,
                remoteIp);
            return false;
        }

        if (string.IsNullOrEmpty(presented))
        {
            logger.LogWarning(
                "Refusing node connection {ConnectionId} from {RemoteIp}: missing enrollment secret",
                connectionId,
                remoteIp);
            return false;
        }

        Span<byte> presentedHash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(presented), presentedHash);

        Span<byte> expectedHash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(expected), expectedHash);

        if (!CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash))
        {
            logger.LogWarning(
                "Refusing node connection {ConnectionId} from {RemoteIp}: invalid enrollment secret",
                connectionId,
                remoteIp);
            return false;
        }

        return true;
    }

    private static string? ResolvePresentedSecret(HubCallerContext context)
    {
        var httpContext = context.GetHttpContext();

        if (httpContext is null)
        {
            return null;
        }

        if (httpContext.Request.Headers.TryGetValue(EnrollmentSecretHeader, out var headerValues))
        {
            var headerSecret = headerValues.ToString();
            if (!string.IsNullOrWhiteSpace(headerSecret))
            {
                return headerSecret.Trim();
            }
        }

        if (httpContext.Request.Query.TryGetValue("enrollmentSecret", out var queryValues))
        {
            var querySecret = queryValues.ToString();
            if (!string.IsNullOrWhiteSpace(querySecret))
            {
                return querySecret.Trim();
            }
        }

        return null;
    }
}
