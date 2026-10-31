using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 125 across real SignalR: a real node registers, runs a job and leaves, a stranger with the wrong secret is
/// turned away, and the trail on the hub's disk says each of those — by node id, from where — and which node a
/// request's job went to.
/// </summary>
public sealed class AuditTrailMeshTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-audit-mesh-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TheFleetsDoorAndTheNodeAJobWentToAreOnTheTrail()
    {
        await using var mesh = await ConcurrentJobsMeshTests.JobMesh.StartAsync(services: Trail);
        var trail = mesh.Services.GetRequiredService<AuditTrail>();
        await trail.StartAsync(CancellationToken.None);

        // What the middleware does for a request, around the real dispatcher.
        var scope = AuditScope.Begin();
        mesh.Backend.Release();
        var result = await mesh.Dispatcher.DispatchAsync(
            mesh.Node(),
            new InferenceJob(Guid.NewGuid(), "chat", """{"model":"llama3","messages":[{"role":"user","content":"the secret prompt"}]}"""),
            CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal([ConcurrentJobsMeshTests.JobMesh.NodeId], scope.Close());
        Assert.Equal("llama3", scope.Model);
        Assert.Equal(CapabilityKinds.Chat, scope.Capability);

        // A stranger: the wrong enrollment secret.
        var stranger = new HubConnectionBuilder()
            .WithUrl(mesh.Url + "/hubs/node", options => options.Headers[NodeAuthFilter.EnrollmentSecretHeader] = "not-the-secret")
            .Build();
        // The hub aborts it in OnConnectedAsync; whether the client's start throws or sees a close is SignalR's timing.
        try
        {
            await stranger.StartAsync();
        }
        catch (Exception)
        {
        }

        await stranger.DisposeAsync();

        await mesh.StopNodeAsync();

        var events = await WaitForAsync(trail, records => records.Any(record => record.Event == AuditRecord.NodeDisconnected)
            && records.Any(record => record.Event == AuditRecord.NodeRefused));
        var registered = Assert.Single(events, record => record.Event == AuditRecord.NodeRegistered);
        Assert.Equal(ConcurrentJobsMeshTests.JobMesh.NodeId, registered.Node);
        Assert.Equal("node", registered.Who);
        Assert.Equal("127.0.0.1", registered.Remote);

        var refused = Assert.Single(events, record => record.Event == AuditRecord.NodeRefused);
        Assert.Equal("-", refused.Who);
        Assert.Null(refused.Node);

        var left = Assert.Single(events, record => record.Event == AuditRecord.NodeDisconnected);
        Assert.Equal(ConcurrentJobsMeshTests.JobMesh.NodeId, left.Node);

        // Newest first, and a node filter finds what that node did.
        Assert.Equal(AuditRecord.NodeDisconnected, events[0].Event);
        var mine = await trail.QueryAsync(new AuditQuery(Node: ConcurrentJobsMeshTests.JobMesh.NodeId), CancellationToken.None);
        Assert.Equal(2, mine.Count);

        // Rule 7: the prompt never reached the disk.
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            Assert.DoesNotContain("secret prompt", Shared(file));
        }

        await trail.StopAsync(CancellationToken.None);
    }

    private static string Shared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void Trail(IServiceCollection services)
    {
        services.AddSingleton(Options.Create(new AuditTrailOptions { Enabled = true, DataDirectory = directory }));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AuditTrail>();
    }

    private static async Task<IReadOnlyList<AuditRecord>> WaitForAsync(AuditTrail trail, Func<IReadOnlyList<AuditRecord>, bool> predicate)
    {
        IReadOnlyList<AuditRecord> records = [];

        for (var i = 0; i < 200; i++)
        {
            records = await trail.QueryAsync(new AuditQuery(), CancellationToken.None);

            if (predicate(records))
            {
                return records;
            }

            await Task.Delay(25);
        }

        Assert.Fail("the trail never showed it: " + string.Join(", ", records.Select(record => record.Event)));
        return records;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
