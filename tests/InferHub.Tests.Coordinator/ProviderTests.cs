using System.Net;
using System.Text;
using System.Text.Json;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Named cloud providers (phase 61). <see cref="FallbackTests"/> is the suite about when an upstream
/// must not fire and it still is; this one is about there being <em>more than one</em> of them —
/// which vendor a model reaches, that the answer is never decided by ordering, and that a deployment
/// which never wrote a <c>Providers:</c> block cannot tell any of it happened.
/// </summary>
public class ProviderTests
{
    private const string ChatJob = """
    {"model":"big-code","messages":[{"role":"user","content":"hi"}],"stream":false}
    """;

    private const string UpstreamAnswer = """
    {
      "id": "chatcmpl-1", "created": 0, "model": "whatever",
      "choices": [{"index":0,"message":{"role":"assistant","content":"Hello."},"finish_reason":"stop"}],
      "usage": {"prompt_tokens": 3, "completion_tokens": 2, "total_tokens": 5}
    }
    """;

    // ---- two providers, two vendors ----------------------------------------------------

    [Fact]
    public async Task EachModelReachesItsOwnProviderWithItsOwnCredential()
    {
        var upstream = new RecordingUpstream(UpstreamAnswer);
        var providers = Registry(
            ("openai", Provider("https://api.openai.com/v1", "key-openai", ("llama3", "gpt-4o-mini"))),
            ("openrouter", Provider("https://openrouter.ai/api/v1", "key-openrouter", ("big-code", "qwen/qwen3-coder"))));

        var dispatcher = Dispatcher(providers, upstream);

        var code = await dispatcher.DispatchAsync("chat", ChatJob, "big-code", stream: false, CancellationToken.None);
        var chat = await dispatcher.DispatchAsync("chat", ChatJob, "llama3", stream: false, CancellationToken.None);

        Assert.Equal("provider:openrouter", code.ServedBy);
        Assert.Equal("provider:openai", chat.ServedBy);

        var first = upstream.Requests[0];
        var second = upstream.Requests[1];

        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", first.Url);
        Assert.Equal("Bearer key-openrouter", first.Authorization);
        Assert.Equal("qwen/qwen3-coder", first.Model);

        Assert.Equal("https://api.openai.com/v1/chat/completions", second.Url);
        Assert.Equal("Bearer key-openai", second.Authorization);
        Assert.Equal("gpt-4o-mini", second.Model);
    }

