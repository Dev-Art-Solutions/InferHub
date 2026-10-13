using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using InferHub.Shared.ChatGpt;

namespace InferHub.Tests;

/// <summary>
/// Phase 103. Sign in with ChatGPT, end to end on this machine: a real loopback listener, a real
/// browser-shaped GET to it, the code exchange, an RS256 ID token really verified, and the encrypted
/// store — against <see cref="FakeOpenAi"/>, because no test here calls a vendor.
/// </summary>
public sealed class ChatGptSignInTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-chatgpt-" + Guid.NewGuid().ToString("N"));
    private readonly FakeOpenAi openAi = new();

    public void Dispose()
    {
        openAi.Dispose();

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- the authorize request -------------------------------------------------------------

    [Fact]
    public void AFirstSignInRegistersDynamicallyWithEveryParameterTheVendorRequires()
    {
        var attempt = ChatGptOAuth.NewAttempt("urn:uuid:host-1", 1234);
        var query = LoopbackCallbackListener.ParseQuery(new Uri(ChatGptOAuth.AuthorizeUrl(attempt)).Query.TrimStart('?'));

        Assert.StartsWith(ChatGptOAuth.AuthorizeEndpoint + "?", ChatGptOAuth.AuthorizeUrl(attempt));
        Assert.Equal("dynamic_agent_client", query["client_id"]);
        Assert.Equal("InferHub", query["agent_name_hint"]);
        Assert.Equal("urn:uuid:host-1", query["ext_agent_host_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("http://127.0.0.1:1234/auth/callback", query["redirect_uri"]);
        Assert.Equal("openid profile email offline_access resource.invoke chatgpt.tokens.use.direct", query["scope"]);
        Assert.Equal("https://api.openai.com/v1", query["resource"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(ChatGptOAuth.Challenge(attempt.CodeVerifier), query["code_challenge"]);
        Assert.False(query.ContainsKey("id_token_hint"));
    }

    [Fact]
    public void AReturningSignInNamesItsIssuedClientAndNeverTheAppName()
    {
        var attempt = ChatGptOAuth.NewAttempt("urn:uuid:host-1", 54321, issuedClientId: "oaiapp_abc", loginHint: "a@b.c");
        var query = LoopbackCallbackListener.ParseQuery(new Uri(ChatGptOAuth.AuthorizeUrl(attempt)).Query.TrimStart('?'));

        Assert.Equal("oaiapp_abc", query["client_id"]);
        Assert.False(query.ContainsKey("agent_name_hint"));
        Assert.Equal("a@b.c", query["login_hint"]);
        Assert.Equal("http://127.0.0.1:54321/auth/callback", query["redirect_uri"]);
    }

    [Fact]
    public void EveryAttemptHasFreshStateNonceAndVerifier()
    {
        var one = ChatGptOAuth.NewAttempt("h", 1234);
        var two = ChatGptOAuth.NewAttempt("h", 1234);

        Assert.NotEqual(one.State, two.State);
        Assert.NotEqual(one.Nonce, two.Nonce);
        Assert.NotEqual(one.CodeVerifier, two.CodeVerifier);

        // Base64url, no padding: the vendor's stated encoding of the S256 challenge.
        Assert.DoesNotContain('=', one.CodeChallenge);
        Assert.Equal(ChatGptOAuth.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(one.CodeVerifier))), one.CodeChallenge);
    }

    // ---- the callback ----------------------------------------------------------------------

    [Fact]
    public void ACallbackWithAnotherAttemptsStateIsRejectedBeforeItsErrorIsRead()
    {
        var attempt = ChatGptOAuth.NewAttempt("h", 1234);

        var ex = Assert.Throws<ChatGptOAuthException>(() => ChatGptOAuth.ReadCallback(attempt,
            new Dictionary<string, string> { ["state"] = "not-ours", ["code"] = "c", ["client_id"] = "oaiapp_1" }));

        Assert.Equal("state_mismatch", ex.Code);
    }

    [Fact]
    public void ADeclinedConsentStopsTheAttemptWithoutAnExchange()
    {
        var attempt = ChatGptOAuth.NewAttempt("h", 1234);

        var ex = Assert.Throws<ChatGptOAuthException>(() => ChatGptOAuth.ReadCallback(attempt,
            new Dictionary<string, string> { ["state"] = attempt.State, ["error"] = "access_denied" }));

        Assert.Equal("access_denied", ex.Code);
    }

    [Fact]
    public void ANewRegistrationWithoutAnIssuedClientIdIsIncomplete()
    {
        var attempt = ChatGptOAuth.NewAttempt("h", 1234);

        var ex = Assert.Throws<ChatGptOAuthException>(() => ChatGptOAuth.ReadCallback(attempt,
            new Dictionary<string, string> { ["state"] = attempt.State, ["code"] = "c" }));

        Assert.Equal("registration_incomplete", ex.Code);
    }

    [Fact]
    public void AReturningSignInWhoseCallbackNamesAnotherClientReplacesNothing()
    {
        var attempt = ChatGptOAuth.NewAttempt("h", 1234, issuedClientId: "oaiapp_mine");

        Assert.Equal("oaiapp_mine", ChatGptOAuth.ReadCallback(attempt,
            new Dictionary<string, string> { ["state"] = attempt.State, ["code"] = "c" }).ClientId);

        var ex = Assert.Throws<ChatGptOAuthException>(() => ChatGptOAuth.ReadCallback(attempt,
            new Dictionary<string, string> { ["state"] = attempt.State, ["code"] = "c", ["client_id"] = "oaiapp_other" }));

        Assert.Equal("client_mismatch", ex.Code);
    }

    // ---- the listener ----------------------------------------------------------------------

    [Fact]
    public void AnOccupiedPreferredPortMovesTheListenerAndOnlyThePort()
    {
        // Occupy a port, then ask for it: the listener must take another and say so in its URI.
        using var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        var taken = ((IPEndPoint)squatter.LocalEndpoint).Port;

        using var listener = LoopbackCallbackListener.Start(taken);

        Assert.NotEqual(taken, listener.Port);
        Assert.Equal($"http://127.0.0.1:{listener.Port}/auth/callback", listener.RedirectUri);
    }

    [Fact]
    public async Task TheListenerIsBoundToLoopbackOnly()
    {
        using var listener = LoopbackCallbackListener.Start(0);

        // An address on another interface of this machine must not reach it.
        var other = Dns.GetHostAddresses(Dns.GetHostName())
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

        if (other is null)
        {
            return; // A box with no non-loopback IPv4 has nothing to prove here.
        }

        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(other, listener.Port));
    }

    // ---- the whole flow --------------------------------------------------------------------

    [Fact]
    public async Task ABrowserSignInRegistersStoresEncryptedAndGrantsPlanUsage()
    {
        var session = Session();
        var hostId = session.HostId();

        var signIn = ChatGptBrowserSignIn.Begin(hostId, session.Hints(null, newAccount: false), preferredPort: 0);
        var grantTask = signIn.CompleteAsync(openAi.Client(), session.Validator, null, TimeSpan.FromSeconds(20), CancellationToken.None);

        await BrowserAsync(openAi.Authorize(signIn.AuthorizeUrl));

        var grant = await grantTask;
        Assert.StartsWith("oaiapp_test", grant.ClientId);
        Assert.Equal("user-sub-1", grant.Claims.Subject);

        var state = await session.ApplySignInAsync(grant, CancellationToken.None);

        Assert.True(state.SignedIn);
        Assert.True(state.PlanUsage);
        Assert.Equal("user@example.com", state.Email);
        Assert.Contains(hostId, openAi.HostIdsSeen);

        // At rest the tokens are ciphertext: neither token appears in any file in the store.
        var issued = openAi.IssuedAccessTokens.Keys.Single();
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            Assert.DoesNotContain(issued, File.ReadAllText(file));
        }

        Assert.Equal(issued, await Session().AccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ASignInWithoutThePlanScopeIsSignedInAndRefusesInferenceWithA403()
    {
        openAi.GrantedScope = "email offline_access openid profile resource.invoke";
        var session = await SignedInAsync();

        var state = session.State();
        Assert.True(state.SignedIn);
        Assert.False(state.PlanUsage);

        var ex = await Assert.ThrowsAsync<ChatGptUnavailableException>(() => session.AccessTokenAsync(CancellationToken.None));
        Assert.Equal(403, ex.StatusCode);
        Assert.Contains("Nothing was sent to a billable API", ex.Message);
    }

    [Fact]
    public async Task AReturningSignInAsAnotherAccountReplacesNothing()
    {
        var session = await SignedInAsync();
        var hints = session.Hints(null, newAccount: false);
        Assert.NotNull(hints.ClientId);

        openAi.Subject = "someone-else";

        var signIn = ChatGptBrowserSignIn.Begin(session.HostId(), hints, 0);
        var grantTask = signIn.CompleteAsync(openAi.Client(), session.Validator, hints.RegistrationId, TimeSpan.FromSeconds(20), CancellationToken.None);
        await BrowserAsync(openAi.Authorize(signIn.AuthorizeUrl));
        var grant = await grantTask;

        var ex = await Assert.ThrowsAsync<ChatGptOAuthException>(() => session.ApplySignInAsync(grant, CancellationToken.None));
        Assert.Equal("identity_mismatch", ex.Code);
        Assert.Equal("user@example.com", session.State().Email);
    }

    [Fact]
    public async Task AddingASecondAccountKeepsTheFirstsClientAndCredentialsApart()
    {
        var session = await SignedInAsync();
        var first = session.State().Registrations.Single();

        openAi.Subject = "second-sub";
        openAi.Email = "second@example.com";
        await SignInAsync(session, newAccount: true);

        var state = session.State();
        Assert.Equal(2, state.Registrations.Count);
        Assert.Equal("second@example.com", state.Email);

        var accounts = new ChatGptCredentialStore(directory).Load();
        Assert.NotEqual(accounts.Registrations[0].ClientId, accounts.Registrations[1].ClientId);
        Assert.NotEqual(accounts.Registrations[0].RefreshToken, accounts.Registrations[1].RefreshToken);

        await session.ActivateAsync(first.Id, CancellationToken.None);
        Assert.Equal("user@example.com", session.State().Email);
    }

    // ---- refresh, sign-out -----------------------------------------------------------------

    [Fact]
    public async Task ATokenNearExpiryIsRefreshedAndTheRotatedRefreshTokenIsSavedWithIt()
    {
        openAi.ExpiresIn = 60; // inside the five-minute refresh window from the start
        var session = await SignedInAsync();
        var before = new ChatGptCredentialStore(directory).Load().ActiveRegistration()!;

        var token = await session.AccessTokenAsync(CancellationToken.None);

        var after = new ChatGptCredentialStore(directory).Load().ActiveRegistration()!;
        Assert.Equal(1, openAi.Refreshes);
        Assert.NotEqual(before.AccessToken, token);
        Assert.Equal(token, after.AccessToken);
        Assert.NotEqual(before.RefreshToken, after.RefreshToken);

        // Scope was omitted from the refresh, so the grant — and plan usage — survive it.
        Assert.True(session.State().PlanUsage);
    }

    [Fact]
    public async Task AnUnusableRefreshTokenClearsTheSessionAndKeepsTheRegistration()
    {
        openAi.ExpiresIn = 60;
        var session = await SignedInAsync();
        openAi.RefreshError = "refresh_token_expired";

        var ex = await Assert.ThrowsAsync<ChatGptUnavailableException>(() => session.AccessTokenAsync(CancellationToken.None));
        Assert.Equal(401, ex.StatusCode);

        var registration = new ChatGptCredentialStore(directory).Load().ActiveRegistration()!;
        Assert.Null(registration.RefreshToken);
        Assert.StartsWith("oaiapp_", registration.ClientId);
    }

    [Fact]
    public async Task ATemporaryRefreshFailureKeepsTheCredentials()
    {
        openAi.ExpiresIn = 60;
        var session = await SignedInAsync();
        openAi.RefreshError = "server_error";

        await Assert.ThrowsAsync<ChatGptUnavailableException>(() => session.AccessTokenAsync(CancellationToken.None));

        Assert.NotNull(new ChatGptCredentialStore(directory).Load().ActiveRegistration()!.RefreshToken);
    }

    [Fact]
    public async Task SigningOutRevokesClearsTheTokensAndKeepsTheClientForNextTime()
    {
        var session = await SignedInAsync();

        Assert.True(await session.SignOutAsync(null, CancellationToken.None));
        Assert.Equal(1, openAi.Revocations);

        var registration = new ChatGptCredentialStore(directory).Load().ActiveRegistration()!;
        Assert.Null(registration.AccessToken);
        Assert.Null(registration.RefreshToken);

        // The next sign-in to it is a returning one.
        Assert.Equal(registration.ClientId, session.Hints(null, newAccount: false).ClientId);
    }

    // ---- the ID token ----------------------------------------------------------------------

    [Fact]
    public async Task TheIdTokenIsCheckedForSignatureIssuerAudienceExpiryAndNonce()
    {
        var validator = new IdTokenValidator(_ => Task.FromResult(openAi.Jwks()));

        var good = openAi.IdToken("oaiapp_1", "n1");
        Assert.Equal("user-sub-1", (await validator.ValidateAsync(good, "oaiapp_1", "n1", CancellationToken.None)).Subject);

        await AssertRejected(validator, good, "oaiapp_1", "other-nonce", "nonce");
        await AssertRejected(validator, good, "oaiapp_2", "n1", "audience");
        await AssertRejected(validator, openAi.IdToken("oaiapp_1", "n1", expires: DateTimeOffset.UtcNow.AddHours(-1)), "oaiapp_1", "n1", "expired");
        await AssertRejected(validator, openAi.IdToken("oaiapp_1", "n1", issuer: "https://evil.example"), "oaiapp_1", "n1", "issuer");

        using var forger = RSA.Create(2048);
        await AssertRejected(validator, openAi.IdToken("oaiapp_1", "n1", signer: forger), "oaiapp_1", "n1", "signature");
    }

    private static async Task AssertRejected(IdTokenValidator validator, string token, string client, string nonce, string because)
    {
        var ex = await Assert.ThrowsAsync<ChatGptOAuthException>(() => validator.ValidateAsync(token, client, nonce, CancellationToken.None));
        Assert.Equal("invalid_id_token", ex.Code);
        Assert.Contains(because, ex.Message);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private ChatGptSession Session()
        => new(new ChatGptCredentialStore(directory), openAi.Client, new IdTokenValidator(_ => Task.FromResult(openAi.Jwks())));

    private async Task<ChatGptSession> SignedInAsync()
    {
        var session = Session();
        await SignInAsync(session, newAccount: false);
        return session;
    }

    private async Task SignInAsync(ChatGptSession session, bool newAccount)
    {
        var hints = session.Hints(null, newAccount);
        var signIn = ChatGptBrowserSignIn.Begin(session.HostId(), hints, 0);
        var grantTask = signIn.CompleteAsync(openAi.Client(), session.Validator, hints.RegistrationId, TimeSpan.FromSeconds(20), CancellationToken.None);
        await BrowserAsync(openAi.Authorize(signIn.AuthorizeUrl));
        await session.ApplySignInAsync(await grantTask, CancellationToken.None);
    }

    /// <summary>What the browser does with the redirect: a GET to the loopback callback.</summary>
    private static async Task BrowserAsync(string callbackUrl)
    {
        using var browser = new HttpClient();
        var page = await browser.GetStringAsync(callbackUrl);
        Assert.Contains("InferHub", page);
    }
}
