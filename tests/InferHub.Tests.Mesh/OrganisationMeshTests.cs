using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 130 across real SignalR: a node that enrolled with a token issued for an organisation belongs to that organisation,
/// whatever it says itself, and the hub records where the answer came from.
/// </summary>
public sealed class OrganisationMeshTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-org-mesh-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ACertifiedNodesOrganisationIsTheOneItsTokenWasIssuedFor()
    {
        await using var mesh = await ConcurrentJobsMeshTests.JobMesh.StartAsync(
            services: services =>
            {
                services.AddSingleton(Options.Create(new NodeIdentityOptions { Enabled = true, Require = true, DataDirectory = directory }));
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<NodeCertificateAuthority>();
                services.AddSingleton(Options.Create(new OrganisationOptions { Enabled = true }));
                services.AddSingleton<OrganisationPolicy>();
            },
            map: app => app.MapNodeIdentity(),
            enrollmentToken: provider => provider.GetRequiredService<NodeCertificateAuthority>().CreateToken("mesh", "test", "lab-b").Token,
            certificate: true);

        var node = Assert.Single(mesh.Registry.Snapshot(DateTimeOffset.UtcNow));
        Assert.Equal("lab-b", node.Organisation);
        Assert.Equal("certificate", mesh.Services.GetRequiredService<OrganisationPolicy>().SourceOf(node.NodeId));
        Assert.Equal("lab-b", Assert.Single(mesh.Services.GetRequiredService<NodeCertificateAuthority>().Certificates()).Organisation);
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
