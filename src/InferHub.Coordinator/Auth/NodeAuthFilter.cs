using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Auth;

public sealed class NodeAuthFilter(
    IOptionsMonitor<ApiKeyOptions> options,
    ILogger<NodeAuthFilter> logger)
{
    public const string EnrollmentSecretHeader = "X-Node-Enrollment-Secret";

    public bool IsAuthorized(HubCallerContext context)
        => IsAuthorized(ResolvePresentedSecret(context), context.ConnectionId, context.GetHttpContext()?.Connection.RemoteIpAddress);

    /// <summary>
    /// Phase 122. The same secret over plain HTTP, for the sealed node's relay — header only: a secret in a query
    /// string is a secret in every proxy's access log, and the relay's URLs are long-lived paths.
    /// </summary>
    public bool IsAuthorized(HttpContext httpContext)
    {
        var presented = httpContext.Request.Headers.TryGetValue(EnrollmentSecretHeader, out var values)
            ? values.ToString().Trim()
            : null;

        return IsAuthorized(presented, httpContext.TraceIdentifier, httpContext.Connection.RemoteIpAddress);
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
