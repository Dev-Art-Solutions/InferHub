using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace InferHub.Shared.ChatGpt;

/// <summary>
/// One host's live view of its ChatGPT store (phase 103): hands out a current access token,
/// refreshes it — serialized, with the rotated refresh token saved in the same write — takes a
/// completed sign-in, signs out, and remembers the plan-usage state a request learned.
/// </summary>
/// <remarks>
/// <para>
/// The coordinator holds one per <c>chatgpt</c> provider and a node holds one for itself. Nothing
/// here knows which, which is the point (103 D1).
/// </para>
/// <para>
/// <b>The file is re-read when another process wrote it.</b> A node's sign-in verb runs beside the
/// running service and writes the same store; the service notices the new write time on its next
/// request rather than needing a restart.
/// </para>
/// </remarks>
public sealed class ChatGptSession(
    ChatGptCredentialStore store,
    Func<HttpClient> httpFactory,
    IdTokenValidator validator,
    TimeProvider? time = null)
{
    /// <summary>Refresh this long before expiry, so a request never leaves with a token about to die.</summary>
    private static readonly TimeSpan RefreshAhead = TimeSpan.FromMinutes(5);

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ChatGptAccountsFile? cached;
    private DateTime? cachedWrite;
    private DateTimeOffset? usagePausedUntil;
    private string? usagePauseReason;
    private string? lastError;
    private DateTimeOffset? lastErrorAtUtc;

    public ChatGptCredentialStore Store => store;

    public IdTokenValidator Validator => validator;

    public string HostId() => store.HostId();

    /// <summary>What a status page may show. Carries no token, not even a prefix of one.</summary>
    public ChatGptAccountState State()
    {
        ChatGptAccountsFile accounts;
        string? storeError = null;

        try
        {
            accounts = Current();
        }
        catch (ChatGptStoreException ex)
        {
            accounts = new ChatGptAccountsFile();
            storeError = ex.Message;
        }

        var active = accounts.ActiveRegistration();
        var now = clock.GetUtcNow();
        var paused = usagePausedUntil is { } until && until > now ? usagePausedUntil : null;

        return new ChatGptAccountState(
            SignedIn: active?.SignedIn ?? false,
            PlanUsage: active is { SignedIn: true, PlanUsage: true },
            Email: active?.Email,
            Account: active?.Id,
            SelectedModel: active?.SelectedModel,
            Scopes: active?.Scopes.ToArray() ?? [],
            AccessExpiresAtUtc: active?.SignedIn == true ? active.AccessExpiresAtUtc : null,
            UsagePausedUntilUtc: paused,
            UsagePauseReason: paused is null ? null : usagePauseReason,
            LastError: storeError ?? lastError,
            LastErrorAtUtc: storeError is null ? lastErrorAtUtc : now,
            Registrations: accounts.Registrations
                .Select(registration => new ChatGptRegistrationState(
                    registration.Id,
                    registration.Email,
                    registration.SignedIn,
                    registration.PlanUsage,
                    registration.Id == accounts.Active))
                .ToArray());
    }

    /// <summary>
    /// The active registration's sign-in hints, for starting a returning sign-in to it. Null
    /// <paramref name="registrationId"/> means the active one; <see cref="ChatGptOAuth.DynamicClientId"/>
    /// comes back for "add another account".
    /// </summary>
    public SignInHints Hints(string? registrationId, bool newAccount)
    {
        if (newAccount)
        {
            return new SignInHints(null, null, null, null);
        }

        var accounts = Current();
        var registration = registrationId is null
            ? accounts.ActiveRegistration()
            : accounts.Registrations.FirstOrDefault(r => r.Id == registrationId)
              ?? throw new ChatGptStoreException($"no ChatGPT account '{registrationId}' is registered on this host.");

        return registration is null
            ? new SignInHints(null, null, null, null)
            : new SignInHints(registration.Id, registration.ClientId, registration.IdToken, registration.Email);
    }

    /// <summary>
    /// A bearer token for the active account, refreshed when it is near expiry. Throws
    /// <see cref="ChatGptUnavailableException"/> with the status a caller should see when there is
    /// no usable account, the plan permission was not granted, or the plan's usage limit paused it.
    /// </summary>
    public async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        if (usagePausedUntil is { } until && until > now)
        {
            throw new ChatGptUnavailableException(429, "subscription_sharing_usage_limit_exceeded",
                $"{usagePauseReason ?? "the ChatGPT plan's usage limit was reached"}; requests on this account are paused "
                + $"until {until:u}. ChatGPT Settings → Usage shows the limit.");
        }

        var active = Current().ActiveRegistration();
        RequireUsable(active);

        if (active!.AccessExpiresAtUtc is { } expires && expires - RefreshAhead > now)
        {
            return active.AccessToken!;
        }

        return await RefreshAsync(active.Id, cancellationToken);
    }

    /// <summary>After an upstream 401: one forced refresh, then the caller tries once more.</summary>
    public Task<string> ForceRefreshAsync(CancellationToken cancellationToken)
    {
        var active = Current().ActiveRegistration();
        RequireUsable(active);
        return RefreshAsync(active!.Id, cancellationToken, force: true);
    }

    /// <summary>
    /// Stores a completed sign-in. A returning sign-in must come back as the account it was for —
    /// the validated <c>sub</c> is compared before anything is replaced.
    /// </summary>
    public async Task<ChatGptAccountState> ApplySignInAsync(SignInGrant grant, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(grant.Token.AccessToken) || string.IsNullOrWhiteSpace(grant.Token.RefreshToken))
        {
            throw new ChatGptOAuthException("invalid_token_response",
                "the token response carried no refresh token, so this host could not keep the session alive; nothing was stored. "
                + "offline_access must be granted.");
        }

        await gate.WaitAsync(cancellationToken);

        try
        {
            var accounts = Load();
            var existing = accounts.Registrations.FirstOrDefault(r => r.ClientId == grant.ClientId);

            if (existing is not null && existing.Subject != grant.Claims.Subject)
            {
                throw new ChatGptOAuthException("identity_mismatch",
                    $"the sign-in came back as a different ChatGPT account than '{existing.Label}', which this client is "
                    + "registered to; nothing was replaced. Use \"add another account\" for a second account.");
            }

            if (grant.ExpectedRegistration is { } expected
                && accounts.Registrations.FirstOrDefault(r => r.Id == expected) is { } target
                && target.Subject != grant.Claims.Subject)
            {
                throw new ChatGptOAuthException("identity_mismatch",
                    $"the sign-in came back as a different ChatGPT account than '{target.Label}'; nothing was replaced.");
            }

            var registration = existing ?? new ChatGptRegistration
            {
                Id = "acct-" + ChatGptOAuth.RandomToken(6).ToLowerInvariant().Replace('_', 'x').Replace('-', 'x'),
                ClientId = grant.ClientId,
                Subject = grant.Claims.Subject
            };

            registration.Email = grant.Claims.Email ?? registration.Email;
            registration.Name = grant.Claims.Name ?? registration.Name;
            Apply(registration, grant.Token);

            if (existing is null)
            {
                accounts.Registrations.Add(registration);
            }

            accounts.Active = registration.Id;
            Save(accounts);

            usagePausedUntil = null;
            lastError = registration.PlanUsage
                ? null
                : "signed in, but ChatGPT plan usage (chatgpt.tokens.use.direct) was not granted — inference is off for this account until it is";
            lastErrorAtUtc = registration.PlanUsage ? null : clock.GetUtcNow();
        }
        finally
        {
            gate.Release();
        }

        return State();
    }

    /// <summary>
    /// Revokes the renewable session and clears the tokens, keeping the client id and identity so
    /// the next sign-in to the same account is a returning one. Returns whether OpenAI confirmed the
    /// revocation — a local sign-out that could not reach it says so.
    /// </summary>
    public async Task<bool> SignOutAsync(string? registrationId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            var accounts = Load();
            var registration = (registrationId is null
                ? accounts.ActiveRegistration()
                : accounts.Registrations.FirstOrDefault(r => r.Id == registrationId))
                ?? throw new ChatGptStoreException("no ChatGPT account is signed in on this host.");

            var confirmed = registration.RefreshToken is null;

            if (registration.RefreshToken is { } refresh)
            {
                for (var attempt = 0; attempt < 3 && !confirmed; attempt++)
                {
                    try
                    {
                        using var http = httpFactory();
                        await ChatGptOAuth.RevokeAsync(http, registration.ClientId, refresh, cancellationToken);
                        confirmed = true;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                                   || (ex is ChatGptOAuthException { Status: >= 500 }))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(500 * (1 << attempt)), cancellationToken);
                    }
                    catch (ChatGptOAuthException)
                    {
                        // A 4xx means the server will not take this token back, which it only does
                        // for a token that is already dead. Clearing locally is the whole answer.
                        confirmed = true;
                    }
                }
            }

            registration.AccessToken = null;
            registration.RefreshToken = null;
            registration.AccessExpiresAtUtc = null;
            Save(accounts);

            usagePausedUntil = null;
            lastError = confirmed ? null : "signed out locally; OpenAI did not confirm the revocation — disconnect the app in ChatGPT Settings to be sure";
            lastErrorAtUtc = confirmed ? null : clock.GetUtcNow();

            return confirmed;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ActivateAsync(string registrationId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            var accounts = Load();

            if (accounts.Registrations.All(r => r.Id != registrationId))
            {
                throw new ChatGptStoreException($"no ChatGPT account '{registrationId}' is registered on this host.");
            }

            accounts.Active = registrationId;
            Save(accounts);
            usagePausedUntil = null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SelectModelAsync(string? model, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            var accounts = Load();
            var active = accounts.ActiveRegistration()
                ?? throw new ChatGptStoreException("no ChatGPT account is signed in on this host.");

            active.SelectedModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
            Save(accounts);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The active account's selected model, for a <c>@selected</c> mapping.</summary>
    public string? SelectedModel() => Current().ActiveRegistration()?.SelectedModel;

    /// <summary>
    /// The active account's ChatGPT account id, read from the ID token stored at sign-in (already
    /// validated then) — the <c>ChatGPT-Account-Id</c> the usage endpoint wants (106 D3). Null when the
    /// token carries none. The claim is the vendor's and treated as opaque beyond being echoed back.
    /// </summary>
    public string? ActiveAccountId()
    {
        try
        {
            var token = Current().ActiveRegistration()?.IdToken;
            var parts = token?.Split('.');

            if (parts is not { Length: 3 })
            {
                return null;
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth)
                   && auth.ValueKind == JsonValueKind.Object
                   && auth.TryGetProperty("chatgpt_account_id", out var id)
                   && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ChatGptStoreException)
        {
            return null;
        }
    }

    /// <summary>
    /// The active account's own catalogue, from <c>GET /v1/models</c> with its own token: the models
    /// whose <c>visibility</c> is <c>list</c>, in the server's order.
    /// </summary>
    public async Task<IReadOnlyList<ChatGptModel>> ListModelsAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var token = await AccessTokenAsync(cancellationToken);
        using var http = httpFactory();
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await ChatGptUpstreamException.FromResponseAsync(response, cancellationToken);
            ReportFailure(error);
            throw error;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ChatGptModel.Parse(document.RootElement);
    }

    /// <summary>
    /// What a request learned about this account. A usage-limit refusal pauses new requests for
    /// <paramref name="pause"/> (the vendor: "pause new requests that use the user's plan") rather
    /// than sending every queued prompt into the same 429.
    /// </summary>
    public void ReportFailure(ChatGptUpstreamException error, TimeSpan? pause = null)
    {
        lastError = error.Message;
        lastErrorAtUtc = clock.GetUtcNow();

        if (error.Code == "subscription_sharing_usage_limit_exceeded")
        {
            usagePausedUntil = clock.GetUtcNow() + (pause ?? TimeSpan.FromMinutes(5));
            usagePauseReason = "the ChatGPT plan's usage limit was reached";
        }
    }

    public void ReportSuccess()
    {
        lastError = null;
        lastErrorAtUtc = null;
    }

    // ---- internals --------------------------------------------------------------------

    private static void RequireUsable(ChatGptRegistration? active)
    {
        if (active is null || !active.SignedIn)
        {
            throw new ChatGptUnavailableException(401, "not_signed_in",
                "no ChatGPT account is signed in on this host. Sign in with \"Continue with ChatGPT\" first.");
        }

        if (!active.PlanUsage)
        {
            // 103 D5: signed in, and still no inference. There is no fallback to a paid API here.
            throw new ChatGptUnavailableException(403, "plan_usage_not_granted",
                $"'{active.Label}' is signed in but did not grant ChatGPT plan usage (chatgpt.tokens.use.direct); "
                + "reconnect and allow it. Nothing was sent to a billable API instead.");
        }
    }

    private async Task<string> RefreshAsync(string registrationId, CancellationToken cancellationToken, bool force = false)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            // Re-read under the lock: another request, or another process, may have refreshed
            // already, and a rotating refresh token used twice is a dead session.
            var accounts = Load();
            var registration = accounts.Registrations.FirstOrDefault(r => r.Id == registrationId);
            RequireUsable(registration);

            if (!force && registration!.AccessExpiresAtUtc is { } expires && expires - RefreshAhead > clock.GetUtcNow())
            {
                return registration.AccessToken!;
            }

            TokenResponse token;

            try
            {
                using var http = httpFactory();
                token = await ChatGptOAuth.RefreshAsync(http, registration!.ClientId, registration.RefreshToken!, cancellationToken);
            }
            catch (ChatGptOAuthException ex) when (ChatGptOAuth.IsUnusableRefresh(ex.Code))
            {
                // Dead token set: clear it, keep the registration, and say what to do.
                registration!.AccessToken = null;
                registration.RefreshToken = null;
                registration.AccessExpiresAtUtc = null;
                Save(accounts);

                lastError = $"the ChatGPT session for '{registration.Label}' ended ({ex.Code}); sign in again";
                lastErrorAtUtc = clock.GetUtcNow();

                throw new ChatGptUnavailableException(401, ex.Code, lastError + ".");
            }
            catch (ChatGptOAuthException ex)
            {
                // A network failure or a 5xx keeps the credentials: the vendor is explicit that a
                // temporary failure is not a reason to erase anything.
                lastError = $"refreshing the ChatGPT session failed: {ex.Message}";
                lastErrorAtUtc = clock.GetUtcNow();
                throw new ChatGptUnavailableException(ex.Status is >= 500 ? 503 : 502, ex.Code, lastError);
            }

            Apply(registration!, token);
            Save(accounts);

            return registration!.AccessToken!;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Access token, rotated refresh token, expiry and granted scopes — replaced together.</summary>
    private void Apply(ChatGptRegistration registration, TokenResponse token)
    {
        var now = clock.GetUtcNow();

        registration.AccessToken = token.AccessToken;
        registration.RefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? registration.RefreshToken : token.RefreshToken;
        registration.IdToken = string.IsNullOrWhiteSpace(token.IdToken) ? registration.IdToken : token.IdToken;
        registration.AccessExpiresAtUtc = now + TimeSpan.FromSeconds(token.ExpiresIn is > 0 ? token.ExpiresIn.Value : 3600);
        registration.SavedAtUtc = now;

        // A refresh response that omits `scope` kept the grant it had (scope was omitted on purpose).
        if (!string.IsNullOrWhiteSpace(token.Scope))
        {
            registration.Scopes = token.Scopes().ToList();
        }
    }

    private ChatGptAccountsFile Current()
    {
        var written = store.LastWriteUtc;

        if (cached is null || written != cachedWrite)
        {
            cached = store.Load();
            cachedWrite = written;
        }

        return cached;
    }

    private ChatGptAccountsFile Load()
    {
        cached = store.Load();
        cachedWrite = store.LastWriteUtc;
        return cached;
    }

    private void Save(ChatGptAccountsFile accounts)
    {
        store.Save(accounts);
        cached = accounts;
        cachedWrite = store.LastWriteUtc;
    }
}

