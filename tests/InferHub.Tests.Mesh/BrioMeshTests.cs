using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.OpenAi;
using InferHub.Coordinator.Services;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Colibri;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
using InferHub.Node.Tools;
using InferHub.Node.Vector;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 94, end to end: an HTTP client → a real coordinator → a real SignalR wire → a real node
/// whose colibri backend crosses a real socket to a gateway that answers what <c>coli serve</c>
/// v1.12.1 answers — and the same request at a solo node.
/// </summary>
public class BrioMeshTests
{
    private const string KnownState = "The quarterly report for the Plovdiv warehouse says stock fell nine percent.";

    private static string Choice(string state = KnownState) => JsonSerializer.Serialize(new
    {
        model = "olmoe",
        state,
        question = "Is the trend good?",
        options = new[] { "yes", "no", "unclear" }
    });

    [Fact]
    public async Task AClosedQuestionRoundTripsThroughTheMeshAndIsBilledForWhatTheEngineRead()
    {
        await using var colibri = await FakeColibri.StartAsync();
        await using var mesh = await BrioMesh.StartAsync(colibri);

        var response = await mesh.Client.PostAsync("/v1/brio", Json(Choice()));

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal("node", response.Headers.GetValues("X-InferHub-Served-By").Single());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("brio.choice", document.RootElement.GetProperty("object").GetString());
        Assert.Equal("no", document.RootElement.GetProperty("answer").GetString());
        Assert.Equal(3, document.RootElement.GetProperty("choices").GetArrayLength());

        // D2: the engine saw the caller's body, not a translation of it.
        Assert.Equal(JsonNode.Parse(Choice())!.ToJsonString(), colibri.LastBody!.ToJsonString());

        // D3: one `score` row, everything the engine read as prompt tokens, nothing generated.
        var row = await mesh.Ledger.SingleAsync();
        Assert.Equal("score", row.Kind);
        Assert.Equal("olmoe", row.Model);
        Assert.Equal(FakeColibri.TotalTokens, row.PromptTokens);
        Assert.Equal(0, row.CompletionTokens);
    }

