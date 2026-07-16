using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Cloud burst. The feature that quietly sends a user's prompt to a third party if we get it
/// wrong, so most of these tests are about when it must <em>not</em> fire.
/// </summary>
public class FallbackTests
{
    private const string ChatJob = """
    {"model":"llama3","messages":[{"role":"user","content":"hi"}],"stream":false}
    """;

    // ---- when it must not fire ---------------------------------------------------------

    [Fact]
    public void DisabledByDefault()
    {
        // The default-constructed options are what an existing deployment gets on upgrade.
        Assert.False(new FallbackOptions().Enabled);

        var fallback = Dispatcher(new FallbackOptions
        {
            BaseUrl = "http://upstream/v1",
            ModelMap = { ["llama3"] = "gpt-4o-mini" }
        });

        Assert.False(fallback.ShouldServe("llama3", hasCapableNode: false));
    }

    [Fact]
    public void DoesNotFireForAnUnmappedModel()
    {
        // The map is the consent. A model nobody mapped never leaves the fleet.
        var fallback = Dispatcher(Enabled(map: ("llama3", "gpt-4o-mini")));

        Assert.False(fallback.ShouldServe("mistral", hasCapableNode: false));
        Assert.True(fallback.ShouldServe("llama3", hasCapableNode: false));
    }

    [Fact]
    public void DoesNotFireForAModelOutsideTheAllowlist()
    {
        var options = Enabled(map: ("llama3", "gpt-4o-mini"));
        options.ModelMap["mistral"] = "gpt-4o";
        options.AllowedModels = ["llama3"];

        var fallback = Dispatcher(options);

        Assert.True(fallback.ShouldServe("llama3", hasCapableNode: false));
        Assert.False(fallback.ShouldServe("mistral", hasCapableNode: false));
    }

    [Fact]
    public void DoesNotFireWhenBaseUrlIsMissing()
    {
        var options = Enabled(map: ("llama3", "gpt-4o-mini"));
        options.BaseUrl = null;

        Assert.False(Dispatcher(options).ShouldServe("llama3", hasCapableNode: false));
    }

    [Fact]
    public void DoesNotFireWhenANodeHoldsTheModel()
    {
        // The default trigger is no-node. A live node always wins — the point is degradation,
        // not diversion.
        var fallback = Dispatcher(Enabled(map: ("llama3", "gpt-4o-mini")));

        Assert.False(fallback.ShouldServe("llama3", hasCapableNode: true));
    }

    // ---- saturation trigger ------------------------------------------------------------

