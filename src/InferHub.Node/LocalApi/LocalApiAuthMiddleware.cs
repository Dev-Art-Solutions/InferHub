using System.Security.Cryptography;
using System.Text;
using InferHub.Node.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace InferHub.Node.LocalApi;

/// <summary>
/// The bearer guard for solo mode. Deliberately a thinner cousin of the coordinator's
/// <c>BearerApiKeyMiddleware</c>: same prefix-set discipline, same loopback stance, same
/// per-dialect rejection body — but no named clients, no quotas and no collection scopes, because
/// solo mode has no fleet to meter and one operator.
/// </summary>
/// <remarks>
/// <para>
/// The prefix set is the load-bearing part, and it is the same trap phase-21 D2 recorded: a new
/// client-facing route added under a prefix that is not listed here ships unauthenticated. Add the
/// prefix; do not add a second middleware.
/// </para>
/// <para>
/// <c>/health</c> is open on purpose, exactly as it is on the hub, so a monitor can poll it.
/// </para>
/// </remarks>
public sealed class LocalApiAuthMiddleware(
    RequestDelegate next,
    IOptions<LocalApiOptions> options,
    ILogger<LocalApiAuthMiddleware> logger)
{
    private const string BearerPrefix = "Bearer ";
    private const string OllamaPathPrefix = "/api";
    private const string OpenAiPathPrefix = "/v1";

    private readonly LocalApiOptions settings = options.Value;

    private readonly byte[][] keyHashes =
    [
        .. options.Value.ApiKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())))
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsGuardedPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        var remoteIp = context.Connection.RemoteIpAddress;
        var isLoopback = remoteIp is not null && System.Net.IPAddress.IsLoopback(remoteIp);

        if (isLoopback && !settings.RequireAuthForLoopback)
        {
            await next(context);
            return;
        }

        // Consented to explicitly, and the validator refused to boot without either this or a key
        // on a non-loopback bind. The startup warning is where the noise belongs; per-request
        // logging here would drown the log of a working deployment.
        if (settings.AllowAnonymous)
        {
            await next(context);
            return;
        }

        // No keys configured on a loopback-only bind: nothing to check against, and the validator
        // already established the address is not reachable from elsewhere.
        if (keyHashes.Length == 0)
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

        if (!IsKnownKey(token))
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

    private bool IsKnownKey(string token)
    {
        var candidate = SHA256.HashData(Encoding.UTF8.GetBytes(token));

        // Fixed-time compare, and no early exit on the first mismatch — the house pattern for every
        // key check in this codebase.
        var matched = false;

        foreach (var known in keyHashes)
        {
            matched |= CryptographicOperations.FixedTimeEquals(known, candidate);
        }

        return matched;
    }

    private static bool IsGuardedPath(PathString path)
        => path.StartsWithSegments(OllamaPathPrefix) || path.StartsWithSegments(OpenAiPathPrefix);

    private static string? ExtractBearerToken(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader)
            || !authorizationHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorizationHeader[BearerPrefix.Length..].Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    private static async Task WriteUnauthorizedAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";

        // Each surface rejects in its own dialect — an OpenAI SDK parses error.error.message and
        // will surface a useless "unknown error" against the Ollama envelope. Same as the hub, so a
        // client that handles one handles the other.
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
