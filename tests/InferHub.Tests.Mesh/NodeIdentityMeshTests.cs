using System.Net.Http.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 127 across real SignalR and a real HTTP enrollment: a coordinator that requires node certificates, a node that
/// enrolls with a one-time token and connects with its own key, one revoked and dropped at once, and a node with only the
/// shared secret refused.
/// </summary>
public sealed class NodeIdentityMeshTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-identity-mesh-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ANodeEnrollsWithAOneTimeTokenAndConnectsWithItsOwnCertificate()
    {
        await using var mesh = await Start(token: true, certificate: true);
        var authority = mesh.Services.GetRequiredService<NodeCertificateAuthority>();

        Assert.True(mesh.Credential!.HasCertificate);
        var issued = Assert.Single(authority.Certificates());
        Assert.Equal(ConcurrentJobsMeshTests.JobMesh.NodeId, issued.NodeId);
        Assert.Equal("active", issued.State(DateTimeOffset.UtcNow));

        // It runs work like any node.
        mesh.Backend.Release();
        var result = await mesh.Dispatcher.DispatchAsync(
            mesh.Node(),
            new InferHub.Shared.Contracts.InferenceJob(Guid.NewGuid(), "chat", """{"model":"llama3","messages":[]}"""),
            CancellationToken.None);
        Assert.True(result.Success);

        // A dropped connection comes back on its own, with a fresh proof: the old nonce would be refused.
        var connection = mesh.Node().ConnectionId;
        Assert.True(mesh.Services.GetRequiredService<INodeConnectionTracker>().Abort(connection));
        await mesh.WaitForAsync(() => mesh.Registry.FindConnectionIdByNodeId(ConcurrentJobsMeshTests.JobMesh.NodeId) is { } again && again != connection);
        Assert.Equal(0, authority.Snapshot()!.Refused);
    }

    [Fact]
    public async Task ARevokedNodeIsDroppedAtOnceAndCannotComeBack()
    {
        await using var mesh = await Start(token: true, certificate: true);
        using var http = new HttpClient { BaseAddress = new Uri(mesh.Url) };

        var revoke = await http.PostAsJsonAsync($"{NodeIdentityEndpoints.AdminPath}/nodes/{ConcurrentJobsMeshTests.JobMesh.NodeId}/revoke", new { reason = "laptop stolen" });
        revoke.EnsureSuccessStatusCode();
        Assert.Contains("\"disconnected\":true", await revoke.Content.ReadAsStringAsync());

        await mesh.WaitForAsync(() => mesh.Registry.FindConnectionIdByNodeId(ConcurrentJobsMeshTests.JobMesh.NodeId) is null);

        // It keeps trying, with its certificate and the shared secret both: neither gets it back in.
        await Task.Delay(2000);
        Assert.Null(mesh.Registry.FindConnectionIdByNodeId(ConcurrentJobsMeshTests.JobMesh.NodeId));
        var snapshot = mesh.Services.GetRequiredService<NodeCertificateAuthority>().Snapshot()!;
        Assert.Equal(1, snapshot.Revoked);
        Assert.True(snapshot.Refused > 0);
    }

    [Fact]
    public async Task UnderRequireTheSharedSecretAloneIsRefused()
    {
        await using var mesh = await Start(token: false, certificate: false, wait: false);

        await Task.Delay(1500);

        Assert.Null(mesh.Registry.FindConnectionIdByNodeId(ConcurrentJobsMeshTests.JobMesh.NodeId));
        Assert.True(mesh.Services.GetRequiredService<NodeCertificateAuthority>().Snapshot()!.Refused > 0);
    }

    [Fact]
    public async Task WithoutRequireTheSharedSecretStillWorks()
    {
        await using var mesh = await Start(token: false, certificate: false, require: false);

        Assert.NotNull(mesh.Registry.FindConnectionIdByNodeId(ConcurrentJobsMeshTests.JobMesh.NodeId));
    }

    private Task<ConcurrentJobsMeshTests.JobMesh> Start(bool token, bool certificate, bool wait = true, bool require = true)
        => ConcurrentJobsMeshTests.JobMesh.StartAsync(
            services: services =>
            {
                services.AddSingleton(Options.Create(new NodeIdentityOptions { Enabled = true, Require = require, DataDirectory = directory }));
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<NodeCertificateAuthority>();
            },
            map: app => app.MapNodeIdentity(),
            enrollmentToken: token ? provider => provider.GetRequiredService<NodeCertificateAuthority>().CreateToken("mesh", "test").Token : null,
            certificate: certificate,
            waitForRegistration: wait);

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