    [Fact]
    public async Task TheStateIsContentAndAppearsInNoLogOnEitherHost()
    {
        await using var colibri = await FakeColibri.StartAsync();
        await using var mesh = await BrioMesh.StartAsync(colibri);

        var response = await mesh.Client.PostAsync("/v1/brio", Json(Choice()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains(mesh.Logs.Lines, line => line.Contains("Brio") && line.Contains("options x3"));
        Assert.False(mesh.Logs.Contains("Plovdiv"), "the state reached a log line");
        Assert.False(mesh.Logs.Contains("Is the trend good"), "the question reached a log line");
        Assert.False(mesh.Logs.Contains("unclear"), "an option reached a log line");
    }

    [Fact]
    public async Task TheEnginesRefusalIsA400InItsOwnWordsAndIsNotBilled()
    {
        await using var colibri = await FakeColibri.StartAsync();
        await using var mesh = await BrioMesh.StartAsync(colibri);

        var body = """{"model":"olmoe","state":"s","options":["only one"]}""";
        var response = await mesh.Client.PostAsync("/v1/brio", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at least two options", await Message(response));
        Assert.Empty(mesh.Ledger.Rows);
    }

    [Fact]
    public async Task AFullEngineIsA503WithItsRetryAfter()
    {
        await using var colibri = await FakeColibri.StartAsync();
        await using var mesh = await BrioMesh.StartAsync(colibri);

        var response = await mesh.Client.PostAsync("/v1/brio", Json(Choice(state: "busy")));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("2", response.Headers.GetValues("Retry-After").Single());
        Assert.Equal(1, colibri.BrioCalls);
    }

    [Fact]
    public async Task AModelNoNodeHoldsIsThe404AndAFormlessBodyNeverLeavesTheHub()
    {
        await using var colibri = await FakeColibri.StartAsync();
        await using var mesh = await BrioMesh.StartAsync(colibri);

        var unknown = await mesh.Client.PostAsync("/v1/brio", Json("""{"model":"gpt-4","state":"s","options":["a","b"]}"""));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var formless = await mesh.Client.PostAsync("/v1/brio", Json("""{"model":"olmoe","state":"s"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, formless.StatusCode);
        Assert.Contains("exactly one of", await Message(formless));

        Assert.Equal(0, colibri.BrioCalls);
    }

    [Fact]
    public async Task ANodeThatDoesNotScoreLeavesTheHubA503NamingScore()
    {
        // The same colibri node with the subtractive key on: it still chats, and the hub knows the
        // model, so this is fleet state (40 D4) and not the 404.
        await using var colibri = await FakeColibri.StartAsync();
        await using var mesh = await BrioMesh.StartAsync(colibri, disabled: ["score"]);

        var response = await mesh.Client.PostAsync("/v1/brio", Json(Choice()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("'score'", await Message(response));
        Assert.Equal(0, colibri.BrioCalls);
    }

    [Fact]
    public async Task TheGenericToolRouteReachesTheSameEngine()
    {
        await using var colibri = await FakeColibri.StartAsync();
        await using var mesh = await BrioMesh.StartAsync(colibri);

        var response = await mesh.Client.PostAsync("/api/tools/score", Json(Choice()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("brio.choice", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("object").GetString());
    }

    [Fact]
    public async Task ASoloColibriNodeAnswersTheSameRequestTheSameWay()
    {
        await using var colibri = await FakeColibri.StartAsync();
        await using var solo = await SoloHost.StartAsync(
            null,
            "--Backend:Type=colibri",
            $"--Upstream:BaseUrl={colibri.Url}/v1",
            "--Colibri:Watch=false");

        var response = await solo.Client.PostAsync("/v1/brio", Json(Choice()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("node-solo", response.Headers.GetValues("X-InferHub-Served-By").Single());
        Assert.Equal("no", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("answer").GetString());

        var unknown = await solo.Client.PostAsync("/v1/brio", Json("""{"model":"gpt-4","state":"s","options":["a","b"]}"""));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("does not exist", await Message(unknown));
    }

    [Fact]
    public async Task ASoloNodeThatIsNotColibriSaysWhatIsMissing()
    {
        await using var solo = await SoloHost.StartAsync();

        var response = await solo.Client.PostAsync("/v1/brio", Json(Choice()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("'score'", await Message(response));
    }

    // ---- harness ------------------------------------------------------------------------

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<string> Message(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetProperty("message").GetString()!;
    }

    /// <summary>
    /// colibri v1.12.1's gateway, as far as a node can tell: the three routes it calls, the same
    /// envelopes, and the same refusal for a body with no <c>Content-Length</c> (93 D6).
    /// </summary>
    private sealed class FakeColibri : IAsyncDisposable
    {
        public const long TotalTokens = 57;

        private WebApplication app = null!;
        private int brioCalls;

        public string Url { get; private set; } = null!;

        public int BrioCalls => Volatile.Read(ref brioCalls);

        public JsonObject? LastBody { get; private set; }

        public static async Task<FakeColibri> StartAsync()
        {
            var fake = new FakeColibri();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            fake.app = builder.Build();
            fake.app.MapGet("/health", () => Results.Json(new { status = "ok" }));
            fake.app.MapGet("/v1/models", () => Results.Json(new { @object = "list", data = new[] { new { id = "olmoe", @object = "model" } } }));
            // Written out rather than returned: a (HttpContext) => Task<IResult> binds as a plain
            // RequestDelegate, and the result would be dropped for an empty 200.
            fake.app.MapPost("/v1/brio", async context => await (await fake.BrioAsync(context)).ExecuteAsync(context));

            await fake.app.StartAsync();
            fake.Url = fake.app.Urls.First();
            return fake;
        }

        private async Task<IResult> BrioAsync(HttpContext context)
        {
            Interlocked.Increment(ref brioCalls);

            if (context.Request.ContentLength is not > 0)
            {
                return Fail(400, "Request body must be between 1 and 4194304 bytes.", "invalid_request_error", null);
            }

            var body = (JsonObject)(await JsonNode.ParseAsync(context.Request.Body))!;
            LastBody = body;

            if (body["model"]?.GetValue<string>() != "olmoe")
            {
                return Fail(404, $"The model `{body["model"]}` does not exist.", "invalid_request_error", "model_not_found");
            }

            if (body["state"]?.GetValue<string>() == "busy")
            {
                context.Response.Headers.RetryAfter = "2";
                return Fail(429, "Server busy: the request queue is full.", "rate_limit_error", "rate_limit_exceeded");
            }

            if (body["options"] is JsonArray { Count: < 2 })
            {
                return Fail(400, "`options` needs at least two options to choose between.", "invalid_request_error", null);
            }

            return Results.Text(
                $$$"""
                {"object":"brio.choice","answer":"no","entropy":0.52,"normalize":"mean",
                 "choices":[{"option":"no","logprob":-1.1,"tokens":1,"mean_logprob":-1.1,"p":0.61},
                            {"option":"unclear","logprob":-2.0,"tokens":2,"mean_logprob":-1.0,"p":0.27},
                            {"option":"yes","logprob":-2.3,"tokens":1,"mean_logprob":-2.3,"p":0.12}],
                 "id":"brio-0","created":0,"model":"olmoe",
                 "usage":{"prompt_tokens":53,"completion_tokens":0,"read_tokens":4,"total_tokens":{{{TotalTokens}}}}}
                """,
                "application/json");
        }

        private static IResult Fail(int status, string message, string type, string? code)
            => Results.Json(new { error = new { message, type, param = (string?)null, code } }, statusCode: status);

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class RecordingLedger : IUsageLedger
    {
        private readonly ConcurrentQueue<UsageRecord> rows = new();

        public IReadOnlyList<UsageRecord> Rows => rows.ToArray();

        public ValueTask RecordAsync(UsageRecord record, CancellationToken cancellationToken = default)
        {
            rows.Enqueue(record);
            return ValueTask.CompletedTask;
        }

        public Task<IReadOnlyList<UsageAggregate>> QueryAsync(UsageQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<UsageAggregate>>([]);

        public async Task<UsageRecord> SingleAsync()
        {
            for (var i = 0; i < 100 && rows.IsEmpty; i++)
            {
                await Task.Delay(20);
            }

            return Assert.Single(Rows);
        }
    }

    /// <summary>AudioMesh's hub with the Brio route, and a node whose backend is a real colibri client.</summary>
    private sealed class BrioMesh : IAsyncDisposable
    {
        private const string Secret = "brio-mesh-secret";

        private WebApplication app = null!;
        private CoordinatorConnection node = null!;
        private string scratch = null!;

        public HttpClient Client { get; private set; } = null!;

        public NodeRegistry Registry { get; } = new();

        public RecordingLedger Ledger { get; } = new();

        public CapturingLoggerProvider Logs { get; } = new();

        public static async Task<BrioMesh> StartAsync(FakeColibri colibri, string[]? disabled = null)
        {
            var mesh = new BrioMesh
            {
                scratch = Path.Combine(Path.GetTempPath(), "inferhub-brio-" + Guid.NewGuid().ToString("N"))
            };

            await mesh.StartCoordinatorAsync();
            await mesh.StartNodeAsync(colibri, disabled ?? []);
            return mesh;
        }

        private async Task StartCoordinatorAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(Logs);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);

            var admission = new AdmissionControl();

            builder.Services.AddSignalR();
            builder.Services.AddSingleton<IOptionsMonitor<ApiKeyOptions>>(
                new StaticApiKeys(new ApiKeyOptions { NodeEnrollmentSecret = Secret }));
            builder.Services.AddSingleton<NodeAuthFilter>();
            builder.Services.AddSingleton<INodeRegistry>(Registry);
            builder.Services.AddSingleton<InferHub.Coordinator.Services.IRouter, Router>();
            builder.Services.AddSingleton<IConversationAffinity>(
                new ConversationAffinity(Options.Create(new RouterOptions()), new NoAffinity(), TimeProvider.System));
            builder.Services.AddSingleton<INodeConnectionTracker, NodeConnectionTracker>();
            builder.Services.AddSingleton<Metrics>();
            builder.Services.AddSingleton<ThroughputTracker>();
            builder.Services.AddSingleton<IUsageLedger>(Ledger);
            builder.Services.AddSingleton(admission);
            builder.Services.AddSingleton(services => new UsageMeter(
                Ledger,
                admission,
                services.GetRequiredService<ILogger<UsageMeter>>()));
            builder.Services.AddSingleton(services => TestUsage.Queue(services.GetRequiredService<INodeRegistry>()));
            builder.Services.Configure<DispatcherOptions>(_ => { });
            builder.Services.Configure<RouterOptions>(_ => { });
            builder.Services.Configure<ToolEdgeOptions>(_ => { });
            builder.Services.AddSingleton<Dispatcher>();
            builder.Services.AddSingleton<IDispatcher>(sp => sp.GetRequiredService<Dispatcher>());
            builder.Services.AddSingleton<IToolDispatcher>(sp => sp.GetRequiredService<Dispatcher>());
            builder.Services.AddSingleton<InferHub.Coordinator.Cluster.IClusterMembership,
                InferHub.Coordinator.Cluster.SingleCoordinatorMembership>();

            app = builder.Build();
            app.MapBrioEndpoints();
            app.MapToolEndpoints();
            app.MapHub<NodeHub>("/hubs/node");

            await app.StartAsync();
            Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        }

        private async Task StartNodeAsync(FakeColibri colibri, string[] disabled)
        {
            var services = new ServiceCollection();
            services.AddHttpClient(UpstreamBackend.ColibriHttpClientName)
                .AddHttpMessageHandler(() => new ColibriRequestHandler(1));
            var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

            var backend = new UpstreamBackend(
                factory,
                Options.Create(new BackendOptions { Type = BackendOptions.Colibri }),
                Options.Create(new UpstreamBackendOptions { BaseUrl = colibri.Url + "/v1" }),
                NullLogger<UpstreamBackend>.Instance);

            var toolOptions = Options.Create(new ToolOptions());
            var runtime = new NoToolRuntime();
            var replicas = new ReplicaStore(
                Options.Create(new VectorReplicaOptions { ReplicaDirectory = Path.Combine(scratch, "replicas") }),
                NullLogger<ReplicaStore>.Instance);

            var nodeOptions = new NodeOptions { Name = "brio-node" };
            nodeOptions.Capabilities.Disabled.AddRange(disabled);

            node = new CoordinatorConnection(
                Options.Create(new CoordinatorOptions
                {
                    Url = app.Urls.First(),
                    EnrollmentSecret = Secret,
                    HeartbeatInterval = TimeSpan.FromSeconds(30),
                    ModelRefreshInterval = TimeSpan.FromSeconds(30)
                }),
                Options.Create(nodeOptions),
                new FixedNodeId("brio-node"),
                backend,
                new InferenceExecutor(backend, replicas, TestProfiles.IdleRetrieval(), NullLogger<InferenceExecutor>.Instance),
                new ModelCommandExecutor(backend, NullLogger<ModelCommandExecutor>.Instance),
                new ToolExecutor(runtime, toolOptions, NullLogger<ToolExecutor>.Instance, backend),
                runtime,
                toolOptions,
                TestProfiles.Applier(backend, runtime),
                TestProfiles.IdleRetrieval(),
                replicas,
                new NoBackendSupervisor(),
                InferHub.Node.Resources.NoResourceGovernor.Instance,
                NullLogger<CoordinatorConnection>.Instance);

            await node.StartAsync(CancellationToken.None);

            for (var i = 0; i < 200 && !Registry.Snapshot(DateTimeOffset.UtcNow)
                     .Any(n => (n.Capabilities ?? []).Any(c => c.Kind == "chat")); i++)
            {
                await Task.Delay(50);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await node.StopAsync(CancellationToken.None);
            }
            catch (Exception)
            {
            }

            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();

            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        private sealed class FixedNodeId(string id) : INodeIdentity
        {
            public string GetOrCreateNodeId() => id;
        }

        private sealed class NoAffinity : IAffinityStore
        {
            public IReadOnlyCollection<PersistedAffinity> Load() => [];

            public void Record(string conversationKey, string nodeId, DateTimeOffset lastUsed)
            {
            }

            public void Forget(string conversationKey)
            {
            }
        }

        private sealed class StaticApiKeys(ApiKeyOptions value) : IOptionsMonitor<ApiKeyOptions>
        {
            public ApiKeyOptions CurrentValue { get; } = value;

            public ApiKeyOptions Get(string? name) => CurrentValue;

            public IDisposable? OnChange(Action<ApiKeyOptions, string?> listener) => null;
        }
    }
}
