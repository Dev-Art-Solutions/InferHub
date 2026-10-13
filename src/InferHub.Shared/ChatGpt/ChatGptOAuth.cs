using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Shared.ChatGpt;

/// <summary>
/// Sign in with ChatGPT for an open-source, locally hosted app (phase 103): the authorize URL, the
/// code exchange, the refresh and the revocation — the four calls OpenAI's "token sharing for
/// open-source" flow is made of. Hand-rolled over <see cref="HttpClient"/>, no package (rule 5).
/// </summary>
/// <remarks>
/// <para>
/// Every constant here is the vendor's, from its own documentation and its
/// <c>/.well-known/openid-configuration</c>, captured on the day this was written. None is a
/// setting: an operator who could point the token endpoint somewhere else could point a refresh
/// token somewhere else.
/// </para>
/// <para>
/// <b>Public client, no secret.</b> The first sign-in sends <c>client_id=dynamic_agent_client</c>
/// and the callback hands back an issued <c>oaiapp_…</c> id; that issued id is what the exchange,
/// every refresh and every later sign-in to the same account use (103 D3).
/// </para>
/// </remarks>
public static class ChatGptOAuth
{
    public const string Issuer = "https://auth.openai.com";

    public const string AuthorizeEndpoint = "https://auth.openai.com/api/accounts/authorize";

    public const string TokenEndpoint = "https://auth.openai.com/api/accounts/oauth/token";

    public const string RevocationEndpoint = "https://auth.openai.com/api/accounts/oauth/revoke";

    public const string JwksUri = "https://auth.openai.com/.well-known/jwks.json";

    /// <summary>The audience of the access token, and the base of every inference call.</summary>
    public const string Resource = "https://api.openai.com/v1";

    /// <summary>The first-registration entrypoint. Never saved, never used for an exchange.</summary>
    public const string DynamicClientId = "dynamic_agent_client";

    /// <summary>
    /// Without <see cref="PlanUsageScope"/> a sign-in is an identity and nothing else: the token
    /// response's granted scopes decide whether inference is allowed, not the request's (103 D5).
    /// </summary>
    public const string RequestedScopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";

    public const string PlanUsageScope = "chatgpt.tokens.use.direct";

    /// <summary>
    /// <c>agent_name_hint</c> on a first registration: the app's actual name, the same on every
    /// installation. The user may rename it on the consent screen; it is display metadata.
    /// </summary>
    public const string AgentName = "InferHub";

    /// <summary>
    /// The callback is always <c>http://127.0.0.1:&lt;port&gt;/auth/callback</c> — only the port may
    /// vary, and <c>localhost</c> is not a synonym the authorization server accepts.
    /// </summary>
    public const string CallbackHost = "127.0.0.1";

    public const string CallbackPath = "/auth/callback";

    public const int DefaultCallbackPort = 1234;

    public static string RedirectUri(int port) => $"http://{CallbackHost}:{port}{CallbackPath}";

    /// <summary>A fresh attempt: new state, nonce and PKCE verifier, bound to one redirect URI.</summary>
    public static AuthorizationAttempt NewAttempt(
        string hostId,
        int port,
        string? issuedClientId = null,
        string? idTokenHint = null,
        string? loginHint = null,
        string? nonce = null)
    {
        var verifier = RandomToken(64);

        return new AuthorizationAttempt(
            ClientId: string.IsNullOrWhiteSpace(issuedClientId) ? DynamicClientId : issuedClientId.Trim(),
            HostId: hostId,
            RedirectUri: RedirectUri(port),
            State: RandomToken(32),
            Nonce: string.IsNullOrWhiteSpace(nonce) ? RandomToken(32) : nonce,
            CodeVerifier: verifier,
            CodeChallenge: Challenge(verifier),
            IdTokenHint: idTokenHint,
            LoginHint: loginHint);
    }

