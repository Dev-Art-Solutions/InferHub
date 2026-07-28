using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 37. What solo mode serves, what it refuses, and what it refuses to boot with.
/// </summary>
/// <remarks>
/// Everything here crosses a real Kestrel port through the real middleware pipeline. The surface
/// table in the phase brief is the contract and these are its teeth — a route that quietly appears
/// (or quietly stops answering) is the failure mode, and neither is visible from a handler test.
/// </remarks>
public class SoloModeTests
{
    // ---- the surface (D5) ------------------------------------------------------------------

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/version")]
    [InlineData("/api/status")]
    [InlineData("/api/tags")]
    [InlineData("/v1/models")]
    public async Task TheReadRoutesAnswer(string path)
    {
        await using var host = await SoloHost.StartAsync();

        var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    // Everything that needs a fleet or a store, and is a deliberate non-goal.
    [InlineData("/api/admin/nodes")]
    [InlineData("/api/collections/docs/documents")]
    [InlineData("/api/vector/docs")]
    [InlineData("/metrics")]
    [InlineData("/console")]
    [InlineData("/api/nodes")]
    public async Task TheFleetAndStoreRoutesAreNotThere(string path)
    {
        await using var host = await SoloHost.StartAsync();

        var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HealthIsOpenAndSaysWhichModeThisIs()
    {
        await using var host = await SoloHost.StartAsync(null, "--LocalApi:ApiKeys:0=secret");

        // No Authorization header: a monitor must not need a key that can spend GPU time.
        var response = await host.Client.GetAsync("/health");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("solo", body.GetProperty("mode").GetString());
        Assert.Equal("scripted", body.GetProperty("backend").GetString());
    }

    [Fact]
    public async Task StatusIsItsOwnDocumentAndDoesNotFakeAFleet()
    {
        await using var host = await SoloHost.StartAsync();

        var body = await host.Client.GetFromJsonAsync<JsonElement>("/api/status");

        Assert.Equal("solo", body.GetProperty("mode").GetString());
        Assert.Equal("solo-test", body.GetProperty("name").GetString());

        // A dashboard reading nodesEvicted: 0 from a process with no concept of nodes is worse
        // than one that gets nothing — so the hub's fleet keys are absent, not zeroed.
        Assert.False(body.TryGetProperty("nodes", out _));
        Assert.False(body.TryGetProperty("metrics", out _));
    }

    [Fact]
    public async Task TagsHonoursTheSameModelFilterTheNodeReportsToAHub()
    {
        var backend = new ScriptedBackend
        {
            Models = [new("llama3", null, null), new("internal-scratch", null, null)]
        };

        await using var host = await SoloHost.StartAsync(backend, "--Node:Models:Exclude:0=internal-scratch");

        var body = await host.Client.GetFromJsonAsync<JsonElement>("/api/tags");
        var names = body.GetProperty("models").EnumerateArray()
            .Select(model => model.GetProperty("name").GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal(["llama3"], names);
    }

    // ---- retrieval is refused, never silently skipped (D8) ---------------------------------

    [Fact]
    public async Task ARetrievalHeaderIsRefusedOnTheOllamaSurface()
    {
        await using var host = await SoloHost.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = Json("""{"model":"llama3","messages":[{"role":"user","content":"hi"}],"stream":false}""")
        };
        request.Headers.Add("X-InferHub-Retrieve", "docs");

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Answering ungrounded without saying so is the failure this prevents: a developer moving
        // a working RAG app onto a solo node would get confident, fluent, wrong answers.
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains("retrieval is not available in solo mode", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ARetrievalHeaderIsRefusedInTheOpenAiEnvelope()
    {
        await using var host = await SoloHost.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = Json("""{"model":"llama3","messages":[{"role":"user","content":"hi"}]}""")
        };
        request.Headers.Add("X-InferHub-Retrieve", "docs");

        var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Equal("retrieval_unavailable", body.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- auth (D4) -------------------------------------------------------------------------

    [Fact]
    public async Task LoopbackNeedsNoKeyByDefault()
    {
        await using var host = await SoloHost.StartAsync(null, "--LocalApi:ApiKeys:0=secret");

        var response = await host.Client.GetAsync("/api/tags");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LoopbackCanBeMadeToRequireAKey()
    {
        await using var host = await SoloHost.StartAsync(
            null,
            "--LocalApi:ApiKeys:0=secret",
            "--LocalApi:RequireAuthForLoopback=true");

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/tags")).StatusCode);

        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", "secret");
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/tags")).StatusCode);

        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/tags")).StatusCode);
    }

    [Fact]
    public async Task EachSurfaceRejectsInItsOwnDialect()
    {
        await using var host = await SoloHost.StartAsync(
            null,
            "--LocalApi:ApiKeys:0=secret",
            "--LocalApi:RequireAuthForLoopback=true");

        // An OpenAI SDK parses error.error.message; handed the Ollama envelope it shows the user a
        // useless "unknown error".
        var openAi = await (await host.Client.GetAsync("/v1/models")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_api_key", openAi.GetProperty("error").GetProperty("code").GetString());

        var ollama = await (await host.Client.GetAsync("/api/tags")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("missing bearer token", ollama.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ANonLoopbackBindWithNoKeysRefusesToStart()
    {
        var ex = await SoloHost.StartExpectingFailureAsync(
            "--LocalApi:Enabled=true",
            "--Coordinator:Enabled=false",
            "--LocalApi:Urls=http://0.0.0.0:5199");

        // A keyless inference API on a LAN hands arbitrary compute on somebody's GPU to anyone who
        // can reach the port. Deliberately stricter than phase-35 D4's warn-don't-refuse.
        Assert.Contains("LocalApi:ApiKeys", ex.Message);
        Assert.Contains("LocalApi:AllowAnonymous", ex.Message);
    }

    [Fact]
    public async Task ANonLoopbackBindWithKeysIsFine()
    {
        var options = Validate(
            new LocalApiOptions
            {
                Enabled = true,
                Urls = "http://0.0.0.0:5199",
                ApiKeys = { "secret" }
            });

        Assert.True(options.Succeeded);
    }

    [Fact]
    public void AllowAnonymousIsTheExplicitEscapeHatch()
    {
        var options = Validate(
            new LocalApiOptions
            {
                Enabled = true,
                Urls = "http://0.0.0.0:5199",
                AllowAnonymous = true
            });

        Assert.True(options.Succeeded);
    }

    [Theory]
    [InlineData("http://+:8080")]
    [InlineData("http://*:8080")]
    [InlineData("http://0.0.0.0:8080")]
    [InlineData("http://localhost:5081;http://+:8080")]
    public void KestrelsWildcardAddressesAreValidAddresses(string urls)
    {
        // v3.5.0 shipped with this validated through Uri.TryCreate, which rejects `+` and `*`.
        // Kestrel accepts both, and `http://+:8080` is exactly what the node image sets — so solo
        // mode could not start in Docker at all, with a message blaming the URL format. Found by
        // running the published image (D7), fixed in v3.5.1. If this goes red, that is back.
        var result = Validate(new LocalApiOptions
        {
            Enabled = true,
            Urls = urls,
            ApiKeys = { "secret" }
        });

        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void AWildcardAddressIsStillTreatedAsExposedForTheKeyCheck()
    {
        // Parsing it and permitting it are different questions, and conflating them is what caused
        // the v3.5.0 bug in the first place: a wildcard is the *most* exposed address there is.
        var result = Validate(new LocalApiOptions { Enabled = true, Urls = "http://+:8080" });

        Assert.False(result.Succeeded);
        Assert.Contains("LocalApi:ApiKeys", result.FailureMessage);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://localhost:5081")]
    [InlineData("localhost:5081")]
    public void GenuineNonsenseIsStillRejected(string urls)
    {
        var result = Validate(new LocalApiOptions { Enabled = true, Urls = urls, ApiKeys = { "secret" } });

        Assert.False(result.Succeeded);
        Assert.Contains("must be absolute http(s) URLs", result.FailureMessage);
    }

    [Theory]
    [InlineData("http://localhost:5081", true)]
    [InlineData("http://127.0.0.1:5081", true)]
    [InlineData("http://[::1]:5081", true)]
    [InlineData("http://0.0.0.0:5081", false)]
    [InlineData("http://+:5081", false)]
    [InlineData("http://*:5081", false)]
    [InlineData("http://10.0.0.7:5081", false)]
    // Two addresses where one is exposed: the set is only loopback if every member is.
    [InlineData("http://localhost:5081;http://0.0.0.0:5082", false)]
    public void LoopbackIsRecognisedIncludingKestrelsWildcards(string urls, bool expected)
    {
        Assert.Equal(expected, new LocalApiOptions { Urls = urls }.BindsLoopbackOnly());
    }

    // ---- the mode matrix (D10) --------------------------------------------------------------

    [Fact]
    public async Task ANodeThatNeitherJoinsAMeshNorServesAnyoneRefusesToStart()
    {
        var ex = await SoloHost.StartExpectingFailureAsync(
            "--LocalApi:Enabled=false",
            "--Coordinator:Enabled=false");

        Assert.Contains("Coordinator:Enabled", ex.Message);
        Assert.Contains("LocalApi:Enabled", ex.Message);
    }

    [Fact]
    public async Task SoloNeedsNoCoordinatorUrlAtAll()
    {
        // The user-facing promise is "no internet". A URL it will never dial must not be required,
        // and today's validator demands one unconditionally.
        await using var host = await SoloHost.StartAsync(null, "--Coordinator:Url=");

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public void TheDefaultNodeIsStillAPlainWorkerWithNoWebHost()
    {
        // Solo off must cost nothing: no Kestrel, no listening socket, no routing middleware.
        var builder = NodeHostFactory.Create(["--Node:Name=default-node"]);

        Assert.IsNotType<Microsoft.AspNetCore.Builder.WebApplicationBuilder>(builder);
    }

    [Fact]
    public void SoloOnProducesAWebHost()
    {
        var builder = NodeHostFactory.Create(["--LocalApi:Enabled=true", "--Node:Name=solo-node"]);

        Assert.IsType<Microsoft.AspNetCore.Builder.WebApplicationBuilder>(builder);
    }

    // ---- concurrency (D9) --------------------------------------------------------------------

    [Fact]
    public async Task WithNoCapConfiguredNothingIsGated()
    {
        // MaxConcurrency unset means unbounded, exactly as it does when a hub is doing the
        // respecting — so two requests reach the backend at once rather than queueing behind
        // each other.
        var hold = new SemaphoreSlim(0);
        var backend = new ScriptedBackend { Hold = hold };

        await using var host = await SoloHost.StartAsync(backend);

        var first = host.Client.PostAsync("/api/chat", ChatBody());
        var second = host.Client.PostAsync("/api/chat", ChatBody());

        await backend.WaitForInFlightAsync(2);

        hold.Release(2);
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second).StatusCode);
    }

    [Fact]
    public async Task OverTheCapIsA503WithRetryAfterJustLikeTheHubsQueue()
    {
        var hold = new SemaphoreSlim(0);
        var backend = new ScriptedBackend { Hold = hold };

        await using var host = await SoloHost.StartAsync(
            backend,
            "--Node:MaxConcurrency=1",
            "--LocalApi:MaxWaitSeconds=1");

        // Fill the single slot and leave it filled.
        var blocking = host.Client.PostAsync("/api/chat", ChatBody());
        await backend.WaitForInFlightAsync(1);

        var rejected = await host.Client.PostAsync("/api/chat", ChatBody());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);

        // Same status and same header as the hub's RequestQueue, so a client's existing retry
        // logic behaves identically against either (phase-25 D5).
        Assert.NotNull(rejected.Headers.RetryAfter);

        // The rejected request never reached the backend — it was turned away at the gate.
        Assert.Equal(1, backend.InFlight);

        hold.Release(2);
        await blocking;
    }

    [Fact]
    public async Task TheSlotIsReturnedAfterTheRequestSoTheNextOneGetsIn()
    {
        await using var host = await SoloHost.StartAsync(null, "--Node:MaxConcurrency=1");

        for (var i = 0; i < 3; i++)
        {
            var response = await host.Client.PostAsync("/api/chat", ChatBody());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    // ---- errors ------------------------------------------------------------------------------

    [Fact]
    public async Task ABackendRefusalIsUnwrappedRatherThanForwardedTripleEncoded()
    {
        // The real captured shape: Ollama stuffs its backend's JSON error into its own error field
        // as a string. With no hub in between, an unwrapped one lands straight in the user's
        // terminal (phase-29 D6).
        var backend = new ScriptedBackend
        {
            Failure = new InvalidOperationException(
                """{"error":"{\"error\":{\"code\":400,\"message\":\"this model does not support images\"}}"}""")
        };

        await using var host = await SoloHost.StartAsync(backend);

        var response = await host.Client.PostAsync(
            "/v1/chat/completions",
            Json("""{"model":"llama3","messages":[{"role":"user","content":"hi"}]}"""));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(
            "this model does not support images",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task AnUnknownModelIs404InTheOpenAiEnvelope()
    {
        await using var host = await SoloHost.StartAsync();

        var response = await host.Client.GetAsync("/v1/models/does-not-exist");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("model_not_found", body.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static StringContent ChatBody()
        => Json("""{"model":"llama3","messages":[{"role":"user","content":"hi"}],"stream":false}""");

    private static ValidateOptionsResult Validate(LocalApiOptions options)
        => new LocalApiOptionsValidator().Validate(null, options);
}
