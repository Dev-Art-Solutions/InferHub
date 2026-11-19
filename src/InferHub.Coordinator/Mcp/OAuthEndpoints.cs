using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using InferHub.Coordinator.Users;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Mcp;

/// <summary>
/// Phase 154: the hub as its own OAuth 2.1 authorization server for <c>/mcp</c> — what a claude.ai or ChatGPT connector
/// needs to sign in (154 D5). Protected-resource metadata (RFC 9728), server metadata (RFC 8414), open dynamic registration
/// (RFC 7591), the code flow with PKCE S256 only, refresh with rotation, revocation (RFC 7009). The person proves who they
/// are with one of their InferHub keys on the authorize page; there are no passwords to store or reset.
/// </summary>
public static class OAuthEndpoints
{
    public sealed record RegisterRequest(
        [property: JsonPropertyName("redirect_uris")] List<string>? RedirectUris,
        [property: JsonPropertyName("client_name")] string? ClientName,
        [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod);

    public static IEndpointRouteBuilder MapOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // The path-suffixed form is what a client derives from https://hub/mcp (RFC 9728 3.1); the bare one is for older ones.
        app.MapGet("/.well-known/oauth-protected-resource", ProtectedResource);
        app.MapGet("/.well-known/oauth-protected-resource/mcp", ProtectedResource);
        app.MapGet("/.well-known/oauth-authorization-server", ServerMetadata);
        app.MapGet("/.well-known/oauth-authorization-server/mcp", ServerMetadata);
        app.MapPost("/oauth/register", Register);
        app.MapGet("/oauth/authorize", AuthorizePage);
        app.MapPost("/oauth/authorize", AuthorizeSubmit).DisableAntiforgery();
        app.MapPost("/oauth/token", Token).DisableAntiforgery();
        app.MapPost("/oauth/revoke", Revoke).DisableAntiforgery();
        return app;
    }