    /// <summary>
    /// The URL the system browser opens. It can carry an <c>id_token_hint</c>, so it is never
    /// logged (the vendor's own instruction, and 103 D7).
    /// </summary>
    public static string AuthorizeUrl(AuthorizationAttempt attempt)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", attempt.ClientId),
            new("redirect_uri", attempt.RedirectUri),
            new("scope", RequestedScopes),
            new("resource", Resource),
            new("state", attempt.State),
            new("nonce", attempt.Nonce),
            new("code_challenge", attempt.CodeChallenge),
            new("code_challenge_method", "S256"),
            new("ext_agent_host_id", attempt.HostId)
        };

        // Only on a first registration: a returning sign-in names its issued client, not the app.
        if (attempt.IsRegistration)
        {
            query.Add(new("agent_name_hint", AgentName));
        }

        if (!string.IsNullOrWhiteSpace(attempt.IdTokenHint))
        {
            query.Add(new("id_token_hint", attempt.IdTokenHint));
        }

        if (!string.IsNullOrWhiteSpace(attempt.LoginHint))
        {
            query.Add(new("login_hint", attempt.LoginHint));
        }

        return AuthorizeEndpoint + "?" + string.Join("&", query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    /// <summary>
    /// What came back to the loopback listener, checked against the attempt that sent it. Returns
    /// the issued client id the exchange must use.
    /// </summary>
    /// <exception cref="ChatGptOAuthException">On a state mismatch, a declined consent, a missing
    /// code, or a returning sign-in whose callback names a different client than the account's.</exception>
    public static CallbackResult ReadCallback(AuthorizationAttempt attempt, IReadOnlyDictionary<string, string> query)
    {
        // State first, error second: an error carrying somebody else's state is not ours to report.
        if (!query.TryGetValue("state", out var state)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(attempt.State)))
        {
            throw new ChatGptOAuthException("state_mismatch",
                "the sign-in callback did not carry this attempt's state; it was ignored. Start the sign-in again.");
        }

        if (query.TryGetValue("error", out var error))
        {
            throw error == "access_denied"
                ? new ChatGptOAuthException(error, "the sign-in was declined in the browser; nothing was stored.")
                : new ChatGptOAuthException(error,
                    $"the authorization server answered '{error}'"
                    + (query.TryGetValue("error_description", out var description) ? $": {description}" : "."));
        }

        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            throw new ChatGptOAuthException("missing_code", "the sign-in callback carried no authorization code.");
        }

        query.TryGetValue("client_id", out var callbackClient);

        string clientId;

        if (attempt.IsRegistration)
        {
            // A new registration that does not hand back its issued id is incomplete, and
            // `dynamic_agent_client` is not an id to save.
            if (string.IsNullOrWhiteSpace(callbackClient) || callbackClient == DynamicClientId)
            {
                throw new ChatGptOAuthException("registration_incomplete",
                    "the new registration's callback carried no issued client id; nothing was stored. Start the sign-in again.");
            }

            clientId = callbackClient;
        }
        else
        {
            // A returning sign-in may omit it; if it names another one, that is another
            // registration's result and it must not replace this account's.
            if (!string.IsNullOrWhiteSpace(callbackClient) && callbackClient != attempt.ClientId)
            {
                throw new ChatGptOAuthException("client_mismatch",
                    "the sign-in callback named a different client than the account being signed in to; nothing was replaced.");
            }

            clientId = attempt.ClientId;
        }

        return new CallbackResult(code, clientId);
    }

    /// <summary>POSTs the code to the token endpoint with the issued client id and the exact redirect URI.</summary>
    public static Task<TokenResponse> ExchangeCodeAsync(
        HttpClient http,
        AuthorizationAttempt attempt,
        CallbackResult callback,
        CancellationToken cancellationToken)
        => PostTokenAsync(http, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = callback.ClientId,
            ["code"] = callback.Code,
            ["code_verifier"] = attempt.CodeVerifier,
            ["redirect_uri"] = attempt.RedirectUri,
            ["resource"] = Resource
        }, cancellationToken);

    /// <summary>
    /// A refresh. <c>scope</c> is omitted so the grant is kept; the response carries a
    /// <b>replacement</b> refresh token that must be saved together with the new access token.
    /// </summary>
    public static Task<TokenResponse> RefreshAsync(
        HttpClient http,
        string clientId,
        string refreshToken,
        CancellationToken cancellationToken)
        => PostTokenAsync(http, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["resource"] = Resource
        }, cancellationToken);

    /// <summary>Ends the renewable session. An empty 200 is success, including for a dead token.</summary>
    public static async Task RevokeAsync(
        HttpClient http,
        string clientId,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = refreshToken,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = clientId
        });

        using var response = await http.PostAsync(RevocationEndpoint, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await ErrorFromAsync(response, cancellationToken);
        }
    }

    /// <summary>The refresh-error codes after which the token set is dead and only a new sign-in helps.</summary>
    public static bool IsUnusableRefresh(string? code)
        => code is "invalid_grant" or "invalid_refresh_token" or "token_expired" or "refresh_token_expired"
            or "refresh_token_invalidated" or "refresh_token_reused";

    public static string Challenge(string verifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }

    private static async Task<TokenResponse> PostTokenAsync(
        HttpClient http,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(TokenEndpoint, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await ErrorFromAsync(response, cancellationToken);
        }

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken);

        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new ChatGptOAuthException("invalid_token_response", "the token endpoint answered without an access token.");
        }

        return token;
    }

    /// <summary>
    /// The OAuth error's machine-readable code and its description — never the body verbatim, which
    /// is not ours to promise is free of a token echo.
    /// </summary>
    private static async Task<ChatGptOAuthException> ErrorFromAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        string? code = null;
        string? description = null;

        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                    {
                        code = error.GetString();
                    }
                    else if (error.ValueKind == JsonValueKind.Object)
                    {
                        code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                        description = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    }
                }

                if (description is null && root.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String)
                {
                    description = d.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        code ??= $"http_{status}";

        return new ChatGptOAuthException(
            code,
            $"the OpenAI authorization server answered {status} ({code})" + (description is null ? "." : $": {description}"),
            status);
    }
}

/// <summary>One sign-in attempt's one-time values. Held in memory only, and dropped when it ends.</summary>
public sealed record AuthorizationAttempt(
    string ClientId,
    string HostId,
    string RedirectUri,
    string State,
    string Nonce,
    string CodeVerifier,
    string CodeChallenge,
    string? IdTokenHint,
    string? LoginHint)
{
    public bool IsRegistration => ClientId == ChatGptOAuth.DynamicClientId;
}

public sealed record CallbackResult(string Code, string ClientId);

/// <summary>The token endpoint's answer. <c>scope</c> is the <b>granted</b> set, space-separated.</summary>
public sealed class TokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    public IReadOnlyList<string> Scopes()
        => (Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// A sign-in, refresh or revocation failed. <see cref="Code"/> is the machine-readable OAuth code
/// where there was one; the message never carries a token.
/// </summary>
public sealed class ChatGptOAuthException(string code, string message, int? status = null) : Exception(message)
{
    public string Code { get; } = code;

    public int? Status { get; } = status;
}
