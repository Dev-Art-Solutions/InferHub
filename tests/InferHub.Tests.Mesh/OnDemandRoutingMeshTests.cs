using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Cluster;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 86, over a real Kestrel hub and real SignalR connections. The router's tiering is unit
/// tested; only the wire can show that the nested <see cref="OnDemandState"/> survives the hop, that
/// a hub composed the shipped way dispatches a chat past a node whose card holds diffusion, and that
/// a node sending the pre-3.51 five-field heartbeat is routed exactly as before.
/// </summary>
public class OnDemandRoutingMeshTests
{
    [Fact]
    public async Task AChatGoesToTheNodeWhoseCardAlreadyHoldsOllama()
    {
        await using var hub = await OnDemandHub.StartAsync();
        await using var a = await hub.ConnectNodeAsync("node-a", "alpha");
        await using var b = await hub.ConnectNodeAsync("node-b", "beta");

        await a.InvokeAsync("Heartbeat", new Heartbeat("node-a", DateTimeOffset.UtcNow, 0,
            OnDemand: new OnDemandState("tool:diffusion", ["image", "video"], false, 0)));
        await b.InvokeAsync("Heartbeat", new Heartbeat("node-b", DateTimeOffset.UtcNow, 0,
            OnDemand: new OnDemandState("ollama", ["chat", "embed"], false, 0)));

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await hub.ChatAsync()).StatusCode);
        }

        Assert.Equal(["node-b", "node-b", "node-b", "node-b"], hub.Dispatched.ToArray());

        // 86 D5: the state reaches /api/status verbatim. The metric is PrometheusMetricsTests'.
        var status = await hub.Client.GetFromJsonAsync<JsonElement>("/api/status");
        var byId = status.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("nodeId").GetString()!);
        Assert.Equal("tool:diffusion", byId["node-a"].GetProperty("onDemand").GetProperty("holder").GetString());
        Assert.Equal(["image", "video"], byId["node-a"].GetProperty("onDemand").GetProperty("warmFor").EnumerateArray().Select(k => k.GetString()));
    }

    /// <summary>
    /// 86 D1, across the wire. A v3.50 node sends the five-field heartbeat, which deserializes with
    /// the new member absent — and absent must mean "always warm", never "cold".
    /// </summary>
    [Fact]
    public async Task ANodeThatSendsTheOldHeartbeatIsRoutedExactlyAsBefore()
    {
        await using var hub = await OnDemandHub.StartAsync();
        await using var a = await hub.ConnectNodeAsync("node-a", "alpha");
        await using var b = await hub.ConnectNodeAsync("node-b", "beta");

        await a.InvokeAsync("Heartbeat", new LegacyHeartbeat("node-a", DateTimeOffset.UtcNow, 0, null, null));
        await b.InvokeAsync("Heartbeat", new LegacyHeartbeat("node-b", DateTimeOffset.UtcNow, 0, null, null));

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await hub.ChatAsync()).StatusCode);
        }

        // Today's pick, untouched: the first request goes to node-a (the round-robin's first, by
        // name), and the hub's conversation key — derived from the identical messages — keeps the
        // next three on it. That same first pick is why the warm test's all-node-b is the tiering
        // and not an accident of ordering.
        Assert.Equal(["node-a", "node-a", "node-a", "node-a"], hub.Dispatched.ToArray());

        var status = await hub.Client.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.All(
            status.GetProperty("nodes").EnumerateArray(),
            node => Assert.Equal(JsonValueKind.Null, node.GetProperty("onDemand").ValueKind));
    }

    [Fact]
    public async Task AnOldNodeIsAlwaysWarmBesideAnOnDemandOneHoldingSomethingElse()
    {
        await using var hub = await OnDemandHub.StartAsync();
        await using var a = await hub.ConnectNodeAsync("node-a", "alpha");
        await using var b = await hub.ConnectNodeAsync("node-b", "beta");

        await a.InvokeAsync("Heartbeat", new Heartbeat("node-a", DateTimeOffset.UtcNow, 0,
            OnDemand: new OnDemandState("tool:whisper", ["transcribe"], false, 0)));
        await b.InvokeAsync("Heartbeat", new LegacyHeartbeat("node-b", DateTimeOffset.UtcNow, 0, null, null));

        Assert.Equal(HttpStatusCode.OK, (await hub.ChatAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await hub.ChatAsync()).StatusCode);

        Assert.Equal(["node-b", "node-b"], hub.Dispatched.ToArray());
    }

    private sealed record LegacyHeartbeat(
        string NodeId,
        DateTimeOffset Timestamp,
        int InFlight,
        BackendHealth? Backend,
        bool? ResourceThrottled);

    /// <summary>
    /// A hub composed the way the product composes one, as in <c>BackendHealthMeshTests</c>, with a
    /// dispatcher that answers and records which node it was sent to.
    /// </summary>
    private sealed class OnDemandHub : IAsyncDisposable
    {
        private const string Secret = "node-enrollment-secret";

        private WebApplication app = null!;

        public HttpClient Client { get; private set; } = null!;

        public ConcurrentQueue<string> Dispatched { get; } = new();

        private string Url { get; set; } = null!;

        public static async Task<OnDemandHub> StartAsync()
        {
            var host = new OnDemandHub();

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            builder.Services.AddSignalR();
            builder.Services.AddSingleton<IOptionsMonitor<ApiKeyOptions>>(
                new StaticApiKeys(new ApiKeyOptions { NodeEnrollmentSecret = Secret }));
            builder.Services.AddSingleton<NodeAuthFilter>();
            builder.Services.AddSingleton<INodeRegistry>(new NodeRegistry());
            builder.Services.AddSingleton<IConversationAffinity, ConversationAffinity>();
            builder.Services.AddSingleton<INodeConnectionTracker, NoConnections>();
            builder.Services.AddSingleton<IClusterMembership, SingleCoordinatorMembership>();
            builder.Services.AddSingleton(Options.Create(new RouterOptions()));
            builder.Services.AddSingleton(Options.Create(new AffinityOptions()));
            builder.Services.AddSingleton<ThroughputTracker>();
            builder.Services.AddSingleton<IRouter, Router>();
            builder.Services.AddSingleton<IDispatcher>(new RecordingDispatcher(host.Dispatched));
            builder.Services.AddSingleton<IProviderDispatcher, NoProvider>();
            builder.Services.AddSingleton<Metrics>();
            builder.Services.AddSingleton<AdmissionControl>();
            builder.Services.AddSingleton(services => TestUsage.Meter(
                admission: services.GetRequiredService<AdmissionControl>()));
            builder.Services.AddSingleton(services => TestUsage.Queue(
                services.GetRequiredService<INodeRegistry>()));

            host.app = builder.Build();
            host.app.MapHub<NodeHub>("/hubs/node");
            host.app.MapInferenceEndpoints();
            host.app.MapStatusEndpoint("3.51.0");

            await host.app.StartAsync();
            host.Url = host.app.Urls.First();
            host.Client = new HttpClient { BaseAddress = new Uri(host.Url) };

            return host;
        }

        public async Task<HubConnection> ConnectNodeAsync(string nodeId, string name)
        {
            var connection = new HubConnectionBuilder()
                .WithUrl($"{Url}/hubs/node", options =>
                {
                    options.Headers[NodeAuthFilter.EnrollmentSecretHeader] = Secret;
                })
                .Build();

            await connection.StartAsync();

            var now = DateTimeOffset.UtcNow;
            await connection.InvokeAsync("Register", new NodeRegistration(nodeId, name, "http://localhost:11434/", "3.51.0"));
            await connection.InvokeAsync("ReportModels", new NodeModels(nodeId, [new ModelInfo("llama3", "sha256:abc", 1234)], now));

            return connection;
        }

        public Task<HttpResponseMessage> ChatAsync()
            => Client.PostAsync(
                "/api/chat",
                new StringContent(
                    """{"model":"llama3","messages":[{"role":"user","content":"hi"}],"stream":false}""",
                    Encoding.UTF8,
                    "application/json"));

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }

        private sealed class StaticApiKeys(ApiKeyOptions value) : IOptionsMonitor<ApiKeyOptions>
        {
            public ApiKeyOptions CurrentValue => value;

            public ApiKeyOptions Get(string? name) => value;

            public IDisposable? OnChange(Action<ApiKeyOptions, string?> listener) => null;
        }

        private sealed class NoConnections : INodeConnectionTracker
        {
            public void Track(string connectionId, Microsoft.AspNetCore.SignalR.HubCallerContext context) { }

            public void Forget(string connectionId) { }

            public bool Abort(string connectionId) => false;

            public int AbortAll() => 0;
        }

        private sealed class RecordingDispatcher(ConcurrentQueue<string> dispatched) : IDispatcher
        {
            public Task<InferenceResult> DispatchAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
            {
                dispatched.Enqueue(node.NodeId);
                return Task.FromResult(InferenceResult.Succeeded(job.JobId, """{"model":"llama3","done":true}"""));
            }

            public Task<ChannelReader<InferenceChunk>> DispatchStreamAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public bool Complete(InferenceResult result) => true;

            public bool WriteChunk(InferenceChunk chunk) => true;

            public void FailForConnection(string connectionId, Exception? error = null)
            {
            }
        }

        private sealed class NoProvider : IProviderDispatcher
        {
            public ProviderDecision Decide(string model, bool hasCapableNode, ProviderSteer steer) => ProviderDecision.No;

            public Task<ProviderResult> DispatchAsync(string kind, string rawJson, string model, bool stream, CancellationToken cancellationToken)
                => throw new NotSupportedException();
        }
    }
}