    public static string BaseUrl(HttpContext context)
    {
        var configured = context.RequestServices.GetRequiredService<IOptionsMonitor<McpOptions>>().CurrentValue.PublicUrl;
        return !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim().TrimEnd('/')
            : $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}".TrimEnd('/');
    }

    public static string ResourceUrl(HttpContext context) => BaseUrl(context) + "/mcp";

    public static string ProtectedResourceUrl(HttpContext context) => BaseUrl(context) + "/.well-known/oauth-protected-resource/mcp";

    private static IResult ProtectedResource(HttpContext context) => Results.Json(new
    {
        resource = ResourceUrl(context),
        authorization_servers = new[] { BaseUrl(context) },
        scopes_supported = new[] { OAuthStore.Scope },
        bearer_methods_supported = new[] { "header" },
        resource_name = "InferHub"
    });

    private static IResult ServerMetadata(HttpContext context)
    {
        var issuer = BaseUrl(context);
        return Results.Json(new
        {
            issuer,
            authorization_endpoint = issuer + "/oauth/authorize",
            token_endpoint = issuer + "/oauth/token",
            registration_endpoint = issuer + "/oauth/register",
            revocation_endpoint = issuer + "/oauth/revoke",
            response_types_supported = new[] { "code" },
            response_modes_supported = new[] { "query" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none", "client_secret_post", "client_secret_basic" },
            revocation_endpoint_auth_methods_supported = new[] { "none", "client_secret_post", "client_secret_basic" },
            scopes_supported = new[] { OAuthStore.Scope },
            authorization_response_iss_parameter_supported = true
        });
    }

    private static IResult Register(RegisterRequest? body, OAuthStore store)
    {
        var (client, secret, error) = store.Register(body?.RedirectUris, body?.ClientName, body?.TokenEndpointAuthMethod);
        if (error is not null)
        {
            return Results.Json(new { error = error.Error, error_description = error.Description }, statusCode: 400);
        }

        return Results.Json(new
        {
            client_id = client!.Id,
            client_id_issued_at = client.CreatedUtc.ToUnixTimeSeconds(),
            client_name = client.Name,
            redirect_uris = client.RedirectUris,
            token_endpoint_auth_method = client.AuthMethod,
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            client_secret = secret,
            client_secret_expires_at = secret is null ? (long?)null : 0
        }, statusCode: 201);
    }

    private sealed record AuthorizeRequest(
        OAuthClient Client, string RedirectUri, string? State, string Challenge, string Resource, string? Scope);

    /// <summary>
    /// Checks an authorization request. Until the client and its redirect are known good an error is a page — never a
    /// redirect to an address nobody registered (RFC 6749 4.1.2.1); after that it goes back to the client.
    /// </summary>
    private static (AuthorizeRequest? Request, IResult? Failure) Check(HttpContext context, Func<string, string?> read, OAuthStore store)
    {
        var client = store.GetClient(read("client_id"));
        if (client is null)
        {
            return (null, Page("Unknown client", "<p>This client is not registered with this hub. Ask the app to connect again.</p>", 400));
        }

        var redirect = read("redirect_uri");
        if (string.IsNullOrEmpty(redirect) && client.RedirectUris.Count == 1)
        {
            redirect = client.RedirectUris[0];
        }

        if (string.IsNullOrEmpty(redirect) || !client.RedirectUris.Contains(redirect, StringComparer.Ordinal))
        {
            return (null, Page("Unknown redirect", "<p>The address this app asked to return to is not one it registered.</p>", 400));
        }

        var state = read("state");
        IResult Back(string error, string description) => Results.Redirect(WithQuery(redirect, new()
        {
            ["error"] = error,
            ["error_description"] = description,
            ["state"] = state,
            ["iss"] = BaseUrl(context)
        }));

        if (read("response_type") != "code")
        {
            return (null, Back("unsupported_response_type", "only response_type=code"));
        }

        var challenge = read("code_challenge");
        if (string.IsNullOrEmpty(challenge) || read("code_challenge_method") != "S256")
        {
            return (null, Back("invalid_request", "PKCE with code_challenge_method=S256 is required"));
        }

        var resource = read("resource");
        var ours = ResourceUrl(context);
        if (!string.IsNullOrEmpty(resource)
            && !string.Equals(resource.TrimEnd('/'), ours, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resource.TrimEnd('/'), BaseUrl(context), StringComparison.OrdinalIgnoreCase))
        {
            return (null, Back("invalid_target", $"this hub serves {ours}"));
        }

        return (new AuthorizeRequest(client, redirect, state, challenge, ours, read("scope")), null);
    }

    private static IResult AuthorizePage(HttpContext context, OAuthStore store)
    {
        var query = context.Request.Query;
        var (request, failure) = Check(context, name => query.TryGetValue(name, out var v) ? v.ToString() : null, store);
        return failure ?? Form(context, request!, error: null);
    }

    private static async Task<IResult> AuthorizeSubmit(HttpContext context, OAuthStore store, UserDirectory users)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var (request, failure) = Check(context, name => form.TryGetValue(name, out var v) ? v.ToString() : null, store);
        if (failure is not null)
        {
            return failure;
        }

        var back = new Dictionary<string, string?> { ["state"] = request!.State, ["iss"] = BaseUrl(context) };

        if (form["decision"].ToString() != "allow")
        {
            back["error"] = "access_denied";
            back["error_description"] = "the user declined";
            return Results.Redirect(WithQuery(request.RedirectUri, back));
        }

        var user = users.Resolve(form["key"].ToString().Trim());
        if (user is null)
        {
            return Form(context, request, "That key is not a key of an enabled user on this hub.");
        }

        back["code"] = store.IssueCode(request.Client.Id, request.RedirectUri, request.Challenge, user, request.Resource, request.Scope);
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("InferHub.Coordinator.Mcp.OAuth")
            .LogInformation("User {User} authorised OAuth client {Client} ({Name})", user.Id, request.Client.Id, request.Client.Name);
        return Results.Redirect(WithQuery(request.RedirectUri, back));
    }

    private static async Task<IResult> Token(HttpContext context, OAuthStore store, UserDirectory users)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        if (!context.Request.HasFormContentType)
        {
            return TokenError("invalid_request", "send application/x-www-form-urlencoded");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var (clientId, secret) = ClientCredentials(context, form);
        var client = store.GetClient(clientId);
        if (client is null || !OAuthStore.SecretMatches(client, secret))
        {
            return TokenError("invalid_client", "unknown client or wrong secret", 401);
        }

        var (tokens, error) = form["grant_type"].ToString() switch
        {
            "authorization_code" => store.Redeem(form["code"], client.Id, form["redirect_uri"], form["code_verifier"], form["resource"]),
            "refresh_token" => store.Refresh(form["refresh_token"], client.Id, form["resource"]),
            var other => (null, new OAuthError("unsupported_grant_type", $"'{other}' — use authorization_code or refresh_token"))
        };

        if (error is not null)
        {
            return TokenError(error.Error, error.Description);
        }

        return Results.Json(new
        {
            access_token = tokens!.AccessToken,
            token_type = "Bearer",
            expires_in = tokens.ExpiresIn,
            refresh_token = tokens.RefreshToken,
            scope = tokens.Scope
        });
    }

    private static async Task<IResult> Revoke(HttpContext context, OAuthStore store)
    {
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            store.Revoke(form["token"].ToString());
        }

        return Results.Ok();
    }

    private static (string? Id, string? Secret) ClientCredentials(HttpContext context, IFormCollection form)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var pair = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var colon = pair.IndexOf(':');
                if (colon > 0)
                {
                    return (Uri.UnescapeDataString(pair[..colon]), Uri.UnescapeDataString(pair[(colon + 1)..]));
                }
            }
            catch (FormatException)
            {
                return (null, null);
            }
        }

        return (form["client_id"].ToString(), form["client_secret"].ToString() is { Length: > 0 } s ? s : null);
    }

    private static IResult TokenError(string error, string description, int status = 400)
        => Results.Json(new { error, error_description = description }, statusCode: status);

    internal static string WithQuery(string uri, Dictionary<string, string?> values)
    {
        var builder = new StringBuilder(uri);
        var separator = uri.Contains('?') ? '&' : '?';
        foreach (var (name, value) in values)
        {
            if (value is null)
            {
                continue;
            }

            builder.Append(separator).Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return builder.ToString();
    }

    private static IResult Form(HttpContext context, AuthorizeRequest request, string? error)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        var name = request.Client.Name ?? "An application";
        var target = Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out var parsed) ? parsed.Scheme + "://" + parsed.Authority : request.RedirectUri;

        var hidden = new StringBuilder();
        foreach (var (field, value) in new[]
        {
            ("client_id", request.Client.Id), ("redirect_uri", request.RedirectUri), ("state", request.State),
            ("response_type", "code"), ("code_challenge", request.Challenge), ("code_challenge_method", "S256"),
            ("resource", request.Resource), ("scope", request.Scope)
        })
        {
            if (value is not null)
            {
                hidden.Append($"<input type=\"hidden\" name=\"{field}\" value=\"{E(value)}\">");
            }
        }

        var body = $"""
            <p><b>{E(name)}</b> asks to use this InferHub as you: list the models your role may see, and run the ones it may run.</p>
            <p class="meta">It will return to <code>{E(target)}</code>. You can sign it out later from the users page.</p>
            {(error is null ? "" : $"<p class=\"err\">{E(error)}</p>")}
            <form method="post" action="{E(BaseUrl(context))}/oauth/authorize" autocomplete="off">
              {hidden}
              <label for="key">Your InferHub key</label>
              <input id="key" name="key" type="password" placeholder="ihu_…" autofocus>
              <div class="row">
                <button name="decision" value="allow" class="primary">Allow</button>
                <button name="decision" value="deny">Deny</button>
              </div>
            </form>
            """;
        return Page("Connect to InferHub", body, 200);
    }

    private static IResult Page(string title, string body, int status)
    {
        var html = $$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>
              body { background:#0f1115; color:#e4e7ee; font-family: ui-sans-serif, system-ui, "Segoe UI", sans-serif; margin:0; padding:48px 16px; }
              main { max-width:440px; margin:0 auto; background:#161a22; border:1px solid #232936; border-radius:10px; padding:24px; }
              h1 { font-size:18px; margin:0 0 12px; } .meta { color:#8b94a7; font-size:13px; } .err { color:#f87171; }
              label { display:block; font-size:13px; color:#8b94a7; margin:16px 0 6px; }
              input { width:100%; box-sizing:border-box; background:#0b0d11; color:#e4e7ee; border:1px solid #232936; border-radius:6px; padding:8px; font-size:14px; }
              .row { display:flex; gap:10px; margin-top:16px; }
              button { flex:1; background:transparent; color:#4ea1ff; border:1px solid rgba(78,161,255,.35); border-radius:6px; padding:8px; font-size:14px; cursor:pointer; }
              button.primary { background:#4ea1ff; color:#0b0d11; }
              code { font-size:12px; }
            </style></head><body><main><h1>{{WebUtility.HtmlEncode(title)}}</h1>{{body}}</main></body></html>
            """;
        return new HtmlPage(html, status);
    }

    private sealed class HtmlPage(string html, int status) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "text/html; charset=utf-8";
            // The page takes a key: nobody may frame it, and nothing on it is cached.
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            return context.Response.WriteAsync(html);
        }
    }
}
