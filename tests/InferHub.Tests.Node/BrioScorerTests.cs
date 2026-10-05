using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Colibri;
using InferHub.Node.Capabilities;
using InferHub.Node.Configuration;
using InferHub.Node.Tools;
using InferHub.Shared.Brio;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 94: a <c>score</c> job on a colibri node goes to the engine's <c>/v1/brio</c>, and the
/// engine's answer comes back stated as a <see cref="ToolResult"/>. The stub answers what colibri
/// v1.12.1's gateway answers; the real engine is in the release notes.
/// </summary>
public class BrioScorerTests
{
    private const string Answer = """
    {"object":"brio.choice","answer":"b","entropy":0.2,"normalize":"mean",
     "choices":[{"option":"b","p":0.9},{"option":"a","p":0.1}],"id":"brio-1","created":0,"model":"olmoe",
     "usage":{"prompt_tokens":12,"completion_tokens":0,"read_tokens":4,"total_tokens":16}}
    """;

    private const string Request = """{"model":"olmoe","state":"A document.","question":"Which?","options":["a","b"]}""";

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task AScoreGoesToTheBrioRouteAsTheCallerWroteIt(int slots)
    {
        var upstream = new Stub(Answer);

        var result = await Colibri(upstream, slots).ScoreAsync(Job(Request), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(Answer, result.Payload);
        Assert.Equal("/v1/brio", upstream.Path);

        // D4: no cache_slot — the engine pins the slot from `state` itself, and a second hash here
        // could only disagree with it. 93 D6's Content-Length still holds.
        Assert.False(upstream.Body!.ContainsKey("cache_slot"));
        Assert.Equal(JsonNode.Parse(Request)!.ToJsonString(), upstream.Body.ToJsonString());
        Assert.Equal(upstream.SentBytes, upstream.ContentLength);
    }

    [Fact]
    public async Task TheEnginesFullQueueIsARetryNotARetriedRequest()
    {
        var upstream = new Stub("""{"error":{"message":"Server busy.","type":"rate_limit_error"}}""")
        {
            Status = HttpStatusCode.TooManyRequests,
            RetryAfter = TimeSpan.FromSeconds(3)
        };

        var result = await Colibri(upstream).ScoreAsync(Job(Request), CancellationToken.None);

        Assert.Equal(3, result.RetryAfterSeconds);
        Assert.Equal("Server busy.", result.Error);
        Assert.Equal(1, upstream.Calls);
    }

    [Fact]
    public async Task TheEnginesRefusalsKeepTheirOwnSentence()
    {
        var model = await Colibri(new Stub(
                """{"error":{"message":"The model `gpt-4` does not exist.","type":"invalid_request_error","param":"model","code":"model_not_found"}}""")
            { Status = HttpStatusCode.NotFound }).ScoreAsync(Job(Request), CancellationToken.None);

        Assert.Equal(BrioErrorCodes.ModelNotFound, model.ErrorCode);
        Assert.Equal("The model `gpt-4` does not exist.", model.Error);

        var invalid = await Colibri(new Stub(
                """{"error":{"message":"`options` needs at least two options to choose between.","type":"invalid_request_error","param":"options"}}""")
            { Status = HttpStatusCode.BadRequest }).ScoreAsync(Job(Request), CancellationToken.None);

        Assert.Equal(ToolErrorCodes.InvalidRequest, invalid.ErrorCode);
        Assert.Contains("at least two options", invalid.Error);

        var broken = await Colibri(new Stub(
                """{"error":{"message":"The colibri engine failed to process the request.","type":"server_error","code":"engine_error"}}""")
            { Status = HttpStatusCode.InternalServerError }).ScoreAsync(Job(Request), CancellationToken.None);

        Assert.False(broken.Success);
        Assert.Null(broken.ErrorCode);
        Assert.Null(broken.RetryAfterSeconds);
        Assert.Contains("failed to process", broken.Error);
    }

    [Fact]
    public async Task AnUnreachableEngineIsAFailedJobNamingWhere()
    {
        var result = await Colibri(new Stub(Answer) { Throw = true }).ScoreAsync(Job(Request), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("unreachable", result.Error);
        Assert.Contains("127.0.0.1:8000", result.Error);
    }

    [Fact]
    public async Task OnlyColibriScores()
    {
        var upstream = new Stub(Answer);
        var backend = new UpstreamBackend(
            new Factory(upstream, slots: 1),
            Options.Create(new BackendOptions { Type = BackendOptions.OpenAi }),
            Options.Create(new UpstreamBackendOptions { BaseUrl = "http://127.0.0.1:8000/v1" }),
            NullLogger<UpstreamBackend>.Instance);

        Assert.DoesNotContain("score", backend.Kinds);
        Assert.False((await backend.ScoreAsync(Job(Request), CancellationToken.None)).Success);
        Assert.Null(upstream.Path);
    }

    // ---- the executor -------------------------------------------------------------------

    [Fact]
    public async Task AScoreJobGoesToTheScorerAndNeverToAToolWorker()
    {
        var upstream = new Stub(Answer);
        var executor = Executor(Colibri(upstream));

        Assert.True(executor.Provides("score", "olmoe"));

        // NoToolRuntime throws on any acquire: reaching it would be a failed job, not this answer.
        var result = await executor.RunAsync(Job(Request), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("/v1/brio", upstream.Path);
    }

    [Fact]
    public async Task ANodeWithNoScorerDoesNotProvideScore()
    {
        var executor = Executor(scorer: null);

        Assert.False(executor.Provides("score", "olmoe"));
        Assert.False((await executor.RunAsync(Job(Request), CancellationToken.None)).Success);
    }

    [Fact]
    public async Task AScoreIsAnsweredOnceAndAStreamIsRefusedInItsTerminalFrame()
    {
        var upstream = new Stub(Answer);
        var chunks = new List<ToolChunk>();

        await foreach (var chunk in Executor(Colibri(upstream)).StreamAsync(Job(Request), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        var only = Assert.Single(chunks);
        Assert.True(only.Done);
        Assert.Contains("without stream", only.Payload);
        Assert.Null(upstream.Path);
    }

    // ---- the composition ----------------------------------------------------------------

    [Fact]
    public void AColibriNodeHasAScorerAndAnyOtherNodeHasNone()
    {
        using (var colibri = BuildNode(("Backend:Type", "colibri"), ("Colibri:Watch", "false")))
        {
            Assert.IsType<UpstreamBackend>(colibri.Services.GetRequiredService<IClosedSetScorer>());
            Assert.True(colibri.Services.GetRequiredService<ToolExecutor>().Provides("score", "olmoe"));
        }

        // A deployment that changes no config: no scorer, and `score` is not provided.
        using var ollama = BuildNode();
        Assert.Null(ollama.Services.GetService<IClosedSetScorer>());
        Assert.False(ollama.Services.GetRequiredService<ToolExecutor>().Provides("score", "olmoe"));
    }

    [Fact]
    public void ScoreIsDeclaredOverTheEnginesModelAndTheSubtractiveKeySwitchesItOff()
    {
        IReadOnlyList<ModelInfo> models = [new ModelInfo("olmoe", null, null)];
        string[] kinds = [CapabilityKinds.Chat, CapabilityKinds.Score];

        var declared = BackendCapabilities.Declare(models, kinds, new CapabilityOptions());
        Assert.Equal(["olmoe"], declared.Single(c => c.Kind == "score").Models);

        var narrowed = BackendCapabilities.Declare(models, kinds, new CapabilityOptions { Disabled = ["score"] });
        Assert.Equal(["chat"], narrowed.Select(c => c.Kind));
    }

    // ---- harness ------------------------------------------------------------------------

    private static ToolJob Job(string payload) => new(Guid.NewGuid(), CapabilityKinds.Score, "olmoe", payload);

    private static UpstreamBackend Colibri(Stub upstream, int slots = 1)
        => new(
            new Factory(upstream, slots),
            Options.Create(new BackendOptions { Type = BackendOptions.Colibri }),
            Options.Create(new UpstreamBackendOptions { BaseUrl = "http://127.0.0.1:8000/v1" }),
            NullLogger<UpstreamBackend>.Instance);

    private static ToolExecutor Executor(IClosedSetScorer? scorer)
        => new(
            new NoToolRuntime(),
            Options.Create(new ToolOptions()),
            NullLogger<ToolExecutor>.Instance,
            scorer);

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

    /// <summary>The composition root's colibri client: the phase-93 handler over the stub.</summary>
    private sealed class Factory(Stub upstream, int slots) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new ColibriRequestHandler(slots) { InnerHandler = upstream }, disposeHandler: false);
    }

    private sealed class Stub(string body) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public TimeSpan? RetryAfter { get; init; }

        public bool Throw { get; init; }

        public int Calls { get; private set; }

        public string? Path { get; private set; }

        public JsonObject? Body { get; private set; }

        public long? ContentLength { get; private set; }

        public long SentBytes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            if (Throw)
            {
                throw new HttpRequestException("Connection refused");
            }

            Path = request.RequestUri!.AbsolutePath;

            if (request.Content is not null)
            {
                ContentLength = request.Content.Headers.ContentLength;
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                SentBytes = bytes.Length;
                Body = JsonNode.Parse(bytes) as JsonObject;
            }

            var response = new HttpResponseMessage(Status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            if (RetryAfter is { } delay)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
            }

            return response;
        }
    }
}
