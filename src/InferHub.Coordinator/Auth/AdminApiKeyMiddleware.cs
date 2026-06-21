using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Auth;

public sealed class AdminApiKeyMiddleware(
    RequestDelegate next,
    IOptionsMonitor<ApiKeyOptions> options,
    ILogger<AdminApiKeyMiddleware> logger)
{
    public const string AdminPathPrefix = "/api/admin";

    private const string BearerPrefix = "Bearer ";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(AdminPathPrefix))
        {
            await next(context);
            return;
        }

        var settings = options.CurrentValue;
        var remoteIp = context.Connection.RemoteIpAddress;
        var isLoopback = remoteIp is not null && System.Net.IPAddress.IsLoopback(remoteIp);

        if (isLoopback && !settings.RequireAuthForLoopback)
        {
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

        if (!IsTokenAccepted(token, settings.AdminApiKeys))
        {
            await WriteUnauthorizedAsync(context, "invalid admin bearer token");
            logger.LogWarning(
                "Rejected admin request to {Path} from {RemoteIp}: invalid bearer token",
                context.Request.Path,
                remoteIp);
            return;
        }

        await next(context);
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

    private static bool IsTokenAccepted(string presented, IReadOnlyList<string> adminApiKeys)
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
