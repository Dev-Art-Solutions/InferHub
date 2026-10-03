using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Colibri;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
using InferHub.Node.LocalApi;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 93: a node driving colibri's <c>coli serve</c>. The stub upstream answers what the real
/// gateway answers; the measurements against a real engine are in the release notes.
/// </summary>
public class ColibriBackendTests
{
    private const string ChatReply = """
    {"id":"c","object":"chat.completion","created":0,"model":"olmoe",
     "choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],
     "usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}}
    """;

    // ---- D1: the type ------------------------------------------------------------------

    [Fact]
    public void AColibriNodeDeclaresChatAloneAndDefaultsToTheEnginesOwnAddress()
    {
        // coli serve has no /v1/embeddings: a node that declared embed would have the hub route an
        // embedding job here and the client read the failure after the hop (67 D4).
        var backend = Backend(new Stub(ChatReply), slots: 1, new UpstreamBackendOptions());

        Assert.Equal(["chat"], backend.Kinds);
        Assert.Equal("colibri", backend.Name);
        Assert.Equal("http://127.0.0.1:8000/v1", backend.Endpoint);
        Assert.False(backend.SupportsModelManagement);
    }

    [Fact]
    public async Task AColibriNodeSpeaksTheOpenAiDialect()
    {
        var upstream = new Stub(ChatReply);

        var reply = await Backend(upstream, slots: 1).ChatAsync(
            """{"model":"olmoe","messages":[{"role":"user","content":"Hi!"}],"stream":false}""",
            CancellationToken.None);

        Assert.Equal("/v1/chat/completions", upstream.Path);
        Assert.Equal("hi", JsonDocument.Parse(reply).RootElement.GetProperty("message").GetProperty("content").GetString());
    }

    /// <summary>
    /// Found against a real engine, invisible to every stub before it: the shared client's
    /// <c>JsonContent</c> goes chunked, and colibri's gateway (Python's http.server) answers a body
    /// with no Content-Length with a 400 — every chat and every stream.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task EveryBodyCarriesAContentLength(int slots)
    {
        var upstream = new Stub(ChatReply);

        await Backend(upstream, slots).ChatAsync(
            """{"model":"olmoe","messages":[{"role":"user","content":"Hi!"}]}""", CancellationToken.None);

        Assert.Equal(upstream.SentBytes, upstream.ContentLength);
        Assert.True(upstream.ContentLength > 0);
    }

    // ---- D2: a conversation's slot is a function of its opening ------------------------

    [Fact]
    public async Task OneSlotAddsNothingSoTheWireIsWhatAnOpenAiNodeSends()
    {
        var upstream = new Stub(ChatReply);

        await Backend(upstream, slots: 1).ChatAsync(
            """{"model":"olmoe","messages":[{"role":"user","content":"Hi!"}]}""", CancellationToken.None);

        Assert.False(upstream.Body!.ContainsKey("cache_slot"));
    }

    [Fact]
    public async Task EveryTurnOfAConversationLandsOnTheSameSlot()
    {
        var upstream = new Stub(ChatReply);
        var backend = Backend(upstream, slots: 4);

        var slots = new List<int>();

        foreach (var request in new[]
                 {
                     """{"model":"m","messages":[{"role":"system","content":"Be brief."},{"role":"user","content":"Plan a trip to Sofia."}]}""",
                     """{"model":"m","messages":[{"role":"system","content":"Be brief."},{"role":"user","content":"Plan a trip to Sofia."},{"role":"assistant","content":"Day 1..."},{"role":"user","content":"And day 2?"}]}""",
                     """{"model":"m","messages":[{"role":"system","content":"Be brief."},{"role":"user","content":"Plan a trip to Sofia."},{"role":"assistant","content":"Day 1..."},{"role":"user","content":"And day 2?"},{"role":"assistant","content":"Day 2..."},{"role":"user","content":"Thanks"}]}"""
                 })
        {
            await backend.ChatAsync(request, CancellationToken.None);
            slots.Add(upstream.Body!["cache_slot"]!.GetValue<int>());
        }

        Assert.Single(slots.Distinct());
        Assert.InRange(slots[0], 0, 3);
    }

    [Fact]
    public void DifferentConversationsSpreadAcrossTheSlots()
    {
        var used = Enumerable.Range(0, 64)
            .Select(i => ColibriSlot.ForChat(Chat($"conversation number {i}"), 4))
            .Distinct()
            .ToList();

        // Not a distribution test — a hash that sent everything to one slot would pass every other
        // assertion here and buy nothing.
        Assert.Equal(4, used.Count);
    }