/// <summary>A completed, validated sign-in, ready to store.</summary>
/// <param name="ExpectedRegistration">The registration a returning sign-in was started for, if any.</param>
public sealed record SignInGrant(string ClientId, TokenResponse Token, IdTokenClaims Claims, string? ExpectedRegistration);

/// <summary>What a returning sign-in sends: the issued client and the retained hints.</summary>
public sealed record SignInHints(string? RegistrationId, string? ClientId, string? IdTokenHint, string? LoginHint);

/// <summary>The status-page view of a host's ChatGPT account. No token, ever.</summary>
public sealed record ChatGptAccountState(
    bool SignedIn,
    bool PlanUsage,
    string? Email,
    string? Account,
    string? SelectedModel,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? AccessExpiresAtUtc,
    DateTimeOffset? UsagePausedUntilUtc,
    string? UsagePauseReason,
    string? LastError,
    DateTimeOffset? LastErrorAtUtc,
    IReadOnlyList<ChatGptRegistrationState> Registrations);

public sealed record ChatGptRegistrationState(string Id, string? Email, bool SignedIn, bool PlanUsage, bool Active);

/// <summary>
/// The account cannot serve right now, for a reason the caller should see with this status:
/// 401 not signed in or session ended, 403 plan usage not granted, 429 usage limit, 5xx temporary.
/// </summary>
public sealed class ChatGptUnavailableException(int status, string code, string message)
    : InferHub.Shared.Upstream.UpstreamDialectException(status, message)
{
    public string Code { get; } = code;
}

