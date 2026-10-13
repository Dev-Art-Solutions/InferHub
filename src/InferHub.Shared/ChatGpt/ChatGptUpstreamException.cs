using System.Text.Json;
using InferHub.Shared.Upstream;

namespace InferHub.Shared.ChatGpt;

/// <summary>
/// The Responses API refused or failed a ChatGPT-plan request (phase 103). <see cref="Code"/> is the
/// vendor's own <c>error.code</c> where it sent one — <c>subscription_sharing_usage_limit_exceeded</c>
/// and its siblings — because those are what decide the recovery, and a 502 with the code flattened
/// out of it sends an operator to the wrong fix.
/// </summary>
public sealed class ChatGptUpstreamException(int statusCode, string? code, string message, string? requestId = null)
    : UpstreamDialectException(statusCode, message)
{
    public string? Code { get; } = code;

    public string? RequestId { get; } = requestId;

    /// <summary>The HTTP status the vendor documents for a code that arrives inside a stream.</summary>
    public static int StatusFor(string? code) => code switch
    {
        "subscription_sharing_usage_limit_exceeded" => 429,
        "subscription_sharing_user_not_eligible" => 403,
        "subscription_sharing_route_not_supported" => 403,
        "chatpass_v2_scope_not_authorized" or "chatpass_v2_invalid_authorization_context" => 403,
        "subscription_sharing_invalid_user" => 401,
        "subscription_sharing_unsupported_capability" => 400,
        "subscription_sharing_usage_unavailable" or "subscription_sharing_user_unavailable" => 503,
        _ => 502
    };

    /// <summary>
    /// The vendor's sentence plus what to do about it, from the recovery table in OpenAI's own
    /// documentation. The code is kept in the text so a log line or a console cell is searchable.
    /// </summary>
    public static string Describe(string? code, string? message, string? param = null)
    {
        var what = string.IsNullOrWhiteSpace(message) ? "the request failed" : message.Trim();
        var advice = code switch
        {
            "subscription_sharing_usage_limit_exceeded"
                => "the ChatGPT plan's usage limit for this app was reached; ChatGPT Settings → Usage shows it",
            "subscription_sharing_user_not_eligible"
                => "ChatGPT plan usage is not available for this user, workspace or policy",
            "subscription_sharing_usage_unavailable" or "subscription_sharing_user_unavailable"
                => "temporarily unavailable; retry later",
            "subscription_sharing_unsupported_capability"
                => $"this request uses something ChatGPT plan usage does not support{(param is null ? "" : $" ({param})")}",
            "subscription_sharing_route_not_supported"
                => "this endpoint is not available through ChatGPT plan usage",
            "subscription_sharing_invalid_user"
                => "the account could not be validated; sign in again",
            "chatpass_v2_scope_not_authorized" or "chatpass_v2_invalid_authorization_context"
                => "the granted permissions do not cover this request; reconnect and allow ChatGPT plan usage",
            _ => null
        };

        var tag = string.IsNullOrWhiteSpace(code) ? string.Empty : $" [{code}]";

        return advice is null ? $"ChatGPT: {what}{tag}" : $"ChatGPT: {what}{tag} — {advice}";
    }

    /// <summary>
    /// A refusal that arrived before a stream opened. The body is either the Responses error object
    /// or a bare <c>{"detail": "…"}</c> from direct-route admission, which the vendor says to treat
    /// as diagnostic text rather than a code.
    /// </summary>
    public static async Task<ChatGptUpstreamException> FromResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
        string? code = null;
        string? message = null;
        string? param = null;

        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            (code, message, param) = Read(document.RootElement);
        }
        catch (JsonException)
        {
        }

        message ??= status switch
        {
            401 => "the account's signed identity or plan permission was not accepted",
            403 => "a policy or permission check (for example the serving region) refused the request",
            503 => "direct routing is unavailable right now",
            _ => $"the upstream answered {status}"
        };

        var text = Describe(code, message, param) + (requestId is null ? string.Empty : $" (request {requestId})");
        return new ChatGptUpstreamException(status, code, text, requestId);
    }

    /// <summary><c>{"error":{code,message,param}}</c>, <c>{code,message}</c> or <c>{"detail":"…"}</c>.</summary>
    internal static (string? Code, string? Message, string? Param) Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        var error = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;

        var code = Str(error, "code");
        var message = Str(error, "message") ?? Str(root, "detail");
        var param = Str(error, "param");

        return (code, message, param);
    }

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
