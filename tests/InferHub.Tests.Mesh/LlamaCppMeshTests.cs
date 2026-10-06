using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.OpenAi;
using InferHub.Coordinator.Services;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
using InferHub.Node.Tools;
using InferHub.Node.Vector;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 96 across a real wire: an HTTP client → a real hub → real SignalR → a real node whose
/// llama.cpp engine crosses a real socket to a server answering what a b11417 router answers. The
/// hub pulls a repo into it, the client calls its native routes and its reranker, and nothing the
/// client sent reaches a log.
/// </summary>
public class LlamaCppMeshTests
{
    private const string Repo = "bartowski/SmolLM2-135M-Instruct-GGUF:Q4_K_M";

    private const string Secret = "The Plovdiv warehouse code is 4471.";

    [Fact]
    public async Task TheHubPullsARepoIntoTheRouterAndTheFleetCanRouteItAfterwards()
    {
        await using var router = await FakeRouter.StartAsync();
        await using var mesh = await LlamaCppMesh.StartAsync(router);

        Assert.Null(mesh.Router.Route(Repo, capability: CapabilityKinds.Chat));

        var done = mesh.WaitForTerminal();
        var start = await mesh.Commands.SendAsync(LlamaCppMesh.NodeId, ModelCommand.KindPull, Repo, CancellationToken.None, engine: "gguf");
        Assert.NotNull(start);

        var terminal = await done;
        Assert.Null(terminal.Error);
        Assert.Equal("success", terminal.Status);
        Assert.Contains($"POST /models {Repo}", router.Calls);

        // The node re-reports after a successful pull; the repo is a chat model now.
        await mesh.WaitForAsync(() => mesh.Router.Route(Repo, capability: CapabilityKinds.Chat) is not null);
        Assert.NotNull(mesh.Router.Route(Repo, capability: CapabilityKinds.LlamaCpp));
    }