    [Fact]
    public void SaturationTriggerFiresOnlyWhenEveryCapableNodeIsAtItsCap()
    {
        var registry = new NodeRegistry();
        registry.Upsert("conn-a", Registration("node-a", maxConcurrency: 1), DateTimeOffset.UtcNow);
        registry.ReportModels(
            "conn-a",
            new NodeModels("node-a", [new ModelInfo("llama3", null, null)], DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow);

        var options = Enabled(map: ("llama3", "gpt-4o-mini"));
        options.Trigger = FallbackOptions.TriggerNoNodeOrSaturated;

        var fallback = Dispatcher(options, registry);

        Assert.False(fallback.ShouldServe("llama3", hasCapableNode: true));

        registry.IncrementInFlight("conn-a");

        Assert.True(fallback.ShouldServe("llama3", hasCapableNode: true));
    }

    [Fact]
    public void ANodeWithNoDeclaredCapIsNeverSaturated()
    {
        // We have no number to compare against, and inventing one would burst to a paid
        // upstream on a guess.
        var registry = new NodeRegistry();
        registry.Upsert("conn-a", Registration("node-a", maxConcurrency: null), DateTimeOffset.UtcNow);
        registry.ReportModels(
            "conn-a",
            new NodeModels("node-a", [new ModelInfo("llama3", null, null)], DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow);

        for (var i = 0; i < 50; i++)
        {
            registry.IncrementInFlight("conn-a");
        }

        var options = Enabled(map: ("llama3", "gpt-4o-mini"));
        options.Trigger = FallbackOptions.TriggerNoNodeOrSaturated;

        Assert.False(Dispatcher(options, registry).ShouldServe("llama3", hasCapableNode: true));
    }

    [Fact]
    public void TheSaturationTriggerIsOptIn()
    {
        // Same fleet, default trigger: a busy node is still a node.
        var registry = new NodeRegistry();
        registry.Upsert("conn-a", Registration("node-a", maxConcurrency: 1), DateTimeOffset.UtcNow);
        registry.ReportModels(
            "conn-a",
            new NodeModels("node-a", [new ModelInfo("llama3", null, null)], DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow);
        registry.IncrementInFlight("conn-a");

        var fallback = Dispatcher(Enabled(map: ("llama3", "gpt-4o-mini")), registry);

        Assert.False(fallback.ShouldServe("llama3", hasCapableNode: true));
    }

    // ---- dispatching -------------------------------------------------------------------

    [Fact]
    public async Task BlockingBurstMapsTheModelBothWaysAndCountsItself()
    {
        var upstream = StubUpstream.Json("""
        {
          "id": "chatcmpl-1", "created": 0, "model": "gpt-4o-mini",
          "choices": [{"index":0,"message":{"role":"assistant","content":"Hello."},"finish_reason":"stop"}],
          "usage": {"prompt_tokens": 3, "completion_tokens": 2, "total_tokens": 5}
        }
        """);

        var metrics = new Metrics();
        var fallback = Dispatcher(Enabled(map: ("llama3", "gpt-4o-mini")), upstream: upstream, metrics: metrics);

        var result = await fallback.DispatchAsync("chat", ChatJob, "llama3", stream: false, CancellationToken.None);

        // Upstream is asked for *its* name for the model...
        Assert.Equal("gpt-4o-mini", upstream.LastRequestBody().GetProperty("model").GetString());

        // ...and the caller gets back the name they asked for. They never named gpt-4o-mini.
        var ollama = JsonDocument.Parse(result.ResponseJson!).RootElement;
        Assert.Equal("llama3", ollama.GetProperty("model").GetString());
        Assert.Equal("Hello.", ollama.GetProperty("message").GetProperty("content").GetString());
        Assert.True(ollama.GetProperty("done").GetBoolean());

        var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.Equal(1, snapshot.FallbackDispatched);
        Assert.Equal("llama3", snapshot.LastFallbackModel);
        Assert.NotNull(snapshot.LastFallbackAtUtc);
    }

    [Fact]
    public async Task StreamingBurstArrivesAsOllamaChunksOnTheUsualChannel()
    {
        var upstream = StubUpstream.Sse(
            """data: {"id":"c","created":0,"model":"gpt-4o-mini","choices":[{"index":0,"delta":{"content":"Hel"},"finish_reason":null}]}""",
            """data: {"id":"c","created":0,"model":"gpt-4o-mini","choices":[{"index":0,"delta":{"content":"lo"},"finish_reason":null}]}""",
            """data: {"id":"c","created":0,"model":"gpt-4o-mini","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            "data: [DONE]");

        var fallback = Dispatcher(Enabled(map: ("llama3", "gpt-4o-mini")), upstream: upstream);

        var result = await fallback.DispatchAsync("chat", ChatJob, "llama3", stream: true, CancellationToken.None);

        var chunks = await Drain(result.Stream!);

        Assert.Equal(3, chunks.Count);
        Assert.Equal("Hel", Body(chunks[0]).GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("lo", Body(chunks[1]).GetProperty("message").GetProperty("content").GetString());
        Assert.True(chunks[^1].Done);

        // The model name is rewritten on the way out on every chunk, not just the first.
        Assert.All(chunks, chunk => Assert.Equal("llama3", Body(chunk).GetProperty("model").GetString()));
    }

    [Fact]
    public async Task AStreamThatFailsUpstreamEndsWithAnErrorChunkRatherThanHanging()
    {
        var upstream = StubUpstream.Status(HttpStatusCode.InternalServerError, "engine crashed");
        var fallback = Dispatcher(Enabled(map: ("llama3", "gpt-4o-mini")), upstream: upstream);

        var result = await fallback.DispatchAsync("chat", ChatJob, "llama3", stream: true, CancellationToken.None);

        var chunk = Assert.Single(await Drain(result.Stream!));

        Assert.True(chunk.Done);
        Assert.Contains("engine crashed", Body(chunk).GetProperty("error").GetString());
    }

    // ---- the routing decision ----------------------------------------------------------

    [Fact]
    public async Task WithFallbackOffAMissingModelIsStillAFlat404()
    {
        var outcome = await Route(model: "llama3", node: null, fallback: new NeverFallback());

        Assert.True(outcome.IsError);
        Assert.Equal(StatusCodes.Status404NotFound, outcome.ErrorStatus);
        Assert.Equal("model 'llama3' not found", outcome.ErrorMessage);
    }

    [Fact]
    public async Task WithFallbackOnAMissingModelIsServedAndTagged()
    {
        var fallback = new RecordingFallback(serves: true);

        var outcome = await Route(model: "llama3", node: null, fallback);

        Assert.False(outcome.IsError);
        Assert.Equal(InferenceCore.ServedByFallback, outcome.ServedBy);
        Assert.Equal("llama3", fallback.LastModel);
    }

    [Fact]
    public async Task ANodeServedRequestIsTaggedAsANodeRequest()
    {
        var node = new RoutableNode("conn-a", "node-a", "alpha");

        var outcome = await Route(model: "llama3", node, fallback: new NeverFallback());

        Assert.False(outcome.IsError);
        Assert.Equal(InferenceCore.ServedByNode, outcome.ServedBy);
    }

    [Fact]
    public async Task AFallbackThatFailsWithNoNodeBehindItSurfacesA502NotASilent404()
    {
        var outcome = await Route(model: "llama3", node: null, new ThrowingFallback());

        Assert.True(outcome.IsError);
        Assert.Equal(StatusCodes.Status502BadGateway, outcome.ErrorStatus);
        Assert.Contains("fallback upstream failed", outcome.ErrorMessage!);
    }

    [Fact]
    public async Task AFailedSaturationBurstFallsBackToTheNode()
    {
        // Bursting on saturation is an optimisation, not a promise. If the upstream is down and
        // we do have a node, use the node.
        var node = new RoutableNode("conn-a", "node-a", "alpha");

        var outcome = await Route(model: "llama3", node, new ThrowingFallback());

        Assert.False(outcome.IsError);
        Assert.Equal(InferenceCore.ServedByNode, outcome.ServedBy);
    }

    // ---- status ------------------------------------------------------------------------

    [Fact]
    public void StatusReportsFallbackEvenWhenItIsOff()
    {
        // "Is this thing sending my prompts anywhere?" must be answerable from /api/status,
        // not only by going and reading the config file.
        var block = StatusEndpoint.BuildFallbackBlock(
            new FallbackOptions(),
            new Metrics().Snapshot(DateTimeOffset.UtcNow));

        Assert.False(block.Enabled);
        Assert.Equal(0, block.Dispatched);
        Assert.Empty(block.MappedModels);
        Assert.Null(block.LastModel);
    }

    [Fact]
    public void StatusListsTheMappedModels()
    {
        var block = StatusEndpoint.BuildFallbackBlock(
            Enabled(map: ("llama3", "gpt-4o-mini")),
            new Metrics().Snapshot(DateTimeOffset.UtcNow));

        Assert.True(block.Enabled);
        Assert.Equal(FallbackOptions.TriggerNoNode, block.Trigger);
        Assert.Equal("llama3", Assert.Single(block.MappedModels));
    }

    // ---- harness -----------------------------------------------------------------------

    private static FallbackOptions Enabled(params (string Local, string Upstream)[] map)
    {
        var options = new FallbackOptions
        {
            Enabled = true,
            BaseUrl = "http://upstream/v1"
        };

        foreach (var (local, upstream) in map)
        {
            options.ModelMap[local] = upstream;
        }

        return options;
    }

    private static FallbackDispatcher Dispatcher(
        FallbackOptions options,
        INodeRegistry? registry = null,
        StubUpstream? upstream = null,
        Metrics? metrics = null)
        => new(
            new StubHttpClientFactory(upstream ?? StubUpstream.Json("{}")),
            registry ?? new NodeRegistry(),
            Options.Create(options),
            metrics ?? new Metrics(),
            NullLogger<FallbackDispatcher>.Instance);

    private static Task<InferenceCore.DispatchOutcome> Route(
        string model,
        RoutableNode? node,
        IFallbackDispatcher fallback)
        => InferenceCore.DispatchAsync(
            "chat",
            ChatJob,
            model,
            stream: false,
            conversationKey: null,
            TestUsage.Context(new NodeRegistry()),
            new StubRouter(node),
            new StubDispatcher(),
            fallback,
            new Metrics(),
            NullLogger.Instance,
            CancellationToken.None);

    private static NodeRegistration Registration(string nodeId, int? maxConcurrency)
        => new(nodeId, nodeId, "http://localhost:11434/", "2.4.0", null, maxConcurrency);

    private static JsonElement Body(InferenceChunk chunk)
        => JsonDocument.Parse(chunk.ResponseJson).RootElement.Clone();

    private static async Task<List<InferenceChunk>> Drain(ChannelReader<InferenceChunk> reader)
    {
        var chunks = new List<InferenceChunk>();

        await foreach (var chunk in reader.ReadAllAsync())
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private sealed class StubRouter(RoutableNode? node) : InferHub.Coordinator.Services.IRouter
    {
        public RoutableNode? Route(string model, string? conversationKey = null, string? excludeConnectionId = null)
            => node;
    }

    private sealed class StubDispatcher : IDispatcher
    {
        public Task<InferenceResult> DispatchAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
            => Task.FromResult(InferenceResult.Succeeded(job.JobId, """{"model":"llama3","done":true}"""));

        public Task<ChannelReader<InferenceChunk>> DispatchStreamAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        // The node-callback half of the interface; nothing here dispatches to a real node.
        public bool Complete(InferenceResult result) => throw new NotImplementedException();

        public bool WriteChunk(InferenceChunk chunk) => throw new NotImplementedException();

        public void FailForConnection(string connectionId, Exception? error = null) => throw new NotImplementedException();
    }

    private sealed class NeverFallback : IFallbackDispatcher
    {
        public bool ShouldServe(string model, bool hasCapableNode) => false;

        public Task<FallbackResult> DispatchAsync(string kind, string rawJson, string model, bool stream, CancellationToken cancellationToken)
            => throw new InvalidOperationException("fallback must not be dispatched when it is off");
    }

    private sealed class RecordingFallback(bool serves) : IFallbackDispatcher
    {
        public string? LastModel { get; private set; }

        public bool ShouldServe(string model, bool hasCapableNode) => serves;

        public Task<FallbackResult> DispatchAsync(string kind, string rawJson, string model, bool stream, CancellationToken cancellationToken)
        {
            LastModel = model;
            return Task.FromResult(new FallbackResult(null, """{"model":"llama3","done":true}"""));
        }
    }

    private sealed class ThrowingFallback : IFallbackDispatcher
    {
        public bool ShouldServe(string model, bool hasCapableNode) => true;

        public Task<FallbackResult> DispatchAsync(string kind, string rawJson, string model, bool stream, CancellationToken cancellationToken)
            => throw new HttpRequestException("upstream unreachable");
    }

    private sealed class StubHttpClientFactory(StubUpstream upstream) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(upstream, disposeHandler: false);
    }

    private sealed class StubUpstream : HttpMessageHandler
    {
        private HttpStatusCode status = HttpStatusCode.OK;
        private string body = "{}";
        private string contentType = "application/json";
        private string? requestBody;

        public static StubUpstream Json(string response) => new() { body = response };

        public static StubUpstream Sse(params string[] lines)
            => new()
            {
                body = string.Join("\n", lines) + "\n",
                contentType = "text/event-stream"
            };

        public static StubUpstream Status(HttpStatusCode status, string response)
            => new() { status = status, body = response };

        public JsonElement LastRequestBody()
            => JsonDocument.Parse(requestBody ?? "{}").RootElement.Clone();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(status)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)))
                {
                    Headers = { { "Content-Type", contentType } }
                }
            };
        }
    }
}
