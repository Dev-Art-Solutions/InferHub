using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using InferHub.Coordinator.Cluster;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 69, over a real Kestrel hub and a real SignalR connection. The unit suites can prove the
/// registry skips a sick node; only the wire can prove that a nullable enum on <c>Heartbeat</c>
/// survives the hop, that a hub composed the shipped way refuses the shipped sentence, and that a
/// node which says nothing routes exactly as it did before the field existed.
/// </summary>
public class BackendHealthMeshTests
{
    [Fact]
    public async Task ADeadBackendTakesTheNodeOutOfRotationAndTheModelStaysDiscoverable()
    {
        await using var hub = await HealthHub.StartAsync();
        await using var node = await hub.ConnectNodeAsync();

        await hub.HeartbeatAsync(node, BackendHealth.Unreachable);

        // The claim: not a 404. The model is on the fleet and the fault is three feet away from it.
        var chat = await hub.ChatAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, chat.StatusCode);
        Assert.Equal(
            "every node holding model 'llama3' reports an unhealthy inference backend",
            await ErrorOf(chat));

        // A fleet-state refusal carries the hint that says it is worth coming back.
        Assert.NotNull(chat.Headers.RetryAfter);

        // And the model is still something a client can see and ask about — 65 D5's reasoning:
        // a client that cannot see a model cannot be told why it is unavailable.
        var tags = await hub.Client.GetFromJsonAsync<JsonElement>("/api/tags");
        Assert.Contains(
            tags.GetProperty("models").EnumerateArray(),
            model => model.GetProperty("name").GetString() == "llama3");

        // The hub can say which of the two states it is, in the vendor of the fault's own terms.
        var status = await hub.Client.GetFromJsonAsync<JsonElement>("/api/status");
        var reported = status.GetProperty("nodes")[0];
        Assert.Equal("unreachable", reported.GetProperty("backendHealth").GetString());
    }

    [Fact]
    public async Task RecoveryNeedsNothingButTheNextHeartbeat()
    {
        await using var hub = await HealthHub.StartAsync();
        await using var node = await hub.ConnectNodeAsync();

        await hub.HeartbeatAsync(node, BackendHealth.Wedged);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await hub.ChatAsync()).StatusCode);

        await hub.HeartbeatAsync(node, BackendHealth.Healthy);

        // Back in rotation: the request reaches the dispatcher, which is as far as this fixture's
        // node goes. No re-registration, no reconnect, no restart of anything.
        Assert.Equal(HttpStatusCode.OK, (await hub.ChatAsync()).StatusCode);
    }

    /// <summary>
    /// 69 D5, across the wire this time. A node built before v3.36 sends a three-field heartbeat,
    /// which deserializes with the new field absent — and absent must never mean sick.
    /// </summary>
    [Fact]
    public async Task ANodeThatSendsTheOldThreeFieldHeartbeatIsRoutedExactlyAsBefore()
    {
        await using var hub = await HealthHub.StartAsync();
        await using var node = await hub.ConnectNodeAsync();

        // Deliberately the pre-v3.36 shape: the payload has no `backend` member at all, which is
        // what a v3.35 node actually puts on the wire.
        await node.InvokeAsync("Heartbeat", new LegacyHeartbeat("node-1", DateTimeOffset.UtcNow, 0));

        Assert.Equal(HttpStatusCode.OK, (await hub.ChatAsync()).StatusCode);

        var status = await hub.Client.GetFromJsonAsync<JsonElement>("/api/status");
        var reported = status.GetProperty("nodes")[0];
        Assert.Equal(JsonValueKind.Null, reported.GetProperty("backendHealth").ValueKind);
    }

    private sealed record LegacyHeartbeat(string NodeId, DateTimeOffset Timestamp, int InFlight);

    private static async Task<string?> ErrorOf(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("error").GetString();
    }

    /// <summary>
    /// A hub composed the way the product composes one — the real registry, the real router, the
    /// real inference endpoints and a real <c>NodeHub</c> — so the only stub in the path is the
    /// thing that would otherwise need a GPU.
    /// </summary>
    private sealed class HealthHub : IAsyncDisposable
    {
        private const string Secret = "node-enrollment-secret";

        private WebApplication app = null!;

        public HttpClient Client { get; private set; } = null!;

        private string Url { get; set; } = null!;

        public static async Task<HealthHub> StartAsync()
        {
            var host = new HealthHub();

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            var registry = new NodeRegistry();

            builder.Services.AddSignalR();
            builder.Services.AddSingleton<IOptionsMonitor<ApiKeyOptions>>(
                new StaticApiKeys(new ApiKeyOptions { NodeEnrollmentSecret = Secret }));
            builder.Services.AddSingleton<NodeAuthFilter>();
            builder.Services.AddSingleton<INodeRegistry>(registry);
            builder.Services.AddSingleton<IConversationAffinity, ConversationAffinity>();
            builder.Services.AddSingleton<INodeConnectionTracker, NoConnections>();
            builder.Services.AddSingleton<IClusterMembership, SingleCoordinatorMembership>();
            builder.Services.AddSingleton(Options.Create(new RouterOptions()));
            builder.Services.AddSingleton(Options.Create(new AffinityOptions()));
            builder.Services.AddSingleton<ThroughputTracker>();
            builder.Services.AddSingleton<IRouter, Router>();
            builder.Services.AddSingleton<IDispatcher, AnsweringDispatcher>();
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
            host.app.MapStatusEndpoint("3.36.0");

            await host.app.StartAsync();
            host.Url = host.app.Urls.First();
            host.Client = new HttpClient { BaseAddress = new Uri(host.Url) };

            return host;
        }

        public async Task<HubConnection> ConnectNodeAsync()
        {
            var connection = new HubConnectionBuilder()
                .WithUrl($"{Url}/hubs/node", options =>
                {
                    options.Headers[NodeAuthFilter.EnrollmentSecretHeader] = Secret;
                })
                .Build();

            await connection.StartAsync();

            var now = DateTimeOffset.UtcNow;
            await connection.InvokeAsync(
                "Register",
                new NodeRegistration("node-1", "gpu-1", "http://localhost:11434/", "3.36.0"));
            await connection.InvokeAsync(
                "ReportModels",
                new NodeModels("node-1", [new ModelInfo("llama3", "sha256:abc", 1234)], now));

            return connection;
        }

        public Task HeartbeatAsync(HubConnection node, BackendHealth health)
            => node.InvokeAsync("Heartbeat", new Heartbeat("node-1", DateTimeOffset.UtcNow, 0, health));

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

        /// <summary>The one stub: it answers instead of holding a GPU.</summary>
        private sealed class AnsweringDispatcher : IDispatcher
        {
            public Task<InferenceResult> DispatchAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
                => Task.FromResult(InferenceResult.Succeeded(job.JobId, """{"model":"llama3","done":true}"""));

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
