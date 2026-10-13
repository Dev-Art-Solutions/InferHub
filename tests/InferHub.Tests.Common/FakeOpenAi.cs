using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InferHub.Shared.ChatGpt;

namespace InferHub.Tests;

/// <summary>
/// Phase 103. OpenAI's sign-in and Responses API as one in-process <see cref="HttpMessageHandler"/>:
/// the authorize step a browser would take, the token endpoint (code and refresh), revocation, a JWKS
/// holding this fixture's own RSA key, <c>/v1/models</c> and a scripted <c>/v1/responses</c> stream.
/// </summary>
/// <remarks>
/// <para>
/// The shapes are the ones in OpenAI's "Sign in with ChatGPT for open-source apps" pages and its
/// <c>/.well-known/openid-configuration</c>, captured on the day this was written (2026-10-13). No test
/// in this repository calls a vendor (the provider track's D6), so a real account's behaviour is what
/// the release notes say was <em>not</em> established.
/// </para>
/// <para>
/// ID tokens are really signed (RS256) and really verified, so the validator's signature, issuer,
/// audience, expiry and nonce checks are each exercised against bytes rather than a stub's opinion.
/// </para>
/// </remarks>
internal sealed class FakeOpenAi : HttpMessageHandler
{
    private readonly RSA key = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Grant> codes = new();
    private readonly ConcurrentDictionary<string, Grant> refreshTokens = new();
    private int clientCounter;

    public const string Kid = "test-key-1";

    /// <summary>The account the next browser sign-in signs in as.</summary>
    public string Subject { get; set; } = "user-sub-1";

    public string Email { get; set; } = "user@example.com";

    /// <summary>The granted scope string; drop <c>chatgpt.tokens.use.direct</c> to model a declined plan permission.</summary>
    public string GrantedScope { get; set; } = "chatgpt.tokens.use.direct email offline_access openid profile resource.invoke";

    public int ExpiresIn { get; set; } = 3600;

    /// <summary>The answer to <c>POST /v1/responses</c>: status, then SSE event payloads (each a JSON object).</summary>
    public Func<string, (HttpStatusCode Status, string[] Events, string? ErrorBody)> Responses { get; set; }
        = _ => (HttpStatusCode.OK, Completed("Hello", " world"), null);

    public string ModelsBody { get; set; } = """
        {"models":[{"slug":"gpt-6.1-sol","display_name":"GPT-6.1 Sol","visibility":"list"},
                   {"slug":"internal-x","display_name":"Hidden","visibility":"hide"},
                   {"slug":"gpt-6.1-mini","display_name":"GPT-6.1 mini","visibility":"list"}]}
        """;

    /// <summary>When set, the next refresh answers this OAuth error code with a 400.</summary>
    public string? RefreshError { get; set; }

    /// <summary>When set, <c>/v1/responses</c> answers 401 this many times before the scripted answer.</summary>
    public int UnauthorizedResponses { get; set; }

    public ConcurrentQueue<(string Method, Uri Uri, string? Authorization, string? Body)> Requests { get; } = new();

    public int Refreshes;

    public int Revocations;

    /// <summary>
    /// What the user's browser does after <paramref name="authorizeUrl"/> opens: signs in, consents,
    /// and is redirected to the callback with a code, the state and — for a new registration — the
    /// issued client id. Returns the callback URL the browser would load.
    /// </summary>
    public string Authorize(string authorizeUrl, bool deny = false)
    {
        var query = LoopbackCallbackListener.ParseQuery(new Uri(authorizeUrl).Query.TrimStart('?'));
        var redirect = query["redirect_uri"];
        var state = query["state"];

        if (deny)
        {
            return $"{redirect}?error=access_denied&state={Uri.EscapeDataString(state)}";
        }

        var registering = query["client_id"] == ChatGptOAuth.DynamicClientId;
        var clientId = registering ? $"oaiapp_test{Interlocked.Increment(ref clientCounter)}" : query["client_id"];
        var code = "code-" + Guid.NewGuid().ToString("N");

        codes[code] = new Grant(clientId, query["nonce"], redirect, query["code_challenge"], Subject, Email, GrantedScope, query["ext_agent_host_id"]);

        var callback = $"{redirect}?code={code}&state={Uri.EscapeDataString(state)}&scope={Uri.EscapeDataString(GrantedScope)}";
        return registering ? callback + $"&client_id={clientId}" : callback;
    }

    /// <summary>The <c>ext_agent_host_id</c> each code was issued under, for the host-separation tests.</summary>
    public IEnumerable<string> HostIdsSeen => codes.Values.Select(g => g.HostId).Concat(refreshTokens.Values.Select(g => g.HostId)).Distinct();