    [Fact]
    public async Task TheCallerIsAnsweredInTheModelTheyAskedFor()
    {
        // They never named qwen/qwen3-coder, and a response that did would break every client
        // that round-trips the model name back into the next turn.
        var dispatcher = Dispatcher(
            Registry(("openrouter", Provider("https://openrouter.ai/api/v1", "k", ("big-code", "qwen/qwen3-coder")))),
            new RecordingUpstream(UpstreamAnswer));

        var result = await dispatcher.DispatchAsync("chat", ChatJob, "big-code", stream: false, CancellationToken.None);

        Assert.Equal("big-code", JsonDocument.Parse(result.ResponseJson!).RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void AnUnmappedModelReachesNobodyAndADisabledProviderMapsNothing()
    {
        var parked = Provider("https://openrouter.ai/api/v1", "k", ("big-code", "qwen/qwen3-coder"));
        parked.Enabled = false;

        var dispatcher = Dispatcher(Registry(("openrouter", parked)), new RecordingUpstream(UpstreamAnswer));

        Assert.False(dispatcher.ShouldServe("big-code", hasCapableNode: false));
        Assert.False(dispatcher.ShouldServe("mistral", hasCapableNode: false));
    }

    [Fact]
    public void TheTriggerIsPerProviderRatherThanPerHub()
    {
        // A busy fleet: one node at its declared cap, holding both models.
        var registry = new NodeRegistry();
        registry.Upsert("conn-a", Registration("node-a", maxConcurrency: 1), DateTimeOffset.UtcNow);
        registry.ReportModels(
            "conn-a",
            new Shared.Contracts.NodeModels(
                "node-a",
                [new Shared.Contracts.ModelInfo("llama3", null, null), new Shared.Contracts.ModelInfo("big-code", null, null)],
                DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow);
        registry.IncrementInFlight("conn-a");

        var cheap = Provider("https://openrouter.ai/api/v1", "k", ("big-code", "qwen/qwen3-coder"));
        cheap.Trigger = FallbackOptions.TriggerNoNodeOrSaturated;

        var dispatcher = Dispatcher(
            Registry(("openai", Provider("https://api.openai.com/v1", "k", ("llama3", "gpt-4o-mini"))), ("openrouter", cheap)),
            new RecordingUpstream(UpstreamAnswer),
            registry);

        // Same fleet, same saturation, two answers — because the operator gave two answers.
        Assert.True(dispatcher.ShouldServe("big-code", hasCapableNode: true));
        Assert.False(dispatcher.ShouldServe("llama3", hasCapableNode: true));
    }

    // ---- what startup refuses ----------------------------------------------------------

    [Fact]
    public void AModelMappedByTwoProvidersFailsStartupAndNamesBoth()
    {
        var result = Validate(
            Configured(
                ("openai", Provider("https://api.openai.com/v1", "k", ("llama3", "gpt-4o-mini"))),
                ("openrouter", Provider("https://openrouter.ai/api/v1", "k", ("llama3", "meta-llama/llama-3-8b")))));

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("llama3", failure);
        Assert.Contains("Providers:openai", failure);
        Assert.Contains("Providers:openrouter", failure);
    }

    [Fact]
    public void AProviderThatClaimsAModelTheLegacySectionAlreadyMapsFailsStartup()
    {
        // The upgrade case: the Fallback: block is already there and somebody adds a provider over
        // one of its models. Caught on the upgrade, not the first time that model is asked for.
        var legacy = new FallbackOptions
        {
            Enabled = true,
            BaseUrl = "https://api.openai.com/v1",
            ModelMap = { ["llama3"] = "gpt-4o-mini" }
        };

        var result = Validate(
            Configured(("openrouter", Provider("https://openrouter.ai/api/v1", "k", ("llama3", "meta-llama/llama-3-8b")))),
            legacy);

        Assert.True(result.Failed);
        Assert.Contains("the Fallback: section", Assert.Single(result.Failures));
    }

    [Fact]
    public void ADisabledProviderCannotCollideWithAnything()
    {
        var parked = Provider("https://openrouter.ai/api/v1", "k", ("llama3", "meta-llama/llama-3-8b"));
        parked.Enabled = false;

        var result = Validate(
            Configured(("openai", Provider("https://api.openai.com/v1", "k", ("llama3", "gpt-4o-mini"))), ("openrouter", parked)));

        Assert.False(result.Failed);
    }

    [Fact]
    public void AnUnknownTypeFailsStartupNamingTheTypesThatExist()
    {
        var typo = Provider("https://api.anthropic.com", "k", ("claude", "claude-sonnet-4"));
        typo.Type = "anthropic";

        var result = Validate(Configured(("claude", typo)));

        Assert.True(result.Failed);
        Assert.Contains(ProviderDefinition.TypeOpenAiCompatible, Assert.Single(result.Failures));
    }

    [Fact]
    public void AnEnabledProviderWithoutAnAbsoluteBaseUrlFailsStartup()
    {
        var incomplete = Provider(baseUrl: null, "k", ("big-code", "qwen/qwen3-coder"));

        Assert.True(Validate(Configured(("openrouter", incomplete))).Failed);
    }

    [Fact]
    public void AProviderIdThatCannotBeALabelFailsStartup()
    {
        // It travels in a response header and in a Prometheus label; both have opinions.
        var result = Validate(Configured(("Open Router", Provider("https://openrouter.ai/api/v1", "k", ("big-code", "q")))));

        Assert.True(result.Failed);
    }

    [Fact]
    public void ANoProvidersDeploymentValidatesAndResolvesNothing()
    {
        Assert.False(Validate(new ProviderOptions()).Failed);
        Assert.Null(Registry().Resolve("llama3"));
    }

    // ---- the legacy section, unchanged --------------------------------------------------

    [Fact]
    public async Task TheProjectedLegacyProviderStillAnswersWithTheOldHeaderValue()
    {
        var legacy = new FallbackOptions
        {
            Enabled = true,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "key-legacy",
            ModelMap = { ["big-code"] = "gpt-4o-mini" }
        };

        var dispatcher = Dispatcher(Registry(legacy: legacy), new RecordingUpstream(UpstreamAnswer));
        var result = await dispatcher.DispatchAsync("chat", ChatJob, "big-code", stream: false, CancellationToken.None);

        Assert.Equal(InferenceCore.ServedByFallback, result.ServedBy);
        Assert.Equal("fallback", result.ServedBy);
    }

    [Fact]
    public void ANamedProviderIsPreferredOverTheLegacySectionForItsOwnModels()
    {
        // They cannot both map one model — the validator refuses that — so this is only about the
        // models each one does claim, and about the legacy section still working beside them.
        var legacy = new FallbackOptions
        {
            Enabled = true,
            BaseUrl = "https://api.openai.com/v1",
            ModelMap = { ["llama3"] = "gpt-4o-mini" }
        };

        var registry = Registry(
            legacy,
            ("openrouter", Provider("https://openrouter.ai/api/v1", "k", ("big-code", "qwen/qwen3-coder"))));

        Assert.Equal("provider:openrouter", registry.Resolve("big-code")!.ServedBy);
        Assert.Equal("fallback", registry.Resolve("llama3")!.ServedBy);
        Assert.Null(registry.Resolve("mistral"));
    }

    // ---- what the operator can see -----------------------------------------------------

    [Fact]
    public void StatusOmitsTheProvidersKeyEntirelyWhenNoneIsConfigured()
    {
        // A v3.28 payload has no `providers` field. A deployment that changed nothing keeps it.
        Assert.Null(StatusEndpoint.BuildProviderBlocks(Registry(), new Metrics().Snapshot(DateTimeOffset.UtcNow)));
        Assert.Null(StatusEndpoint.BuildProviderBlocks(null, new Metrics().Snapshot(DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void StatusReportsAConfiguredProviderBeforeItHasServedAnything()
    {
        var keyless = Provider(baseUrl: "http://vllm.internal/v1", apiKey: null, ("local-big", "Qwen/Qwen3-32B"));

        var blocks = StatusEndpoint.BuildProviderBlocks(
            Registry(("openrouter", Provider("https://openrouter.ai/api/v1", "key", ("big-code", "qwen/qwen3-coder"))), ("vllm", keyless)),
            new Metrics().Snapshot(DateTimeOffset.UtcNow));

        Assert.NotNull(blocks);
        Assert.Equal(2, blocks!.Count);

        var openrouter = blocks.Single(block => block.Id == "openrouter");
        Assert.Equal("configured", openrouter.Credential);
        Assert.Equal("big-code", Assert.Single(openrouter.MappedModels));
        Assert.Equal(0, openrouter.Dispatched);
        Assert.Null(openrouter.LastAtUtc);

        Assert.Equal("absent", blocks.Single(block => block.Id == "vllm").Credential);
    }

    [Fact]
    public void NoPartOfAnApiKeyReachesTheStatusPayload()
    {
        var blocks = StatusEndpoint.BuildProviderBlocks(
            Registry(("openrouter", Provider("https://openrouter.ai/api/v1", "sk-or-v1-secret-value", ("big-code", "q")))),
            new Metrics().Snapshot(DateTimeOffset.UtcNow));

        var rendered = JsonSerializer.Serialize(blocks);

        Assert.DoesNotContain("secret-value", rendered);
        Assert.DoesNotContain("sk-or", rendered);
    }

    [Fact]
    public async Task ADispatchCountsAgainstItsProviderAndAgainstTheUnchangedTotal()
    {
        var metrics = new Metrics();
        var dispatcher = Dispatcher(
            Registry(("openrouter", Provider("https://openrouter.ai/api/v1", "k", ("big-code", "qwen/qwen3-coder")))),
            new RecordingUpstream(UpstreamAnswer),
            metrics: metrics);

        await dispatcher.DispatchAsync("chat", ChatJob, "big-code", stream: false, CancellationToken.None);

        var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);

        // The old series still means what it always meant: requests the fleet did not serve.
        Assert.Equal(1, snapshot.FallbackDispatched);
        Assert.Equal("big-code", snapshot.LastFallbackModel);

        var provider = Assert.Single(snapshot.PerProvider!);
        Assert.Equal("openrouter", provider.Provider);
        Assert.Equal(1, provider.Dispatched);
        Assert.Equal("big-code", provider.LastModel);
        Assert.NotNull(provider.LastAtUtc);
    }

    [Fact]
    public void AProviderThatHasServedNothingHasNoMetricSeries()
    {
        // Phase-28 D5, for the provider table: configuring a vendor is not traffic.
        var scrape = new Metrics().Snapshot(DateTimeOffset.UtcNow);

        Assert.Empty(scrape.PerProvider!);
    }

    // ---- harness -----------------------------------------------------------------------

    private static ProviderDefinition Provider(string? baseUrl, string? apiKey, params (string Local, string Upstream)[] map)
    {
        var definition = new ProviderDefinition { BaseUrl = baseUrl, ApiKey = apiKey };

        foreach (var (local, upstream) in map)
        {
            definition.ModelMap[local] = upstream;
        }

        return definition;
    }

    private static ProviderOptions Configured(params (string Id, ProviderDefinition Definition)[] providers)
    {
        var options = new ProviderOptions();

        foreach (var (id, definition) in providers)
        {
            options.Entries[id] = definition;
        }

        return options;
    }

    private static ProviderRegistry Registry(params (string Id, ProviderDefinition Definition)[] providers)
        => new(Options.Create(Configured(providers)), Options.Create(new FallbackOptions()));

    private static ProviderRegistry Registry(
        FallbackOptions legacy,
        params (string Id, ProviderDefinition Definition)[] providers)
        => new(Options.Create(Configured(providers)), Options.Create(legacy));

    private static ValidateOptionsResult Validate(ProviderOptions options, FallbackOptions? legacy = null)
        => new ProviderOptionsValidator(Options.Create(legacy ?? new FallbackOptions()))
            .Validate(null, options);

    private static ProviderDispatcher Dispatcher(
        ProviderRegistry providers,
        RecordingUpstream upstream,
        INodeRegistry? registry = null,
        Metrics? metrics = null)
        => new(
            new StubFactory(upstream),
            registry ?? new NodeRegistry(),
            providers,
            metrics ?? new Metrics(),
            NullLogger<ProviderDispatcher>.Instance);

    private static Shared.Contracts.NodeRegistration Registration(string nodeId, int? maxConcurrency)
        => new(nodeId, nodeId, "http://localhost:11434/", "3.29.0", null, maxConcurrency);

    private sealed class StubFactory(RecordingUpstream upstream) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(upstream, disposeHandler: false);
    }

    /// <summary>
    /// Records where each request went and what it carried, because with more than one provider the
    /// interesting failure is no longer "did it call an upstream" but "did it call <em>that</em> one
    /// with <em>that</em> key".
    /// </summary>
    private sealed class RecordingUpstream(string response) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new Sent(
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                JsonDocument.Parse(body).RootElement.TryGetProperty("model", out var model)
                    ? model.GetString()
                    : null));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(response)))
                {
                    Headers = { { "Content-Type", "application/json" } }
                }
            };
        }

        internal sealed record Sent(string Url, string? Authorization, string? Model);
    }
}