    [Fact]
    public async Task AnUnloadReachesTheRouterAndAFileOnTheBoxCannotBeDeletedFromTheHub()
    {
        await using var router = await FakeRouter.StartAsync();
        router.Models["qwen"] = "loaded";
        await using var mesh = await LlamaCppMesh.StartAsync(router);

        var unloaded = mesh.WaitForTerminal();
        await mesh.Commands.SendAsync(LlamaCppMesh.NodeId, ModelCommand.KindUnload, "qwen", CancellationToken.None);
        Assert.Equal("unloaded", (await unloaded).Status);
        Assert.Contains("POST /models/unload qwen", router.Calls);

        var deleted = mesh.WaitForTerminal();
        await mesh.Commands.SendAsync(LlamaCppMesh.NodeId, ModelCommand.KindDelete, "qwen", CancellationToken.None);
        Assert.Contains("Serve:ModelsDir", (await deleted).Error);
        Assert.DoesNotContain(router.Calls, c => c.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheNativeRoutesAnswerAsTheEngineDoesAndWhatGeneratedIsBilled()
    {
        await using var router = await FakeRouter.StartAsync();
        await using var mesh = await LlamaCppMesh.StartAsync(router);

        var tokenize = await mesh.Client.PostAsync("/v1/llamacpp/tokenize", Json($$"""{"model":"qwen","content":"{{Secret}}"}"""));
        Assert.Equal(HttpStatusCode.OK, tokenize.StatusCode);
        Assert.Equal("""{"tokens":[1,2,3]}""", await tokenize.Content.ReadAsStringAsync());

        var completion = await mesh.Client.PostAsync(
            "/v1/llamacpp/completion",
            Json($$"""{"model":"qwen","prompt":"{{Secret}}","grammar":"root ::= \"yes\" | \"no\""}"""));
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        Assert.Contains("\"content\":\"yes\"", await completion.Content.ReadAsStringAsync());

        // The body went through untouched — the grammar is llama.cpp's to read, not the hub's.
        Assert.Contains(router.Bodies, b => b.Contains("root ::=", StringComparison.Ordinal));

        var row = await mesh.Ledger.SingleAsync();
        Assert.Equal((CapabilityKinds.LlamaCpp, "qwen", 7L, 1L), (row.Kind, row.Model, row.PromptTokens, row.CompletionTokens));

        var props = await mesh.Client.GetAsync("/v1/llamacpp/props?model=qwen");
        Assert.Equal(HttpStatusCode.OK, props.StatusCode);

        var slots = await mesh.Client.PostAsync("/v1/llamacpp/slots", Json("""{"model":"qwen"}"""));
        Assert.Equal(HttpStatusCode.NotFound, slots.StatusCode);

        var streamed = await mesh.Client.PostAsync("/v1/llamacpp/completion", Json("""{"model":"qwen","prompt":"x","stream":true}"""));
        Assert.Equal(HttpStatusCode.BadRequest, streamed.StatusCode);

        var refused = await mesh.Client.PostAsync("/v1/llamacpp/infill", Json("""{"model":"qwen"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("input_extra", await refused.Content.ReadAsStringAsync());

        Assert.DoesNotContain(mesh.Logs.Lines, line => line.Contains("4471", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARerankIsAnsweredBestFirstByTheLlamaCppReranker()
    {
        await using var router = await FakeRouter.StartAsync();
        await using var mesh = await LlamaCppMesh.StartAsync(router);

        var response = await mesh.Client.PostAsync(
            "/v1/rerank",
            Json($$"""{"model":"bge","query":"{{Secret}}","documents":["stocks fell","a cat sat"],"top_n":1,"return_documents":true}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(1, result.GetProperty("index").GetInt32());
        Assert.Equal("a cat sat", result.GetProperty("document").GetProperty("text").GetString());

        // A chat model is not a reranker: the fleet knows it, and says no node provides that.
        var chat = await mesh.Client.PostAsync("/v1/rerank", Json("""{"model":"qwen","query":"q","documents":["a"]}"""));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, chat.StatusCode);

        Assert.DoesNotContain(mesh.Logs.Lines, line => line.Contains("4471", StringComparison.Ordinal));
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>What a b11417 router answers, as measured in the live run.</summary>
    private sealed class FakeRouter : IAsyncDisposable
    {
        private WebApplication app = null!;
        private readonly ConcurrentQueue<string> calls = new();
        private readonly ConcurrentQueue<string> bodies = new();
        private int pollsSincePull;

        public string Url { get; private set; } = null!;

        public ConcurrentDictionary<string, string> Models { get; } = new() { ["qwen"] = "unloaded", ["bge"] = "unloaded" };

        public IReadOnlyList<string> Calls => calls.ToArray();

        public IReadOnlyList<string> Bodies => bodies.ToArray();

        public static async Task<FakeRouter> StartAsync()
        {
            var fake = new FakeRouter();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            fake.app = builder.Build();

            fake.app.MapGet("/health", () => Results.Json(new { status = "ok" }));
            fake.app.MapGet("/models", () => fake.Listing());
            fake.app.MapGet("/v1/models", () => fake.Listing());

            fake.app.MapPost("/models", async (HttpContext context) =>
            {
                var model = await fake.ModelOf(context);
                fake.calls.Enqueue($"POST /models {model}");
                fake.Models[model] = "downloading";
                Interlocked.Exchange(ref fake.pollsSincePull, 0);
                return Results.Json(new { success = true });
            });

            fake.app.MapPost("/models/unload", async (HttpContext context) =>
            {
                var model = await fake.ModelOf(context);
                fake.calls.Enqueue($"POST /models/unload {model}");
                fake.Models[model] = "unloaded";
                return Results.Json(new { success = true });
            });

            fake.app.MapDelete("/models", (HttpContext context) =>
            {
                fake.calls.Enqueue($"DELETE /models {context.Request.Query["model"]}");
                return Results.Json(new { success = true });
            });

            fake.app.MapPost("/tokenize", async (HttpContext context) =>
            {
                await fake.ModelOf(context);
                return Results.Text("""{"tokens":[1,2,3]}""", "application/json");
            });

            fake.app.MapPost("/completion", async (HttpContext context) =>
            {
                await fake.ModelOf(context);
                return Results.Text("""{"content":"yes","stop":true,"tokens_evaluated":7,"tokens_predicted":1}""", "application/json");
            });

            fake.app.MapPost("/infill", () => Results.Json(
                new { error = new { code = 400, message = "\"input_extra\" must be an array of {\"filename\": string, \"text\": string}", type = "invalid_request_error" } },
                statusCode: 400));

            fake.app.MapGet("/props", () => Results.Json(new { default_generation_settings = new { n_ctx = 4096 } }));

            fake.app.MapPost("/v1/rerank", async (HttpContext context) =>
            {
                await fake.ModelOf(context);
                return Results.Text(
                    """{"model":"bge","object":"list","usage":{"prompt_tokens":11,"total_tokens":11},"results":[{"index":0,"relevance_score":-3.1},{"index":1,"relevance_score":2.4}]}""",
                    "application/json");
            });

            await fake.app.StartAsync();
            fake.Url = fake.app.Urls.First();
            return fake;
        }

        private IResult Listing()
        {
            // A download finishes a couple of polls after it started.
            if (Interlocked.Increment(ref pollsSincePull) > 2)
            {
                foreach (var pair in Models.Where(m => m.Value == "downloading"))
                {
                    Models[pair.Key] = "unloaded";
                }
            }

            return Results.Json(new
            {
                data = Models.Select(m => new
                {
                    id = m.Key,
                    @object = "model",
                    owned_by = "llamacpp",
                    status = new { value = m.Value },
                    source = m.Key.Contains('/') ? "cache" : "models_dir",
                    can_remove = m.Key.Contains('/')
                }),
                @object = "list"
            });
        }

        private async Task<string> ModelOf(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            bodies.Enqueue(body);
            return JsonDocument.Parse(body).RootElement.GetProperty("model").GetString()!;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class RecordingLedger : IUsageLedger
    {
        private readonly ConcurrentQueue<UsageRecord> rows = new();

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

            return Assert.Single(rows.ToArray());
        }
    }

    private sealed class LlamaCppMesh : IAsyncDisposable
    {
        public const string NodeId = "llamacpp-node";
        private const string EnrollmentSecret = "llamacpp-mesh-secret";

        private WebApplication app = null!;
        private CoordinatorConnection node = null!;
        private MultiBackend engines = null!;
        private string scratch = null!;

        public HttpClient Client { get; private set; } = null!;

        public NodeRegistry Registry { get; } = new();

        public RecordingLedger Ledger { get; } = new();

        public CapturingLoggerProvider Logs { get; } = new();

        public IRouter Router { get; private set; } = null!;

        public ModelCommandCoordinator Commands { get; private set; } = null!;

        public static async Task<LlamaCppMesh> StartAsync(FakeRouter router)
        {
            var mesh = new LlamaCppMesh { scratch = Path.Combine(Path.GetTempPath(), "inferhub-llamacpp-" + Guid.NewGuid().ToString("N")) };
            await mesh.StartCoordinatorAsync();
            await mesh.StartNodeAsync(router);
            return mesh;
        }

        public Task<ModelCommandProgress> WaitForTerminal()
        {
            var done = new TaskCompletionSource<ModelCommandProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<ModelCommandProgress>? handler = null;
            handler = progress =>
            {
                if (progress.Done)
                {
                    Commands.ProgressReceived -= handler;
                    done.TrySetResult(progress);
                }
            };
            Commands.ProgressReceived += handler;
            return done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }

        public async Task WaitForAsync(Func<bool> predicate)
        {
            for (var i = 0; i < 400 && !predicate(); i++)
            {
                await Task.Delay(25);
            }

            Assert.True(predicate());
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
            builder.Services.AddSingleton<IOptionsMonitor<ApiKeyOptions>>(new StaticApiKeys(new ApiKeyOptions { NodeEnrollmentSecret = EnrollmentSecret }));
            builder.Services.AddSingleton<NodeAuthFilter>();
            builder.Services.AddSingleton<INodeRegistry>(Registry);
            builder.Services.AddSingleton<IRouter, InferHub.Coordinator.Services.Router>();
            builder.Services.AddSingleton<IConversationAffinity>(
                new ConversationAffinity(Options.Create(new RouterOptions()), new NoAffinity(), TimeProvider.System));
            builder.Services.AddSingleton<INodeConnectionTracker, NodeConnectionTracker>();
            builder.Services.AddSingleton<Metrics>();
            builder.Services.AddSingleton<ThroughputTracker>();
            builder.Services.AddSingleton<IUsageLedger>(Ledger);
            builder.Services.AddSingleton(admission);
            builder.Services.AddSingleton(services => new UsageMeter(Ledger, admission, services.GetRequiredService<ILogger<UsageMeter>>()));
            builder.Services.AddSingleton(services => TestUsage.Queue(services.GetRequiredService<INodeRegistry>()));
            builder.Services.Configure<DispatcherOptions>(_ => { });
            builder.Services.Configure<RouterOptions>(_ => { });
            builder.Services.Configure<ToolEdgeOptions>(_ => { });
            builder.Services.AddSingleton<InferHub.Coordinator.Services.Dispatcher>();
            builder.Services.AddSingleton<IDispatcher>(sp => sp.GetRequiredService<InferHub.Coordinator.Services.Dispatcher>());
            builder.Services.AddSingleton<IToolDispatcher>(sp => sp.GetRequiredService<InferHub.Coordinator.Services.Dispatcher>());
            builder.Services.AddSingleton<ModelCommandCoordinator>();
            builder.Services.AddSingleton<InferHub.Coordinator.Cluster.IClusterMembership,
                InferHub.Coordinator.Cluster.SingleCoordinatorMembership>();

            app = builder.Build();
            app.MapLlamaCppEndpoints();
            app.MapHub<NodeHub>("/hubs/node");

            await app.StartAsync();
            Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            Router = app.Services.GetRequiredService<IRouter>();
            Commands = app.Services.GetRequiredService<ModelCommandCoordinator>();
        }

        private async Task StartNodeAsync(FakeRouter router)
        {
            var services = new ServiceCollection();
            services.AddHttpClient(UpstreamBackend.HttpClientName);
            var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

            var engine = new EngineOptions
            {
                Type = BackendOptions.LlamaCpp,
                Router = true,
                BaseUrl = router.Url + "/v1",
                Serve = { Presets = { ["bge"] = new LlamaCppPresetOptions { Reranking = true } } }
            };

            var upstream = new UpstreamBackend(
                factory,
                Options.Create(new BackendOptions { Type = BackendOptions.LlamaCpp }),
                Options.Create(new UpstreamBackendOptions { BaseUrl = engine.BaseUrl }),
                NullLogger<UpstreamBackend>.Instance);

            var llama = new LlamaCppBackend(
                upstream,
                engine,
                router: true,
                () => new HttpClient { BaseAddress = new Uri(router.Url + "/") },
                TimeProvider.System,
                NullLogger.Instance);

            engines = new MultiBackend(
                [new Engine("gguf", BackendOptions.LlamaCpp, llama, autostart: true)],
                TimeSpan.FromSeconds(5),
                TimeProvider.System,
                NullLogger.Instance);

            await engines.StartAsync(CancellationToken.None);

            var toolOptions = Options.Create(new ToolOptions());
            var runtime = new NoToolRuntime();
            var replicas = new ReplicaStore(
                Options.Create(new VectorReplicaOptions { ReplicaDirectory = Path.Combine(scratch, "replicas") }),
                NullLogger<ReplicaStore>.Instance);
            var nodeOptions = Options.Create(new NodeOptions { Name = NodeId });

            node = new CoordinatorConnection(
                Options.Create(new CoordinatorOptions
                {
                    Url = app.Urls.First(),
                    EnrollmentSecret = EnrollmentSecret,
                    HeartbeatInterval = TimeSpan.FromSeconds(30),
                    ModelRefreshInterval = TimeSpan.FromSeconds(30)
                }),
                nodeOptions,
                new FixedIdentity(NodeId),
                engines,
                new InferenceExecutor(engines, replicas, TestProfiles.IdleRetrieval(), NullLogger<InferenceExecutor>.Instance),
                new ModelCommandExecutor(engines, NullLogger<ModelCommandExecutor>.Instance),
                new ToolExecutor(runtime, toolOptions, NullLogger<ToolExecutor>.Instance, backendJobs: engines),
                runtime,
                toolOptions,
                TestProfiles.Applier(engines, runtime),
                TestProfiles.IdleRetrieval(),
                replicas,
                new NoBackendSupervisor(),
                InferHub.Node.Resources.NoResourceGovernor.Instance,
                NullLogger<CoordinatorConnection>.Instance,
                engines: engines);

            await node.StartAsync(CancellationToken.None);

            for (var i = 0; i < 400 && Router.Route("qwen", capability: CapabilityKinds.LlamaCpp) is null; i++)
            {
                await Task.Delay(25);
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

            await engines.StopAllAsync();
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

        private sealed class FixedIdentity(string nodeId) : INodeIdentity
        {
            public string GetOrCreateNodeId() => nodeId;
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
