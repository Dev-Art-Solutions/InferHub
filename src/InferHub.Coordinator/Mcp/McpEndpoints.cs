using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Mcp;

/// <summary>
/// Phase 154: <c>/mcp</c>, the Streamable HTTP transport of the Model Context Protocol. JSON-RPC 2.0 in a POST; every
/// answer is one <c>application/json</c> body (the spec lets a server choose JSON over SSE per request, and no tool here has
/// progress worth streaming — 154 D1). <c>GET</c> is 405: this server never speaks first. <c>DELETE</c> ends a session.
/// </summary>
public static class McpEndpoints
{
    public const string Path = "/mcp";
    public const string SessionHeader = "Mcp-Session-Id";
    public const string ProtocolHeader = "MCP-Protocol-Version";

    /// <summary>Newest first; <c>initialize</c> answers the client's version when it is here, else the newest.</summary>
    public static readonly IReadOnlyList<string> ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26"];

    private const int MaxBodyBytes = 4 * 1024 * 1024;

    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, HandlePostAsync).DisableAntiforgery();
        app.MapGet(Path, (HttpContext context) =>
        {
            context.Response.Headers.Allow = "POST, DELETE";
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        });
        app.MapDelete(Path, (HttpContext context, McpSessions sessions) =>
        {
            if (Guard(context, out var caller) is { } refused)
            {
                return refused;
            }

            return sessions.End(context.Request.Headers[SessionHeader].ToString(), caller!.Who)
                ? Results.NoContent()
                : Results.NotFound();
        });
        return app;
    }

    private static IResult? Guard(HttpContext context, out McpCaller? caller)
    {
        caller = null;

        // The spec's DNS-rebinding defence: a browser page on another origin may not drive a hub on this machine's network.
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !OriginAllowed(context, origin))
        {
            return Results.Json(new { error = $"origin '{origin}' may not call {Path}; add it to Mcp:AllowedOrigins" }, statusCode: 403);
        }

        caller = McpCaller.Resolve(context, OAuthEndpoints.ResourceUrl(context));
        if (caller is null)
        {
            // Where to sign in, for a client that can (RFC 9728 5.1). Without OAuth the header still says Bearer.
            context.Response.Headers.WWWAuthenticate = OAuthOn(context)
                ? $"Bearer resource_metadata=\"{OAuthEndpoints.ProtectedResourceUrl(context)}\", scope=\"{OAuthStore.Scope}\""
                : "Bearer";
            return Results.Json(new { error = "missing or invalid bearer token" }, statusCode: 401);
        }

        context.Items[BearerApiKeyMiddleware.ClientItemKey] = caller.Client;
        context.Items[AuditMiddleware.WhoItemKey] = caller.Who;
        return null;
    }

    internal static bool OAuthOn(HttpContext context)
        => context.RequestServices.GetRequiredService<IOptionsMonitor<McpOptions>>().CurrentValue.OAuth.Enabled
           && context.RequestServices.GetService<Users.UserDirectory>() is not null;

    private static bool OriginAllowed(HttpContext context, string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (string.Equals(parsed.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var options = context.RequestServices.GetRequiredService<IOptionsMonitor<McpOptions>>().CurrentValue;
        if (Uri.TryCreate(OAuthEndpoints.BaseUrl(context), UriKind.Absolute, out var own)
            && string.Equals(own.GetLeftPart(UriPartial.Authority), parsed.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return options.AllowedOrigins.Any(allowed => string.Equals(allowed.Trim().TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IResult> HandlePostAsync(HttpContext context, McpSessions sessions, McpTools tools)
    {
        if (Guard(context, out var caller) is { } refused)
        {
            return refused;
        }

        var version = context.Request.Headers[ProtocolHeader].ToString();
        if (version.Length > 0 && !ProtocolVersions.Contains(version))
        {
            return Results.Json(new { error = $"unsupported {ProtocolHeader} '{version}'; this server speaks {string.Join(", ", ProtocolVersions)}" }, statusCode: 400);
        }

        // A session id this hub did not issue, issued to someone else, or ended: 404, which tells the client to initialize
        // again (the spec's own signal). No id at all is a stateless client, and every request carries its own key.
        var sessionId = context.Request.Headers[SessionHeader].ToString();
        if (sessionId.Length > 0 && !sessions.Touch(sessionId, caller!.Who))
        {
            return Results.Json(Error(null, -32001, "session not found; initialize again"), statusCode: 404);
        }

        JsonNode? message;
        try
        {
            if (context.Request.ContentLength > MaxBodyBytes)
            {
                return Results.Json(Error(null, -32600, $"request larger than {MaxBodyBytes} bytes"), statusCode: 413);
            }

            message = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        }
        catch (JsonException ex)
        {
            return Results.Json(Error(null, -32700, "parse error: " + ex.Message), statusCode: 400);
        }

        // Phase 130: a caller's tools run in its organisation, like its requests on /api and /v1 do.
        if (context.RequestServices.GetService<OrganisationPolicy>() is { Enabled: true } policy)
        {
            OrganisationScope.Begin(policy, policy.OrganisationOf(caller!.Client.Organisation));
        }

        if (message is JsonArray batch)
        {
            // 2025-03-26 allowed batches and later versions removed them; answering one costs nothing and refuses nobody.
            var answers = new JsonArray();
            foreach (var item in batch)
            {
                if (await HandleOneAsync(context, item, caller!, sessions, tools) is { } answer)
                {
                    answers.Add(answer);
                }
            }

            return answers.Count == 0 ? Results.Accepted() : Json(answers);
        }

        var single = await HandleOneAsync(context, message, caller!, sessions, tools);
        return single is null ? Results.Accepted() : Json(single);
    }

    /// <summary>One JSON-RPC message. Null for a notification or a client's response: those are acknowledged with 202.</summary>
    private static async Task<JsonNode?> HandleOneAsync(HttpContext context, JsonNode? node, McpCaller caller, McpSessions sessions, McpTools tools)
    {
        if (node is not JsonObject message || Str(message["jsonrpc"]) != "2.0")
        {
            return Error(null, -32600, "not a JSON-RPC 2.0 message");
        }

        var id = message["id"]?.DeepClone();
        var method = Str(message["method"]);

        if (method is null)
        {
            return null; // a response to something we never sent; nothing to do
        }

        if (id is null)
        {
            return null; // notifications/initialized, notifications/cancelled, …
        }

        var parameters = message["params"] as JsonObject;

        try
        {
            switch (method)
            {
                case "initialize":
                {
                    var asked = Str(parameters?["protocolVersion"]);
                    var agreed = asked is not null && ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0];
                    context.Response.Headers[SessionHeader] = sessions.Start(caller.Who, agreed);
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = agreed,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = "inferhub",
                            ["title"] = "InferHub",
                            ["version"] = typeof(McpEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0"
                        },
                        ["instructions"] = "InferHub is a self-hosted inference mesh. Call list_models to see every model you may use "
                            + "(local nodes, node groups, and cloud providers the operator connected), then chat with one by name. "
                            + "embed returns vectors; fleet_status (operators) shows the nodes."
                    });
                }

                case "ping":
                    return Result(id, new JsonObject());

                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = tools.List(caller) });

                case "tools/call":
                {
                    var name = Str(parameters?["name"]);
                    if (string.IsNullOrEmpty(name))
                    {
                        return Error(id, -32602, "params.name is required");
                    }

                    var result = await tools.CallAsync(context, caller, name, parameters?["arguments"] as JsonObject ?? new JsonObject(), context.RequestAborted);
                    return result is null ? Error(id, -32602, $"unknown tool '{name}'") : Result(id, result);
                }

                default:
                    return Error(id, -32601, $"method '{method}' not found");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
        {
            return Error(id, -32602, "invalid params: " + ex.Message);
        }
    }

    internal static string? Str(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject Result(JsonNode? id, JsonObject result)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static IResult Json(JsonNode node) => Results.Text(node.ToJsonString(), "application/json");
}

/// <summary>
/// The sessions <c>initialize</c> opened: an id bound to the caller who opened it (a session id is not a credential, and a
/// second caller presenting it gets 404), forgotten after a day idle. In memory: a restarted hub answers 404 and the client
/// initializes again, which the spec makes every client handle.
/// </summary>
public sealed class McpSessions(TimeProvider? time = null)
{
    public const int MaxSessions = 10_000;
    private static readonly TimeSpan Idle = TimeSpan.FromDays(1);
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (string Who, string Version, DateTimeOffset Seen)> sessions = new(StringComparer.Ordinal);

    public int Count => sessions.Count;

    public string Start(string who, string version)
    {
        var now = time.GetUtcNow();
        if (sessions.Count >= MaxSessions)
        {
            foreach (var (id, session) in sessions.OrderBy(s => s.Value.Seen).Take(sessions.Count / 10 + 1).ToArray())
            {
                sessions.TryRemove(id, out _);
            }
        }

        var sessionId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        sessions[sessionId] = (who, version, now);
        return sessionId;
    }

    public bool Touch(string sessionId, string who)
    {
        if (!sessions.TryGetValue(sessionId, out var session) || session.Who != who)
        {
            return false;
        }

        var now = time.GetUtcNow();
        if (now - session.Seen > Idle)
        {
            sessions.TryRemove(sessionId, out _);
            return false;
        }

        sessions[sessionId] = session with { Seen = now };
        return true;
    }

    public bool End(string sessionId, string who)
        => sessionId.Length > 0 && sessions.TryGetValue(sessionId, out var session) && session.Who == who && sessions.TryRemove(sessionId, out _);
}