/// <summary>One entry of the account's <c>/v1/models</c> catalogue.</summary>
public sealed record ChatGptModel(string Slug, string DisplayName)
{
    /// <summary>
    /// <c>{"models":[{slug, display_name, visibility}]}</c>, keeping <c>visibility: "list"</c> in the
    /// server's order. A standard <c>{"data":[{id}]}</c> listing is read too, so a proxy that
    /// answers in the public API's shape is not an empty picker.
    /// </summary>
    public static IReadOnlyList<ChatGptModel> Parse(JsonElement root)
    {
        var models = new List<ChatGptModel>();

        if (root.ValueKind != JsonValueKind.Object)
        {
            return models;
        }

        if (root.TryGetProperty("models", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in list.EnumerateArray())
            {
                var slug = model.TryGetProperty("slug", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;

                if (string.IsNullOrWhiteSpace(slug))
                {
                    continue;
                }

                if (model.TryGetProperty("visibility", out var visibility)
                    && visibility.ValueKind == JsonValueKind.String
                    && visibility.GetString() != "list")
                {
                    continue;
                }

                var display = model.TryGetProperty("display_name", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : null;

                models.Add(new ChatGptModel(slug!, string.IsNullOrWhiteSpace(display) ? slug! : display!));
            }
        }
        else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in data.EnumerateArray())
            {
                if (model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } slug)
                {
                    models.Add(new ChatGptModel(slug, slug));
                }
            }
        }

        return models;
    }
}