    [Fact]
    public void TheSlotIsStableAcrossProcessesBecauseItIsNotStringGetHashCode()
    {
        // A node restart must find the conversation where colibri's .coli_kv kept it.
        Assert.Equal(ColibriSlot.Slot("Plan a trip to Sofia.", 16), ColibriSlot.Slot("Plan a trip to Sofia.", 16));
        // FNV-1a-32("hello") is the published 0x4f9f2cab; 0xb is its low nibble.
        Assert.Equal(0xb, ColibriSlot.Slot("hello", 16));
    }

    [Fact]
    public void AGenerateIsPinnedByTheOpeningOfItsPrompt()
    {
        var document = new string('x', ColibriSlot.PromptOpeningChars);

        var first = ColibriSlot.ForCompletion(new JsonObject { ["prompt"] = document + " question one" }, 8);
        var second = ColibriSlot.ForCompletion(new JsonObject { ["prompt"] = document + " a different question" }, 8);

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task AGenerateGoesToTheCompletionsRouteWithItsSlot()
    {
        var upstream = new Stub("""
        {"id":"c","created":0,"model":"m","choices":[{"index":0,"text":"x","finish_reason":"stop"}]}
        """);

        await Backend(upstream, slots: 2).GenerateAsync("""{"model":"m","prompt":"Once upon a time"}""", CancellationToken.None);

        Assert.Equal("/v1/completions", upstream.Path);
        Assert.Equal(ColibriSlot.ForCompletion(new JsonObject { ["prompt"] = "Once upon a time" }, 2), upstream.Body!["cache_slot"]!.GetValue<int>());
    }

    [Fact]
    public async Task AStreamCarriesItsSlotToo()
    {
        var upstream = new Stub(
            "data: {\"id\":\"c\",\"created\":0,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
            "text/event-stream");

        await foreach (var _ in Backend(upstream, slots: 3).StreamAsync(
                           "chat", """{"model":"m","messages":[{"role":"user","content":"Hi"}],"stream":true}""", CancellationToken.None))
        {
        }

        Assert.True(upstream.Body!.ContainsKey("cache_slot"));
    }

    // ---- D4: the watcher ----------------------------------------------------------------

    [Fact]
    public void HealthIsAskedAtTheGatewaysRootNotUnderV1()
    {
        Assert.Equal("http://127.0.0.1:8000/health", ColibriWatcher.HealthUri("http://127.0.0.1:8000/v1").ToString());
        Assert.Equal("http://box:9000/health", ColibriWatcher.HealthUri("http://box:9000/v1/").ToString());
        Assert.Equal("http://box:9000/health", ColibriWatcher.HealthUri("http://box:9000").ToString());
    }

    [Fact]
    public async Task OneFailedProbeDecidesNothingAndTheThresholdDeclares()
    {
        var upstream = new Stub("{}") { Status = HttpStatusCode.ServiceUnavailable };
        var watcher = Watcher(upstream);

        await watcher.TickAsync(CancellationToken.None);
        await watcher.TickAsync(CancellationToken.None);
        Assert.Null(watcher.Health);

        await watcher.TickAsync(CancellationToken.None);
        Assert.Equal(BackendHealth.Wedged, watcher.Health);
        Assert.Equal("/health", upstream.Path);
    }

    [Fact]
    public async Task RecoveryIsPushedOnceSoTheNodeReReportsItsModels()
    {
        var upstream = new Stub("{}") { Status = HttpStatusCode.InternalServerError };
        var watcher = Watcher(upstream);
        var recovered = 0;
        watcher.Recovered += () => recovered++;

        for (var i = 0; i < 3; i++)
        {
            await watcher.TickAsync(CancellationToken.None);
        }

        upstream.Status = HttpStatusCode.OK;
        await watcher.TickAsync(CancellationToken.None);
        await watcher.TickAsync(CancellationToken.None);

        Assert.Equal(BackendHealth.Healthy, watcher.Health);
        Assert.Equal(1, recovered);
    }

    /// <summary>
    /// Found on a real boot: the launched engine is still loading when the node registers, so the
    /// first listing is "could not ask" and the node reports nothing. One failed probe never became
    /// an outage, so nothing pushed a report and the node sat unroutable until the 60 s refresh.
    /// </summary>
    [Fact]
    public async Task AnEngineThatComesUpBelowTheThresholdStillPushesAReport()
    {
        var upstream = new Stub("{}") { Status = HttpStatusCode.ServiceUnavailable };
        var watcher = Watcher(upstream);
        var recovered = 0;
        watcher.Recovered += () => recovered++;

        await watcher.TickAsync(CancellationToken.None);
        Assert.Null(watcher.Health);

        upstream.Status = HttpStatusCode.OK;
        await watcher.TickAsync(CancellationToken.None);
        await watcher.TickAsync(CancellationToken.None);

        Assert.Equal(BackendHealth.Healthy, watcher.Health);
        Assert.Equal(1, recovered);
    }

    [Fact]
    public async Task AnEngineHealthyFromTheFirstProbePushesNothing()
    {
        var watcher = Watcher(new Stub("{}"));
        var recovered = 0;
        watcher.Recovered += () => recovered++;

        await watcher.TickAsync(CancellationToken.None);

        Assert.Equal(0, recovered);
    }

    [Fact]
    public async Task NothingListeningIsUnreachable()
    {
        // A real closed port, not a stub: the classification lives in the connect callback.
        var port = FreePort();
        var factory = new ProbeFactory(new HttpClient(OllamaProbe.CreateHandler(TimeSpan.FromSeconds(2))) { Timeout = TimeSpan.FromSeconds(2) });
        var watcher = new ColibriWatcher(
            factory,
            Options.Create(new ColibriOptions { UnhealthyThreshold = 1 }),
            Options.Create(new UpstreamBackendOptions { BaseUrl = $"http://127.0.0.1:{port}/v1" }),
            TimeProvider.System,
            NullLogger<ColibriWatcher>.Instance);

        await watcher.TickAsync(CancellationToken.None);

        Assert.Equal(BackendHealth.Unreachable, watcher.Health);
    }

    // ---- D5: the launched engine --------------------------------------------------------

    [Fact]
    public void TheLaunchedEngineBindsLoopbackWithTheSlotsAndTheModelId()
    {
        var info = ColibriServe.StartInfo(new ColibriOptions
        {
            KvSlots = 4,
            Serve = { Model = "/models/olmoe/", Port = 8123, Python = "python3", Launcher = "/opt/colibri/coli" }
        });

        Assert.Equal("python3", info.FileName);
        Assert.Equal(
            ["/opt/colibri/coli", "serve", "--model", "/models/olmoe/", "--model-id", "olmoe",
             "--host", "127.0.0.1", "--port", "8123", "--kv-slots", "4"],
            info.ArgumentList);
    }

    [Fact]
    public void TheEngineDoesNotInheritThePromptTee()
    {
        // COLI_DEBUG tees prompts to stderr, and the node logs that stream (rule 7).
        Environment.SetEnvironmentVariable(ColibriServe.ContentTeeVariable, "2");

        try
        {
            var info = ColibriServe.StartInfo(new ColibriOptions { Serve = { Model = "/m" } });

            Assert.False(info.Environment.ContainsKey(ColibriServe.ContentTeeVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ColibriServe.ContentTeeVariable, null);
        }
    }

    [Fact]
    public async Task AMissingModelDirectoryIsOneWarningAndNoProcess()
    {
        using var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));

        var serve = new ColibriServe(
            Options.Create(new ColibriOptions { Serve = { Model = Path.Combine(Path.GetTempPath(), "no-such-colibri-model-" + Guid.NewGuid()), Python = "definitely-not-python" } }),
            TimeProvider.System,
            factory.CreateLogger<ColibriServe>());

        await serve.StartAsync(CancellationToken.None);
        await serve.ExecuteTask!;

        Assert.Contains(logs.Lines, line => line.Contains("colibri was not started", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line => line.Contains("Could not start", StringComparison.Ordinal));
    }

    // ---- composition ---------------------------------------------------------------------

    [Fact]
    public void AColibriNodeWatchesHealthAndDeclaresItsSlotsAsItsConcurrency()
    {
        using var host = BuildNode(("Backend:Type", "colibri"), ("Colibri:KvSlots", "4"));

        Assert.IsType<UpstreamBackend>(host.Services.GetRequiredService<IInferenceBackend>());
        Assert.IsType<ColibriWatcher>(host.Services.GetRequiredService<IBackendSupervisor>());
        Assert.Equal(4, host.Services.GetRequiredService<IOptions<NodeOptions>>().Value.MaxConcurrency);

        // An engine started elsewhere: nothing is launched.
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), service => service is ColibriServe);
        Assert.Null(host.Services.GetService<IOllamaProbe>());
    }

    [Fact]
    public void TheOperatorsMaxConcurrencyWins()
    {
        using var host = BuildNode(("Backend:Type", "colibri"), ("Colibri:KvSlots", "4"), ("Node:MaxConcurrency", "1"));

        Assert.Equal(1, host.Services.GetRequiredService<IOptions<NodeOptions>>().Value.MaxConcurrency);
    }

    [Fact]
    public void SoloModeOnColibriAlwaysHasAConcurrencyGate()
    {
        using var host = BuildNode(("Backend:Type", "colibri"), ("LocalApi:Enabled", "true"), ("LocalApi:AllowAnonymous", "true"));

        Assert.NotNull(host.Services.GetService<LocalConcurrencyGate>());
    }

    [Fact]
    public void ALaunchedEngineIsWherePromptsGo()
    {
        using var host = BuildNode(("Backend:Type", "colibri"), ("Colibri:Serve:Model", "/models/olmoe"), ("Colibri:Serve:Port", "8123"));

        Assert.Contains(host.Services.GetServices<IHostedService>(), service => service is ColibriServe);
        Assert.Equal("http://127.0.0.1:8123/v1", host.Services.GetRequiredService<IInferenceBackend>().Endpoint);
    }

    [Fact]
    public void ALaunchedEngineAndABaseUrlIsAStartupFailureNamingBoth()
    {
        using var host = BuildNode(
            ("Backend:Type", "colibri"),
            ("Colibri:Serve:Model", "/models/olmoe"),
            ("Upstream:BaseUrl", "http://elsewhere:8000/v1"));

        var ex = Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IOptions<ColibriOptions>>().Value);

        Assert.Contains("Colibri:Serve:Model", ex.Message);
        Assert.Contains("Upstream:BaseUrl", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("17")]
    public void ASlotCountTheEngineCannotHaveFailsNamingTheKey(string slots)
    {
        using var host = BuildNode(("Backend:Type", "colibri"), ("Colibri:KvSlots", slots));

        var ex = Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IOptions<ColibriOptions>>().Value);

        Assert.Contains("Colibri:KvSlots", ex.Message);
    }

    [Fact]
    public void ANodeThatIsNotColibriIsUntouched()
    {
        // A deployment that changes no config: same supervisor, same concurrency, nothing launched,
        // and a bad Colibri: value on a node that does not use it is not a reason to refuse to boot.
        using var host = BuildNode(("Colibri:KvSlots", "99"));

        Assert.IsType<OllamaSupervisor>(host.Services.GetRequiredService<IBackendSupervisor>());
        Assert.Null(host.Services.GetRequiredService<IOptions<NodeOptions>>().Value.MaxConcurrency);
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), service => service is ColibriServe or ColibriWatcher);
        _ = host.Services.GetRequiredService<IOptions<ColibriOptions>>().Value;
    }

    [Fact]
    public void WatchingCanBeTurnedOff()
    {
        using var host = BuildNode(("Backend:Type", "colibri"), ("Colibri:Watch", "false"));

        Assert.IsType<NoBackendSupervisor>(host.Services.GetRequiredService<IBackendSupervisor>());
    }

    // ---- harness ---------------------------------------------------------------------

    private static JsonObject Chat(string opening)
        => new() { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = opening }) };

    private static UpstreamBackend Backend(Stub upstream, int slots, UpstreamBackendOptions? options = null)
        => new(
            new SlotFactory(upstream, slots),
            Options.Create(new BackendOptions { Type = BackendOptions.Colibri }),
            Options.Create(options ?? new UpstreamBackendOptions { BaseUrl = "http://127.0.0.1:8000/v1" }),
            NullLogger<UpstreamBackend>.Instance);

    private static ColibriWatcher Watcher(Stub upstream)
        => new(
            new ProbeFactory(new HttpClient(upstream, disposeHandler: false)),
            Options.Create(new ColibriOptions()),
            Options.Create(new UpstreamBackendOptions { BaseUrl = "http://127.0.0.1:8000/v1" }),
            TimeProvider.System,
            NullLogger<ColibriWatcher>.Instance);

    private static IHost BuildNode(params (string Key, string? Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Coordinator:Url"] = "http://localhost:5080/",
            ["Coordinator:EnrollmentSecret"] = "test-secret",
            ["Ollama:Endpoint"] = "http://localhost:11434/",
            ["Node:Name"] = "test-node",
        };

        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddInferHubNode();

        return builder.Build();
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>The composition root's colibri client, minus DI: the slot handler over the stub.</summary>
    private sealed class SlotFactory(Stub upstream, int slots) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(UpstreamBackend.ColibriHttpClientName, name);
            return new HttpClient(new ColibriRequestHandler(slots) { InnerHandler = upstream }, disposeHandler: false);
        }
    }

    private sealed class ProbeFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Stub(string body, string contentType = "application/json") : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string? Path { get; private set; }

        public JsonObject? Body { get; private set; }

        /// <summary>What the request declared before anyone read it — null is a chunked body.</summary>
        public long? ContentLength { get; private set; }

        public long SentBytes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;

            if (request.Content is not null)
            {
                ContentLength = request.Content.Headers.ContentLength;
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                SentBytes = bytes.Length;
                Body = JsonNode.Parse(bytes) as JsonObject;
            }

            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            };
        }
    }
}
