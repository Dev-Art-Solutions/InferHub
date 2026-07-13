using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Auth;

public sealed class BearerApiKeyMiddleware(
    RequestDelegate next,
    IOptionsMonitor<ApiKeyOptions> options,
    ILogger<BearerApiKeyMiddleware> logger)
{
    public const string OpenAiPathPrefix = "/v1";

    private const string OllamaPathPrefix = "/api";
    private const string BearerPrefix = "Bearer ";

    public async Task InvokeAsync(HttpContext context)
    {
        // Both client-facing dialects are guarded by the same inference-key scope. A new
        // surface that isn't listed here ships unauthenticated — add the prefix, not a
        // second middleware.
        if (!IsGuardedPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        // Admin routes have their own middleware with the admin-key scope.
        if (context.Request.Path.StartsWithSegments(AdminApiKeyMiddleware.AdminPathPrefix))
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
            await WriteUnauthorizedAsync(context, "missing bearer token");
            logger.LogWarning(
                "Rejected request to {Path} from {RemoteIp}: missing bearer token",
                context.Request.Path,
                remoteIp);
            return;
        }

        if (!IsTokenAccepted(token, settings.ApiKeys))
        {
            await WriteUnauthorizedAsync(context, "invalid bearer token");
            logger.LogWarning(
                "Rejected request to {Path} from {RemoteIp}: invalid bearer token",
                context.Request.Path,
                remoteIp);
            return;
        }

        await next(context);
    }

    private static bool IsGuardedPath(PathString path)
    {
        return path.StartsWithSegments(OllamaPathPrefix)
            || path.StartsWithSegments(OpenAiPathPrefix);
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

    private static bool IsTokenAccepted(string presented, IReadOnlyList<string> apiKeys)
    {
        if (apiKeys.Count == 0)
        {
            return false;
        }

        Span<byte> presentedHash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(presented), presentedHash);

        Span<byte> keyHash = stackalloc byte[32];
        var accepted = false;

        foreach (var key in apiKeys)
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

        // Each surface rejects in its own dialect — an OpenAI SDK parses error.error.message
        // and will surface a useless "unknown error" against the Ollama envelope.
        if (context.Request.Path.StartsWithSegments(OpenAiPathPrefix))
        {
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    message,
                    type = "invalid_request_error",
                    param = (string?)null,
                    code = "invalid_api_key"
                }
            });
            return;
        }

        await context.Response.WriteAsJsonAsync(new { error = message });
    }
}
