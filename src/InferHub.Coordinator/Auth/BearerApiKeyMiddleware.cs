using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Auth;

public sealed class BearerApiKeyMiddleware(
    RequestDelegate next,
    IOptionsMonitor<ApiKeyOptions> options,
    IClientRegistry clients,
    ILogger<BearerApiKeyMiddleware> logger)
{
    public const string OpenAiPathPrefix = "/v1";

    /// <summary>The <see cref="ResolvedClient"/> for this request, set on every guarded path.</summary>
    public const string ClientItemKey = "InferHub.Client";

    private const string OllamaPathPrefix = "/api";
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// The read-only fleet view. Guarded by the client scope like everything else under
    /// <c>/api</c>, and — since phase 60 — readable with an admin key as well.
    /// </summary>
    private const string StatusPath = "/api/status";

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

        var token = ExtractBearerToken(context.Request.Headers.Authorization);

        if (isLoopback && !settings.RequireAuthForLoopback)
        {
            // Loopback is exempt from *rejection*, not from identity: a valid named key still
            // resolves (so quotas can be exercised locally), anything else stays anonymous.
            context.Items[ClientItemKey] = (token is not null ? clients.Resolve(token) : null)
                ?? ResolvedClient.Anonymous;
            await next(context);
            return;
        }

        if (token is null)
        {
            await WriteUnauthorizedAsync(context, "missing bearer token");
            logger.LogWarning(
                "Rejected request to {Path} from {RemoteIp}: missing bearer token",
                context.Request.Path,
                remoteIp);
            return;
        }

        var client = clients.Resolve(token);

        // Phase 60, found by pulling the image. `/api/status` is the fleet view the console polls
        // every five seconds, and it sat behind the CLIENT scope alone — so an operator holding only
        // an admin key could not read it, and the console (which sends nothing at all on that call)
        // could not read it from anywhere except a loopback hub. Every containerised deployment sees
        // the bridge gateway rather than a loopback address, so this was 401 on the one surface it
        // matters on, in every real deployment, since the console existed.
        //
        // Widening it grants nothing new: an admin key already reads `/api/admin/nodes`, which
        // carries the same fleet and more. It stays scoped to this one read-only path — an admin key
        // must still never run inference, which is what the table in the root CLAUDE.md says and
        // what `AnAdminKeyStillCannotRunInference` holds.
        if (client is null
            && context.Request.Path.StartsWithSegments(StatusPath)
            && AdminApiKeyMiddleware.IsTokenAccepted(token, settings.AdminApiKeys))
        {
            context.Items[ClientItemKey] = ResolvedClient.Anonymous;
            await next(context);
            return;
        }

        if (client is null)
        {
            await WriteUnauthorizedAsync(context, "invalid bearer token");
            logger.LogWarning(
                "Rejected request to {Path} from {RemoteIp}: invalid bearer token",
                context.Request.Path,
                remoteIp);
            return;
        }

        context.Items[ClientItemKey] = client;
        await next(context);
    }

    /// <summary>The resolved client for this request; anonymous when nothing resolved it.</summary>
    public static ResolvedClient ClientOf(HttpContext context)
        => context.Items.TryGetValue(ClientItemKey, out var value) && value is ResolvedClient client
            ? client
            : ResolvedClient.Anonymous;

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
