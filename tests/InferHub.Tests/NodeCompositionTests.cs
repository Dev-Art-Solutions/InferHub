using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using InferHub.Node.Vector;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Locks the shared composition root: the console host (InferHub.Node) and the
/// Windows-service host (InferHub.Node.WindowsService) both wire their services through
/// <see cref="NodeHostBuilderExtensions.AddInferHubNode"/>, so this test guards both paths
/// at once.
/// </summary>
public class NodeCompositionTests
{
    [Fact]
    public void AddInferHubNodeRegistersCoreServicesAndBindsOptions()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Coordinator:Url"] = "http://localhost:5080/",
            ["Coordinator:EnrollmentSecret"] = "test-secret",
            ["Ollama:Endpoint"] = "http://localhost:11434/",
            ["Node:Name"] = "test-node",
        });

        builder.AddInferHubNode();
        using var host = builder.Build();

        // Key services resolve.
        Assert.NotNull(host.Services.GetRequiredService<IInferenceBackend>());
        Assert.NotNull(host.Services.GetRequiredService<CoordinatorConnection>());
        Assert.NotNull(host.Services.GetRequiredService<INodeIdentity>());
        Assert.NotNull(host.Services.GetRequiredService<InferenceExecutor>());
        Assert.NotNull(host.Services.GetRequiredService<ReplicaStore>());

        // Worker is registered as the hosted service.
        Assert.Contains(
            host.Services.GetServices<IHostedService>(),
            service => service is Worker);

        // The three validated options bind from configuration.
        Assert.Equal("http://localhost:5080/", host.Services.GetRequiredService<IOptions<CoordinatorOptions>>().Value.Url);
        Assert.Equal("test-node", host.Services.GetRequiredService<IOptions<NodeOptions>>().Value.Name);
        Assert.Equal("http://localhost:11434/", host.Services.GetRequiredService<IOptions<OllamaOptions>>().Value.Endpoint);
    }
}
