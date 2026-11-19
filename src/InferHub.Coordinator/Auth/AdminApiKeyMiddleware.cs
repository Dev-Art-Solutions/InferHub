using System.Security.Cryptography;
using System.Text;
using InferHub.Coordinator.Observability;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Auth;

public sealed class AdminApiKeyMiddleware(
    RequestDelegate next,
    IOptionsMonitor<ApiKeyOptions> options,
    IOptionsMonitor<MetricsOptions> metricsOptions,
    ILogger<AdminApiKeyMiddleware> logger)
{
    public const string AdminPathPrefix = "/api/admin";

    /// <summary>
    /// The scrape endpoint (phase 28). It is not under <c>/api/admin</c> — Prometheus convention
    /// is a bare <c>/metrics</c> and fighting it would earn nothing — so the prefix check is a
    /// small set rather than one constant. It is admin-guarded by default and open only when
    /// <c>Metrics:OpenScrape</c> says so.
    /// </summary>
    public const string MetricsPath = "/metrics";

    private const string BearerPrefix = "Bearer ";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!Guards(context.Request.Path))
        {
            await next(context);
            return;
        }

        var settings = options.CurrentValue;
        var remoteIp = context.Connection.RemoteIpAddress;
        var isLoopback = remoteIp is not null && System.Net.IPAddress.IsLoopback(remoteIp);

        if (isLoopback && !settings.RequireAuthForLoopback)
        {
            context.Items[Services.AuditMiddleware.WhoItemKey] = "loopback";
            await next(context);
            return;
        }

        var token = ExtractBearerToken(context.Request.Headers.Authorization);

        if (token is null)
        {
            await WriteUnauthorizedAsync(context, "missing admin bearer token");
            logger.LogWarning(
                "Rejected admin request to {Path} from {RemoteIp}: missing bearer token",
                context.Request.Path,
                remoteIp);
            return;
        }

        if (!IsTokenAccepted(token, settings.AdminApiKeys)
            && context.RequestServices?.GetService<Users.UserDirectory>()?.Resolve(token) is { } user)
        {
            // Phase 154: a user whose role reads or writes the admin API. People are their own permission (154 D2).
            if (WhyRefused(context.Request, user) is { } refusal)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = refusal });
                logger.LogWarning("Refused admin request to {Path} by {User}: {Reason}", context.Request.Path, user.Who, refusal);
                return;
            }

            context.Items[Services.AuditMiddleware.WhoItemKey] = user.Who;
            await next(context);
            return;
        }

        if (!IsTokenAccepted(token, settings.AdminApiKeys))
        {
            await WriteUnauthorizedAsync(context, "invalid admin bearer token");
            logger.LogWarning(
                "Rejected admin request to {Path} from {RemoteIp}: invalid bearer token",
                context.Request.Path,
                remoteIp);
            return;
        }

        context.Items[Services.AuditMiddleware.WhoItemKey] = Fingerprint(token);
        await next(context);
    }

    /// <summary>
    /// Phase 125. Which admin key was used, for the audit trail: <c>admin:</c> and the first eight hex digits of its
    /// SHA-256. Enough to tell two operators' keys apart, nothing a key can be recovered from (125 D1).
    /// </summary>
    internal static string Fingerprint(string token)
        => "admin:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..8];

    internal static string? WhyRefused(HttpRequest request, Users.AuthenticatedUser user)
    {
        var reading = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);

        if (request.Path.StartsWithSegments("/api/admin/users") || request.Path.StartsWithSegments("/api/admin/roles"))
        {
            return user.Can(Users.Permissions.Users) ? null : $"role '{user.Role}' may not manage users";
        }

        if (reading)
        {
            return user.Can(Users.Permissions.AdminRead) || user.Can(Users.Permissions.AdminWrite) ? null : $"role '{user.Role}' may not read the admin API";
        }

        return user.Can(Users.Permissions.AdminWrite) ? null : $"role '{user.Role}' may not change anything through the admin API";
    }

    private bool Guards(PathString path)
    {
        if (path.StartsWithSegments(AdminPathPrefix))
        {
            return true;
        }

        return path.StartsWithSegments(MetricsPath) && !metricsOptions.CurrentValue.OpenScrape;
    }

    private static string? ExtractBearerToken(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }

        if (!authorizationHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorizationHeader[BearerPrefix.Length..].Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    /// <summary>
    /// Whether a presented token is one of the admin keys, in constant time. Internal rather than
    /// private since phase 60: <see cref="BearerApiKeyMiddleware"/> asks the same question for
    /// <c>/api/status</c>, and a second copy of a fixed-time comparison is a second place to get it
    /// wrong.
    /// </summary>
    internal static bool IsTokenAccepted(string presented, IReadOnlyList<string> adminApiKeys)
    {
        if (adminApiKeys.Count == 0)
        {
            return false;
        }

        Span<byte> presentedHash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(presented), presentedHash);

        Span<byte> keyHash = stackalloc byte[32];
        var accepted = false;

        foreach (var key in adminApiKeys)
        {
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            SHA256.HashData(Encoding.UTF8.GetBytes(key), keyHash);

            if (CryptographicOperations.FixedTimeEquals(presentedHash, keyHash))
            {
                accepted = true;
            }
        }

        return accepted;
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
}
