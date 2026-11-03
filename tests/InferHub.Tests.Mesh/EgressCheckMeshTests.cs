using System.Net;
using System.Net.Sockets;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Node;
using InferHub.Node.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 128 across real SignalR: a sealed node's egress check reaches its coordinator on registration and again on every
/// check, under the id the node registered as, and the hub's scrape says how many doors are open.
/// </summary>
public sealed class EgressCheckMeshTests : IDisposable
{
    private readonly TcpListener door = new(IPAddress.Loopback, 0);

    public EgressCheckMeshTests() => door.Start();

    [Fact]
    public async Task TheNodesCheckReachesTheHubAndEachNewCheckReplacesIt()
    {
        var open = $"127.0.0.1:{((IPEndPoint)door.LocalEndpoint).Port}";
        var probes = new List<string> { open };
        var check = new EgressCheck(
            Options.Create(new NodeOptions { Egress = new EgressOptions { Probes = probes, TimeoutSeconds = 2 } }),
            new Fixed(ConcurrentJobsMeshTests.JobMesh.NodeId),
            NullLogger<EgressCheck>.Instance);
        await check.CheckAsync(CancellationToken.None);

        await using var mesh = await ConcurrentJobsMeshTests.JobMesh.StartAsync(
            services: services => services.AddSingleton<NodeEgressRegistry>(),
            egress: check);
        var registry = mesh.Services.GetRequiredService<NodeEgressRegistry>();

        await mesh.WaitForAsync(() => registry.Of(ConcurrentJobsMeshTests.JobMesh.NodeId) is { Open.Count: 1 });

        // The door closes; the next check says so without a reconnect.
        door.Stop();
        await check.CheckAsync(CancellationToken.None);
        await mesh.WaitForAsync(() => registry.Of(ConcurrentJobsMeshTests.JobMesh.NodeId) is { Closed: true });

        var scrape = PrometheusFormatter.Format(new PrometheusScrape(
            "test", new Metrics().Snapshot(DateTimeOffset.UtcNow), [], [], new QueueSnapshot(0, 0, 0, 0, 0, null), [], 0,
            Egress: registry.All()));
        Assert.Contains($"inferhub_node_egress_open{{node=\"{ConcurrentJobsMeshTests.JobMesh.NodeId}\"}} 0", scrape);
    }

    public void Dispose()
    {
        try
        {
            door.Stop();
        }
        catch (SocketException)
        {
        }
    }

    private sealed class Fixed(string id) : INodeIdentity
    {
        public string GetOrCreateNodeId() => id;
    }
}
