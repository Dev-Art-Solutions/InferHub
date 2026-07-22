using System.Collections.Concurrent;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Cluster;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Services;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Node-side failover (phase 32), across the real SignalR wire for the same reason
/// <see cref="NodeHubStreamingTests"/> is: the handshake is a seam a stubbed hub cannot exercise,
/// and "the standby refuses the connection" is the single fact node failover rests on. If a
/// standby quietly accepted registrations, a node would sit attached to a hub that never sends it
/// a job — a node the mesh has silently lost, with nothing in any log to say so.
/// </summary>
public class FailoverTests
{
    private const string Secret = "test-enrollment-secret";

    [Fact]
    public async Task AStandbyCoordinatorRefusesTheNodeHandshake()
    {
        await using var standby = await HubHost.StartAsync(Standby());

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => standby.ConnectNodeAsync());

        // The negotiate is refused with the same 503 clients get, so the node's connect attempt
        // fails outright rather than "succeeding" into a hub that will never send it a job.
        Assert.Contains("503", failure.Message);
        Assert.Empty(standby.Registry.Registered);
    }

    [Fact]
    public async Task AnActiveCoordinatorAcceptsTheNodeAndRegistersIt()
    {
        await using var active = await HubHost.StartAsync(Active());

        await using var connection = await active.ConnectNodeAsync();
        await connection.InvokeAsync("Register", Registration("node-1"));

        Assert.Contains("node-1", active.Registry.Registered);
    }

    [Fact]
    public async Task APromotedStandbyAcceptsTheNodesThatWereRefusedBefore()
    {
        // The whole failover story in one test: refused while standby, accepted once it holds the
        // lease, with no restart and no configuration change in between.
        var membership = Standby();
        await using var hub = await HubHost.StartAsync(membership);

        await Assert.ThrowsAnyAsync<Exception>(() => hub.ConnectNodeAsync());

        membership.MarkActive(2, DateTimeOffset.UtcNow);

        await using var connection = await hub.ConnectNodeAsync();
        await connection.InvokeAsync("Register", Registration("node-1"));

        Assert.Contains("node-1", hub.Registry.Registered);
    }

    [Fact]
    public async Task DemotionDropsTheNodesSoTheyGoLookForTheNewActive()
    {
        var membership = Active();
        await using var hub = await HubHost.StartAsync(membership);

        var connection = await hub.ConnectNodeAsync();
        await connection.InvokeAsync("Register", Registration("node-1"));

        var closed = new TaskCompletionSource();
        connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };

        Assert.Equal(1, hub.Connections.AbortAll());

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HubConnectionState.Disconnected, connection.State);

        await connection.DisposeAsync();
    }

    [Fact]
    public void ANodeWithNoEndpointListBehavesExactlyAsBefore()
    {
        var options = new CoordinatorOptions { Url = "http://hub:5080/" };

        Assert.Equal(["http://hub:5080/"], options.ResolvedEndpoints());
        // One endpoint keeps SignalR's automatic reconnect, which is the right strategy when
        // there is nowhere else to go.
        Assert.False(options.HasFailoverEndpoints());
    }

    [Fact]
    public void AnEndpointListWinsOverTheSingleUrlAndKeepsItsOrder()
    {
        var options = new CoordinatorOptions
        {
            Url = "http://ignored:5080/",
            Endpoints = { "http://hub-a:5080/", "  ", "http://hub-b:5080/" }
        };

        Assert.Equal(["http://hub-a:5080/", "http://hub-b:5080/"], options.ResolvedEndpoints());
        Assert.True(options.HasFailoverEndpoints());
    }

    [Fact]
    public void AMalformedEndpointFailsStartupRatherThanSilentlyHalvingTheFailoverList()
    {
        var result = new CoordinatorOptionsValidator().Validate(null, new CoordinatorOptions
        {
            Endpoints = { "http://hub-a:5080/", "hub-b:5080" }
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("hub-b:5080"));
    }

    private static ClusterMembership Standby()
    {
        var membership = new ClusterMembership("hub-a");
        membership.MarkStandby("the lease is held by 'hub-b'");
        return membership;
    }

    private static ClusterMembership Active()
    {
        var membership = new ClusterMembership("hub-a");
        membership.MarkActive(1, DateTimeOffset.UtcNow);
        return membership;
    }

    private static NodeRegistration Registration(string nodeId) =>
        new(nodeId, nodeId, "http://localhost:11434", "test", null, null, null, true);

    /// <summary>A real coordinator host with only the node hub mapped, at a role we control.</summary>
    private sealed class HubHost : IAsyncDisposable
    {
        private WebApplication app = null!;

        public RecordingRegistry Registry { get; } = new();

        public NodeConnectionTracker Connections { get; } = new();

        public string Url { get; private set; } = null!;

        public static async Task<HubHost> StartAsync(IClusterMembership membership)
        {
            var host = new HubHost();

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            builder.Services.AddSignalR();
            builder.Services.AddSingleton<IOptionsMonitor<ApiKeyOptions>>(
                new StaticApiKeyOptions(new ApiKeyOptions { NodeEnrollmentSecret = Secret }));
            builder.Services.AddSingleton<NodeAuthFilter>();
            builder.Services.AddSingleton<IDispatcher>(new UnusedDispatcher());
            builder.Services.AddSingleton<INodeRegistry>(host.Registry);
            builder.Services.AddSingleton<INodeConnectionTracker>(host.Connections);
            builder.Services.AddSingleton(membership);

            host.app = builder.Build();
            host.app.UseMiddleware<ClusterRoleMiddleware>();
            host.app.MapHub<NodeHub>("/hubs/node");

            await host.app.StartAsync();
            host.Url = host.app.Urls.First();

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
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class RecordingRegistry : INodeRegistry
    {
        private readonly ConcurrentBag<string> registered = [];

        public IReadOnlyCollection<string> Registered => registered;

        public event Action? Changed { add { } remove { } }

        public void Upsert(string connectionId, NodeRegistration registration, DateTimeOffset now)
            => registered.Add(registration.NodeId);

        public bool Touch(string connectionId, Heartbeat heartbeat, DateTimeOffset now) => true;

        public bool ReportModels(string connectionId, NodeModels models, DateTimeOffset now) => true;

        public bool Remove(string connectionId) => false;

        public bool Cordon(string nodeId) => false;

        public bool Uncordon(string nodeId) => false;

        public string? FindConnectionIdByNodeId(string nodeId) => null;

        public IReadOnlyCollection<NodeSnapshot> Snapshot(DateTimeOffset now) => [];

        public IReadOnlyCollection<ModelInfo> DistinctModels() => [];

        public IReadOnlyCollection<NodeModelInventory> ModelInventory() => [];

        public IReadOnlyCollection<RoutableNode> FindNodesWithModel(string model) => [];

        public int IncrementInFlight(string connectionId) => 0;

        public int DecrementInFlight(string connectionId) => 0;

        public int GetLocalInFlight(string connectionId) => 0;

        public IReadOnlyCollection<NodeSnapshot> EvictStale(DateTimeOffset cutoffUtc, DateTimeOffset now) => [];
    }

    private sealed class UnusedDispatcher : IDispatcher
    {
        public Task<InferenceResult> DispatchAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<System.Threading.Channels.ChannelReader<InferenceChunk>> DispatchStreamAsync(
            RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public bool Complete(InferenceResult result) => true;

        public bool WriteChunk(InferenceChunk chunk) => true;

        public void FailForConnection(string connectionId, Exception? exception) { }
    }

    private sealed class StaticApiKeyOptions(ApiKeyOptions value) : IOptionsMonitor<ApiKeyOptions>
    {
        public ApiKeyOptions CurrentValue { get; } = value;

        public ApiKeyOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ApiKeyOptions, string?> listener) => null;
    }
}
