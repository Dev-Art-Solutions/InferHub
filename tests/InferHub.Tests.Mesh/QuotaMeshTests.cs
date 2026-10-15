using System.Net;
using System.Text;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
using InferHub.Node.Profiles;
using InferHub.Node.Tools;
using InferHub.Node.Vector;
using InferHub.Shared.Quota;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 106 across a real wire: a node on its own OpenAI key, a vendor answering with limit headers, the
/// node's real <see cref="UpstreamBackend"/> and pooled client, and a real hub hearing the report. The
/// node's limits arrive under the node, and nothing of its credential does.
/// </summary>
public class QuotaMeshTests
{
    private const string NodeId = "quota-node";
    private const string Secret = "quota-mesh-secret";

    private const string Answer = """
    {"id":"c","created":0,"model":"gpt-5-mini","choices":[{"index":0,"message":{"role":"assistant","content":"Hi."},"finish_reason":"stop"}],
     "usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}
    """;

    [Fact]
    public async Task ANodesOwnProviderLimitsReachTheHubUnderThatNode()
    {
        var quotas = new NodeQuotaRegistry();
        await using var app = await StartHubAsync(quotas);

        var tracker = new QuotaTracker();
        tracker.Declare(BackendOptions.OpenAi, BackendOptions.OpenAi, new QuotaBillingOptions { MonthlyBudgetUsd = 30 });

        var services = new ServiceCollection();
        services.AddHttpClient(UpstreamBackend.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new Vendor())
            .AddHttpMessageHandler(() => new QuotaObservingHandler(tracker));
        await using var provider = services.BuildServiceProvider();

        var backend = new UpstreamBackend(
            provider.GetRequiredService<IHttpClientFactory>(),
            Options.Create(new BackendOptions { Type = BackendOptions.OpenAi }),
            Options.Create(new UpstreamBackendOptions { BaseUrl = "https://api.openai.com/v1", ApiKey = "sk-node-own" }),
            NullLogger<UpstreamBackend>.Instance);

        var node = Node(app.Urls.First(), backend, tracker);
        node.QuotaReportDelay = TimeSpan.FromMilliseconds(50);
        await node.StartAsync(CancellationToken.None);

        try
        {
            // Registration carries the declared provider and its budget before anything was sent.
            var declared = await WaitAsync(quotas, q => q.Providers.Count == 1);
            Assert.Equal(30m, declared.Providers[0].Billing!.BudgetUsd);
            Assert.Empty(declared.Providers[0].Windows);

            // A real request through the real backend: the vendor's headers become this node's windows.
            await backend.ChatAsync("""{"model":"gpt-5-mini","messages":[{"role":"user","content":"hi"}],"stream":false}""", CancellationToken.None);

            var reported = await WaitAsync(quotas, q => q.Providers[0].Windows.Count > 0);
            var requests = Assert.Single(reported.Providers[0].Windows, w => w.Name == "requests");
            Assert.Equal(80, requests.UsedPercent);
            Assert.Equal(QuotaLevel.Warn, reported.Providers[0].Level);
            Assert.DoesNotContain("sk-node-own", System.Text.Json.JsonSerializer.Serialize(reported));
        }
        finally
        {
            await node.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<NodeQuotaState> WaitAsync(NodeQuotaRegistry quotas, Func<NodeQuotaState, bool> predicate)
    {
        for (var i = 0; i < 400; i++)
        {
            if (quotas.Of(NodeId) is { } state && predicate(state))
            {
                return state;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"No quota report matching the predicate arrived; the last was {quotas.Of(NodeId)}.");
    }

    private static async Task<WebApplication> StartHubAsync(NodeQuotaRegistry quotas)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddSignalR();
        builder.Services.AddSingleton<IOptionsMonitor<ApiKeyOptions>>(new ApiKeyMonitor(new ApiKeyOptions { NodeEnrollmentSecret = Secret }));
        builder.Services.AddSingleton<NodeAuthFilter>();
        builder.Services.AddSingleton<INodeRegistry>(new NodeRegistry());
        builder.Services.AddSingleton<IProfileRegistry>(new ProfileRegistry(new NoProfileStore(), NullLogger<ProfileRegistry>.Instance));
        builder.Services.AddSingleton(quotas);
        builder.Services.AddSingleton<IAuditLog, AuditLog>();
        builder.Services.AddSingleton<IRouter, InferHub.Coordinator.Services.Router>();
        builder.Services.AddSingleton<IConversationAffinity>(new ConversationAffinity(
            Options.Create(new RouterOptions()), new NoAffinityStore(), TimeProvider.System));
        builder.Services.AddSingleton<INodeConnectionTracker, NodeConnectionTracker>();
        builder.Services.AddSingleton<Metrics>();
        builder.Services.AddSingleton<ThroughputTracker>();
        builder.Services.Configure<DispatcherOptions>(_ => { });
        builder.Services.Configure<RouterOptions>(_ => { });
        builder.Services.AddSingleton<InferHub.Coordinator.Services.Dispatcher>();
        builder.Services.AddSingleton<IDispatcher>(sp => sp.GetRequiredService<InferHub.Coordinator.Services.Dispatcher>());
        builder.Services.AddSingleton<InferHub.Coordinator.Vector.CollectionOwnership>();
        builder.Services.AddSingleton<InferHub.Coordinator.Vector.NodeCorpusRegistry>();
        builder.Services.AddSingleton<NodeProfileCoordinator>();
        builder.Services.AddSingleton<InferHub.Coordinator.Cluster.IClusterMembership,
            InferHub.Coordinator.Cluster.SingleCoordinatorMembership>();

        var app = builder.Build();
        app.MapHub<NodeHub>("/hubs/node");
        await app.StartAsync();
        return app;
    }

    private static CoordinatorConnection Node(string url, IInferenceBackend backend, QuotaTracker tracker)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "inferhub-quota-mesh-" + Guid.NewGuid().ToString("N"));
        var toolOptions = Options.Create(new ToolOptions());
        var runtime = new NoToolRuntime();
        var replicas = new ReplicaStore(
            Options.Create(new VectorReplicaOptions { ReplicaDirectory = Path.Combine(scratch, "replicas") }),
            NullLogger<ReplicaStore>.Instance);
        var nodeOptions = Options.Create(new NodeOptions { Name = NodeId });

        return new CoordinatorConnection(
            Options.Create(new CoordinatorOptions
            {
                Url = url,
                EnrollmentSecret = Secret,
                HeartbeatInterval = TimeSpan.FromSeconds(30),
                ModelRefreshInterval = TimeSpan.FromSeconds(30)
            }),
            nodeOptions,
            new FixedIdentity(NodeId),
            backend,
            new InferenceExecutor(backend, replicas, TestProfiles.IdleRetrieval(), NullLogger<InferenceExecutor>.Instance),
            new ModelCommandExecutor(backend, NullLogger<ModelCommandExecutor>.Instance),
            new ToolExecutor(runtime, toolOptions, NullLogger<ToolExecutor>.Instance),
            runtime,
            toolOptions,
            new NodeProfileApplier(nodeOptions, toolOptions, backend, runtime, TestProfiles.IdleRetrieval(), NullLogger<NodeProfileApplier>.Instance),
            TestProfiles.IdleRetrieval(),
            replicas,
            new NoBackendSupervisor(),
            InferHub.Node.Resources.NoResourceGovernor.Instance,
            NullLogger<CoordinatorConnection>.Instance,
            quota: tracker);
    }

    private sealed class Vendor : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
                ? """{"object":"list","data":[{"id":"gpt-5-mini","object":"model"}]}"""
                : Answer;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            response.Headers.TryAddWithoutValidation("x-ratelimit-limit-requests", "500");
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining-requests", "100");
            return Task.FromResult(response);
        }
    }

    private sealed class FixedIdentity(string nodeId) : INodeIdentity
    {
        public string GetOrCreateNodeId() => nodeId;
    }

    private sealed class ApiKeyMonitor(ApiKeyOptions value) : IOptionsMonitor<ApiKeyOptions>
    {
        public ApiKeyOptions CurrentValue => value;

        public ApiKeyOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ApiKeyOptions, string?> listener) => null;
    }
}
