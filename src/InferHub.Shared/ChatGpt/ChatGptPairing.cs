using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Shared.ChatGpt;

/// <summary>
/// Signing a <b>remote</b> coordinator in to ChatGPT (phase 103, 103 D6). A <c>127.0.0.1</c>
/// callback reaches the computer running the browser, not the server — so the sign-in runs on the
/// administrator's own machine, in a small helper, and the result travels to the coordinator over
/// a one-time pairing an administrator created in the console.
/// </summary>
/// <remarks>
/// <para>
/// The pairing code is the only credential the helper ever holds for the hub: single use, ten
/// minutes, bound to one provider, created behind the admin key, sent in a header and never in a
/// URL. <b>The coordinator issues the nonce</b>, so the ID token the helper hands over is checked
/// against a value the coordinator chose for this pairing — a token from another sign-in does not
/// verify. And the host id is the coordinator's, so the registration is attributed to the host that
/// will use it rather than to a laptop (the vendor's self-hosted-VM rule).
/// </para>
/// <para>
/// The helper exchanges the code itself, as the vendor's self-hosted procedure describes, holds the
/// tokens in memory only, and refuses to send them anywhere but HTTPS — or plain HTTP to a loopback
/// hub, where there is no wire to protect.
/// </para>
/// </remarks>
public static class ChatGptPairing
{
    public const string HeaderName = "X-InferHub-Pairing";

    public const string BeginPath = "/chatgpt-pairing/begin";

    public const string CompletePath = "/chatgpt-pairing/complete";

    public const string CancelPath = "/chatgpt-pairing/cancel";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// The whole helper: ask the hub what to sign in as, run the browser sign-in here, hand the
    /// result over. Progress goes to <paramref name="output"/>; no token is ever written to it.
    /// </summary>
    public static async Task<PairingCompleteResponse> RunAsync(
        Uri hub,
        string code,
        int preferredPort,
        bool allowInsecureHttp,
        bool openBrowser,
        TextWriter output,
        CancellationToken cancellationToken,
        HttpMessageHandler? oauthHandler = null,
        Func<string, Task>? browser = null)
    {
        RequireSecure(hub, allowInsecureHttp);

        using var hubHttp = new HttpClient { BaseAddress = hub, Timeout = TimeSpan.FromSeconds(60) };
        hubHttp.DefaultRequestHeaders.TryAddWithoutValidation(HeaderName, code.Trim());

        using var begin = await hubHttp.PostAsync(BeginPath.TrimStart('/'), content: null, cancellationToken);
        await ThrowIfFailedAsync(begin, "start the pairing", cancellationToken);

        var plan = await begin.Content.ReadFromJsonAsync<PairingBeginResponse>(JsonOptions, cancellationToken)
            ?? throw new ChatGptOAuthException("pairing_failed", "the hub answered the pairing with an empty body.");

        output.WriteLine($"Signing coordinator {hub} (provider '{plan.Provider}') in to ChatGPT.");
        output.WriteLine(plan.LoginHint is null
            ? "A new ChatGPT account registration will be created for that coordinator."
            : $"Reconnecting the account {plan.LoginHint}.");

        var signIn = ChatGptBrowserSignIn.Begin(
            plan.HostId,
            new SignInHints(plan.RegistrationId, plan.ClientId, IdTokenHint: null, plan.LoginHint),
            preferredPort,
            plan.Nonce);

        output.WriteLine($"Waiting for the browser on {signIn.Attempt.RedirectUri} …");

        if (browser is not null)
        {
            _ = browser(signIn.AuthorizeUrl);
        }
        else if (!openBrowser || !ChatGptBrowserSignIn.TryOpenBrowser(signIn.AuthorizeUrl))
        {
            output.WriteLine("Open this address in a browser on THIS computer:");
            output.WriteLine(signIn.AuthorizeUrl);
        }

        using var oauthHttp = oauthHandler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(60) }
            : new HttpClient(oauthHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
        SignInGrant grant;

        try
        {
            grant = await signIn.CompleteAsync(oauthHttp, IdTokenValidator.Over(oauthHttp), plan.RegistrationId,
                plan.ExpiresAtUtc - DateTimeOffset.UtcNow, cancellationToken);
        }
        catch
        {
            // Burn the code on the way out, so a half-finished pairing is not left valid.
            try
            {
                using var cancel = await hubHttp.PostAsync(CancelPath.TrimStart('/'), content: null, CancellationToken.None);
            }
            catch (HttpRequestException)
            {
            }

            throw;
        }

        output.WriteLine($"Signed in as {grant.Claims.Email ?? grant.Claims.Subject}. Handing the session to the coordinator …");

        var request = new PairingCompleteRequest
        {
            ClientId = grant.ClientId,
            AccessToken = grant.Token.AccessToken,
            RefreshToken = grant.Token.RefreshToken,
            IdToken = grant.Token.IdToken,
            TokenType = grant.Token.TokenType,
            ExpiresIn = grant.Token.ExpiresIn,
            Scope = grant.Token.Scope
        };

        using var complete = await hubHttp.PostAsJsonAsync(CompletePath.TrimStart('/'), request, JsonOptions, cancellationToken);
        await ThrowIfFailedAsync(complete, "hand the session to the coordinator", cancellationToken);

        return await complete.Content.ReadFromJsonAsync<PairingCompleteResponse>(JsonOptions, cancellationToken)
            ?? new PairingCompleteResponse { Message = "the coordinator accepted the session." };
    }

    /// <summary>HTTPS, or HTTP to a loopback hub. Anything else needs the explicit override.</summary>
    public static void RequireSecure(Uri hub, bool allowInsecureHttp)
    {
        if (hub.Scheme == Uri.UriSchemeHttps || hub.IsLoopback || allowInsecureHttp)
        {
            return;
        }

        throw new ChatGptOAuthException("insecure_hub",
            $"{hub} is plain HTTP to another machine, and a ChatGPT session would cross the network unencrypted. "
            + "Use the coordinator's https:// address (or --allow-http on a network you trust).");
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? message = null;

        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            message = document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
        }

        throw new ChatGptOAuthException("pairing_failed",
            $"the coordinator would not {what} ({(int)response.StatusCode}){(message is null ? "." : $": {message}")}");
    }
}

/// <summary>What the coordinator tells the helper to sign in as. No token.</summary>
public sealed class PairingBeginResponse
{
    public string Provider { get; set; } = string.Empty;

    /// <summary>The <b>coordinator's</b> <c>ext_agent_host_id</c>, never the helper's machine's.</summary>
    public string HostId { get; set; } = string.Empty;

    /// <summary>The issued client to reconnect, or null for a new registration.</summary>
    public string? ClientId { get; set; }

    public string? RegistrationId { get; set; }

    public string? LoginHint { get; set; }

    /// <summary>Chosen by the coordinator, and checked by it against the ID token it receives.</summary>
    public string Nonce { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; set; }
}

/// <summary>The token response, as the helper received it, sent once over TLS in a request body.</summary>
public sealed class PairingCompleteRequest
{
    public string? ClientId { get; set; }

    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }

    public string? IdToken { get; set; }

    public string? TokenType { get; set; }

    public int? ExpiresIn { get; set; }

    public string? Scope { get; set; }
}

public sealed class PairingCompleteResponse
{
    public string? Email { get; set; }

    public bool PlanUsage { get; set; }

    public string? Account { get; set; }

    public string? Message { get; set; }
}
