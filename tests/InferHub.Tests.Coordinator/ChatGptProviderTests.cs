using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InferHub.Coordinator.ChatGpt;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.ChatGpt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 103, the coordinator's half: a <c>Type: chatgpt</c> provider signed in through the console's
/// one-time pairing by the helper, then serving a real request through the shipped endpoints — on a
/// real Kestrel hub, with <see cref="FakeOpenAi"/> standing in for OpenAI.
/// </summary>
public sealed class ChatGptProviderTests : IAsyncLifetime
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "inferhub-hub-chatgpt-" + Guid.NewGuid().ToString("N"));
    private readonly FakeOpenAi openAi = new();
    private HubHost hub = null!;

    public async Task InitializeAsync()
    {
        hub = await HubHost.StartAsync(
            blockingResponse: "{}",
            providers: Providers(),
            configure: builder =>
            {
                builder.Services.Configure<ChatGptOptions>(options => options.DataDirectory = dataDirectory);
                builder.Services.AddSingleton(TimeProvider.System);
                builder.Services.AddHttpClient(ChatGptAccounts.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Borrowed(openAi));
                builder.Services.AddHttpClient(ProviderDispatcher.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Borrowed(openAi));
                builder.Services.AddSingleton<ChatGptAccounts>();
            },
            map: app => app.MapChatGptEndpoints());
    }

    public async Task DisposeAsync()
    {
        await hub.DisposeAsync();
        openAi.Dispose();

        if (Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static ProviderOptions Providers()
    {
        var options = new ProviderOptions();
        options.Entries["plan"] = new ProviderDefinition
        {
            Type = ProviderDefinition.TypeChatGpt,
            // `only`: this fixture's one scripted node answers for every model name, and a provider
            // under the default policy would (correctly) leave the request to it.
            Policy = ProviderPolicy.Only,
            ModelMap = new(StringComparer.OrdinalIgnoreCase) { ["chatgpt"] = ProviderDefinition.SelectedModel, ["sol"] = "gpt-6.1-sol" }
        };

        // A second, key-authenticated provider beside it: the new type must leave this one alone.
        options.Entries["openai"] = new ProviderDefinition
        {
            Type = ProviderDefinition.TypeOpenAiCompatible,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-test",
            ModelMap = new(StringComparer.OrdinalIgnoreCase) { ["gpt-api"] = "gpt-4.1" }
        };

        return options;
    }

    // ---- the pairing -----------------------------------------------------------------------

    [Fact]
    public async Task TheHelperSignsTheCoordinatorInThroughAOneTimePairing()
    {
        var result = await PairAsync();

        Assert.True(result.PlanUsage);
        Assert.Equal("user@example.com", result.Email);

        var detail = await hub.Client.GetFromJsonAsync<JsonElement>("/api/admin/chatgpt/plan");
        Assert.True(detail.GetProperty("account").GetProperty("signedIn").GetBoolean());

        // The registration is attributed to the COORDINATOR's host id, not to the helper's machine.
        var hostId = detail.GetProperty("hostId").GetString();
        Assert.Equal([hostId!], openAi.HostIdsSeen.ToArray());

        // The account's own catalogue, visible models only.
        Assert.Equal(["gpt-6.1-sol", "gpt-6.1-mini"],
            detail.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("slug").GetString()!).ToArray());

        // No token in what the console reads.
        var raw = await hub.Client.GetStringAsync("/api/admin/chatgpt/plan");
        foreach (var token in openAi.IssuedAccessTokens.Keys)
        {
            Assert.DoesNotContain(token, raw);
        }

        Assert.DoesNotContain("refresh", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APairingCodeWorksOnceAndAnUnknownOneLearnsNothing()
    {
        var code = await CreatePairingAsync();
        await PairAsync(code);

        using var again = new HttpRequestMessage(HttpMethod.Post, ChatGptPairing.BeginPath);
        again.Headers.TryAddWithoutValidation(ChatGptPairing.HeaderName, code);
        var reused = await hub.Client.SendAsync(again);

        using var unknown = new HttpRequestMessage(HttpMethod.Post, ChatGptPairing.BeginPath);
        unknown.Headers.TryAddWithoutValidation(ChatGptPairing.HeaderName, "ihp_nonsense");
        var guessed = await hub.Client.SendAsync(unknown);

        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, guessed.StatusCode);
        Assert.Equal(await reused.Content.ReadAsStringAsync(), await guessed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnIdTokenFromAnotherSignInIsRejectedBecauseTheCoordinatorChoseTheNonce()
    {
        var code = await CreatePairingAsync();

        using var begin = new HttpRequestMessage(HttpMethod.Post, ChatGptPairing.BeginPath);
        begin.Headers.TryAddWithoutValidation(ChatGptPairing.HeaderName, code);
        Assert.Equal(HttpStatusCode.OK, (await hub.Client.SendAsync(begin)).StatusCode);

        // A perfectly valid token, from a sign-in that used some other nonce.
        using var complete = new HttpRequestMessage(HttpMethod.Post, ChatGptPairing.CompletePath)
        {
            Content = JsonContent.Create(new PairingCompleteRequest
            {
                ClientId = "oaiapp_x",
                AccessToken = "at-x",
                RefreshToken = "rt-x",
                IdToken = openAi.IdToken("oaiapp_x", "a-nonce-the-hub-never-issued"),
                Scope = openAi.GrantedScope
            })
        };
        complete.Headers.TryAddWithoutValidation(ChatGptPairing.HeaderName, code);

        var response = await hub.Client.SendAsync(complete);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("nonce", await response.Content.ReadAsStringAsync());
        Assert.False((await hub.Client.GetFromJsonAsync<JsonElement>("/api/admin/chatgpt/plan")).GetProperty("account").GetProperty("signedIn").GetBoolean());
    }

    [Fact]
    public async Task TheHelperRefusesToSendASessionOverPlainHttpToAnotherMachine()
    {
        var ex = Assert.Throws<ChatGptOAuthException>(() => ChatGptPairing.RequireSecure(new Uri("http://hub.example.com"), allowInsecureHttp: false));
        Assert.Equal("insecure_hub", ex.Code);

        ChatGptPairing.RequireSecure(new Uri("https://hub.example.com"), allowInsecureHttp: false);
        ChatGptPairing.RequireSecure(new Uri("http://127.0.0.1:5080"), allowInsecureHttp: false);
    }

    // ---- inference -------------------------------------------------------------------------

    [Fact]
    public async Task ASignedInProviderServesAChatOnTheAccountsPlan()
    {
        await PairAsync();

        var response = await hub.Client.PostAsync("/api/chat", Json("""{"model":"sol","stream":false,"messages":[{"role":"user","content":"hi"}]}"""));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Hello world", body);
        Assert.Equal("provider:plan", response.Headers.GetValues("X-InferHub-Served-By").Single());

        var call = openAi.Requests.Last(r => r.Uri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal));
        Assert.Contains("\"model\":\"gpt-6.1-sol\"", call.Body);
        Assert.Contains("\"store\":false", call.Body);
    }

    [Fact]
    public async Task SelectedMapsToTheModelPickedInTheConsoleAndRefusesUntilOneIs()
    {
        await PairAsync();

        var before = await hub.Client.PostAsync("/api/chat", Json("""{"model":"chatgpt","stream":false,"messages":[{"role":"user","content":"hi"}]}"""));
        Assert.Equal(HttpStatusCode.Conflict, before.StatusCode);
        Assert.Contains("no model is selected", await before.Content.ReadAsStringAsync());

        var notListed = await hub.Client.PutAsJsonAsync("/api/admin/chatgpt/plan/model", new { model = "internal-x" });
        Assert.Equal(HttpStatusCode.BadRequest, notListed.StatusCode);

        var saved = await hub.Client.PutAsJsonAsync("/api/admin/chatgpt/plan/model", new { model = "gpt-6.1-mini" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var after = await hub.Client.PostAsync("/api/chat", Json("""{"model":"chatgpt","stream":false,"messages":[{"role":"user","content":"hi"}]}"""));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Contains("\"model\":\"gpt-6.1-mini\"", openAi.Requests.Last(r => r.Uri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal)).Body);
    }

    [Fact]
    public async Task AUsageLimitIsA429ToTheCallerAndNeverAPaidApiCall()
    {
        await PairAsync();
        openAi.Responses = _ => (HttpStatusCode.TooManyRequests, [],
            """{"error":{"code":"subscription_sharing_usage_limit_exceeded","message":"Usage limit reached."}}""");

        var response = await hub.Client.PostAsync("/api/chat", Json("""{"model":"sol","stream":false,"messages":[{"role":"user","content":"hi"}]}"""));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("subscription_sharing_usage_limit_exceeded", await response.Content.ReadAsStringAsync());

        // Nothing went to any other route, and in particular no API-key call was made.
        Assert.DoesNotContain(openAi.Requests, r => r.Authorization == "Bearer sk-test");
    }

    [Fact]
    public async Task ASignedOutProviderRefusesWithA401NamingTheFix()
    {
        var response = await hub.Client.PostAsync("/api/chat", Json("""{"model":"sol","stream":false,"messages":[{"role":"user","content":"hi"}]}"""));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Continue with ChatGPT", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(openAi.Requests, r => r.Uri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisconnectRevokesAndTheProviderStopsServing()
    {
        await PairAsync();

        var result = await hub.Client.PostAsJsonAsync("/api/admin/chatgpt/plan/disconnect", new { });
        Assert.True((await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revoked").GetBoolean());
        Assert.Equal(1, openAi.Revocations);

        var response = await hub.Client.PostAsync("/api/chat", Json("""{"model":"sol","stream":false,"messages":[{"role":"user","content":"hi"}]}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- status and validation -------------------------------------------------------------

    [Fact]
    public void TheStatusBlockSaysSignedInOrOutAndTheOtherProvidersKeepTheirShape()
    {
        var accounts = new ChatGptAccounts(
            new ProviderRegistry(Options.Create(Providers()), Options.Create(new FallbackOptions())),
            Options.Create(new ChatGptOptions { DataDirectory = dataDirectory }),
            new TestHostEnvironment(),
            new SingleClientFactory(openAi),
            TimeProvider.System);

        var blocks = StatusEndpoint.BuildProviderBlocks(
            new ProviderRegistry(Options.Create(Providers()), Options.Create(new FallbackOptions())),
            new Metrics().Snapshot(DateTimeOffset.UtcNow),
            accounts)!;

        var plan = blocks.Single(b => b.Id == "plan");
        Assert.Equal("signed-out", plan.Credential);
        Assert.NotNull(plan.ChatGpt);

        var openai = blocks.Single(b => b.Id == "openai");
        Assert.Equal("configured", openai.Credential);
        Assert.Null(openai.ChatGpt);
        Assert.DoesNotContain("chatGpt", JsonSerializer.Serialize(openai, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void AChatGptProviderWithAnApiKeyFailsStartup()
    {
        var options = new ProviderOptions();
        options.Entries["plan"] = new ProviderDefinition
        {
            Type = ProviderDefinition.TypeChatGpt,
            ApiKey = "sk-should-not-be-here",
            ModelMap = new(StringComparer.OrdinalIgnoreCase) { ["sol"] = "gpt-6.1-sol" }
        };

        var result = new ProviderOptionsValidator(Options.Create(new FallbackOptions())).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("never an API key", result.FailureMessage);
    }

    [Fact]
    public void SelectedIsAChatGptWordAndNowhereElse()
    {
        var options = new ProviderOptions();
        options.Entries["openai"] = new ProviderDefinition
        {
            Type = ProviderDefinition.TypeOpenAiCompatible,
            BaseUrl = "https://api.openai.com/v1",
            ModelMap = new(StringComparer.OrdinalIgnoreCase) { ["x"] = ProviderDefinition.SelectedModel }
        };

        var result = new ProviderOptionsValidator(Options.Create(new FallbackOptions())).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(ProviderDefinition.SelectedModel, result.FailureMessage);
        Assert.True(new ProviderOptionsValidator(Options.Create(new FallbackOptions())).Validate(null, Providers()).Succeeded);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private async Task<string> CreatePairingAsync()
    {
        var pairing = await hub.Client.PostAsJsonAsync("/api/admin/chatgpt/plan/pairing", new { });
        pairing.EnsureSuccessStatusCode();
        return (await pairing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
    }

    /// <summary>The helper, run here, with the fixture playing the administrator's browser.</summary>
    private async Task<PairingCompleteResponse> PairAsync(string? code = null)
    {
        code ??= await CreatePairingAsync();

        return await ChatGptPairing.RunAsync(
            hub.Client.BaseAddress!,
            code,
            preferredPort: 0,
            allowInsecureHttp: false,
            openBrowser: false,
            TextWriter.Null,
            CancellationToken.None,
            oauthHandler: openAi,
            browser: async url =>
            {
                using var browser = new HttpClient();
                await browser.GetStringAsync(openAi.Authorize(url));
            });
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>The factory disposes its handlers; the fixture outlives them.</summary>
    private sealed class Borrowed(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