    /// <summary>An ID token signed by this fixture's key.</summary>
    public string IdToken(string clientId, string? nonce, string? subject = null, DateTimeOffset? expires = null, string issuer = ChatGptOAuth.Issuer, RSA? signer = null)
    {
        var header = ChatGptOAuth.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = Kid, typ = "JWT" }));
        var now = DateTimeOffset.UtcNow;
        var payload = ChatGptOAuth.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["iss"] = issuer,
            ["aud"] = new[] { clientId },
            ["sub"] = subject ?? Subject,
            ["email"] = Email,
            ["nonce"] = nonce,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = (expires ?? now.AddHours(1)).ToUnixTimeSeconds()
        }));

        var signature = (signer ?? key).SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + ChatGptOAuth.Base64Url(signature);
    }

    public string Jwks()
    {
        var parameters = key.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new { kty = "RSA", kid = Kid, alg = "RS256", use = "sig", n = ChatGptOAuth.Base64Url(parameters.Modulus), e = ChatGptOAuth.Base64Url(parameters.Exponent) }
            }
        });
    }

    public static string[] Completed(params string[] deltas)
        => [
            .. deltas.Select(d => JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = d })),
            """{"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":12,"output_tokens":3}}}"""
        ];

    public HttpClient Client() => new(this, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request.Method.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));

        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);

        return url switch
        {
            ChatGptOAuth.TokenEndpoint => Token(body ?? string.Empty),
            ChatGptOAuth.RevocationEndpoint => Revoke(body ?? string.Empty),
            ChatGptOAuth.JwksUri => Json(HttpStatusCode.OK, Jwks()),
            _ when url.EndsWith("/models", StringComparison.Ordinal) => Authorized(request) ? Json(HttpStatusCode.OK, ModelsBody) : Json(HttpStatusCode.Unauthorized, """{"detail":"bad token"}"""),
            _ when url.EndsWith("/responses", StringComparison.Ordinal) => ResponsesAnswer(request, body ?? string.Empty),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }

    private HttpResponseMessage Token(string body)
    {
        var form = LoopbackCallbackListener.ParseQuery(body);

        if (form["grant_type"] == "authorization_code")
        {
            if (!codes.TryRemove(form["code"], out var grant))
            {
                return Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
            }

            if (form["client_id"] != grant.ClientId || form["redirect_uri"] != grant.Redirect
                || ChatGptOAuth.Challenge(form["code_verifier"]) != grant.Challenge || form["resource"] != ChatGptOAuth.Resource)
            {
                return Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"code bound to another client, redirect or verifier"}""");
            }

            return Issue(grant, includeId: true);
        }

        if (form["grant_type"] == "refresh_token")
        {
            Interlocked.Increment(ref Refreshes);

            if (RefreshError is { } error)
            {
                return Json(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { error }));
            }

            if (!refreshTokens.TryRemove(form["refresh_token"], out var grant) || grant.ClientId != form["client_id"])
            {
                return Json(HttpStatusCode.BadRequest, """{"error":"refresh_token_reused"}""");
            }

            return Issue(grant, includeId: false);
        }

        return Json(HttpStatusCode.BadRequest, """{"error":"unsupported_grant_type"}""");
    }

    private HttpResponseMessage Issue(Grant grant, bool includeId)
    {
        var refresh = "rt-" + Guid.NewGuid().ToString("N");
        refreshTokens[refresh] = grant;
        var access = "at-" + Guid.NewGuid().ToString("N");
        IssuedAccessTokens[access] = grant.ClientId;

        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["access_token"] = access,
            ["refresh_token"] = refresh,
            ["id_token"] = includeId ? IdToken(grant.ClientId, grant.Nonce, grant.Subject) : null,
            ["token_type"] = "Bearer",
            ["expires_in"] = ExpiresIn,
            ["scope"] = includeId ? grant.Scope : null
        }));
    }

    /// <summary>Every access token issued, with the client it was issued to.</summary>
    public ConcurrentDictionary<string, string> IssuedAccessTokens { get; } = new();

    private HttpResponseMessage Revoke(string body)
    {
        Interlocked.Increment(ref Revocations);
        var form = LoopbackCallbackListener.ParseQuery(body);
        refreshTokens.TryRemove(form["token"], out _);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private bool Authorized(HttpRequestMessage request)
        => request.Headers.Authorization is { Scheme: "Bearer" } auth && IssuedAccessTokens.ContainsKey(auth.Parameter!);

    private HttpResponseMessage ResponsesAnswer(HttpRequestMessage request, string body)
    {
        if (UnauthorizedResponses > 0)
        {
            UnauthorizedResponses--;
            return Json(HttpStatusCode.Unauthorized, """{"error":{"message":"token expired","code":"token_expired"}}""");
        }

        if (!Authorized(request))
        {
            return Json(HttpStatusCode.Unauthorized, """{"detail":"unknown token"}""");
        }

        var (status, events, error) = Responses(body);

        if (status != HttpStatusCode.OK)
        {
            var refused = Json(status, error ?? "{}");
            refused.Headers.TryAddWithoutValidation("x-request-id", "req_test");
            return refused;
        }

        var sse = string.Concat(events.Select(e =>
        {
            using var document = JsonDocument.Parse(e);
            var type = document.RootElement.GetProperty("type").GetString();
            return $"event: {type}\ndata: {e}\n\n";
        }));

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            key.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed record Grant(string ClientId, string Nonce, string Redirect, string Challenge, string Subject, string Email, string Scope, string HostId);
}
