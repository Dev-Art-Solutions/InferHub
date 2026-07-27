using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Supervision;
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

    [Fact]
    public void BackendTypeSelectsTheImplementation()
    {
        Assert.IsType<OllamaBackend>(ResolveBackend(backendType: null));
        Assert.IsType<OllamaBackend>(ResolveBackend("ollama"));
        Assert.IsType<OpenAiBackend>(ResolveBackend("openai"));

        // The switch is case- and whitespace-tolerant; a config file with "OpenAI" is not a
        // reason to fail to boot.
        Assert.IsType<OpenAiBackend>(ResolveBackend(" OpenAI "));
    }

    [Fact]
    public void AnUnknownBackendTypeFailsLoudly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ResolveBackend("tgi-native"));

        Assert.Contains("tgi-native", ex.Message);
    }

    [Fact]
    public void TheOpenAiBackendReportsItsUpstreamRatherThanTheOllamaEndpoint()
    {
        var backend = ResolveBackend("openai", baseUrl: "http://localhost:8000/v1");

        Assert.Equal("http://localhost:8000/v1", backend.Endpoint);
    }

    // ---- phase 36: the Ollama supervisor's three-part registration guard -------------------

    [Fact]
    public void TheSupervisorIsOffByDefault()
    {
        using var host = BuildNode();

        // The default node must be indistinguishable from one built before the feature existed:
        // no hosted service, no probe client, nothing.
        Assert.IsType<NoBackendSupervisor>(host.Services.GetRequiredService<IBackendSupervisor>());
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), service => service is OllamaSupervisor);
        Assert.Null(host.Services.GetService<IOllamaProbe>());
    }

    [Fact]
    public void TheSupervisorIsRegisteredForAnEnabledLoopbackOllamaNode()
    {
        using var host = BuildNode(("Ollama:Supervisor:Enabled", "true"));

        var supervisor = Assert.IsType<OllamaSupervisor>(host.Services.GetRequiredService<IBackendSupervisor>());

        Assert.True(supervisor.IsSupervising);
        Assert.Contains(host.Services.GetServices<IHostedService>(), service => ReferenceEquals(service, supervisor));

        // Constructors do no I/O: everything above resolved against an Ollama that is not there.
        Assert.Null(supervisor.Health);
        Assert.NotNull(host.Services.GetRequiredService<IOllamaProbe>());
        Assert.NotNull(host.Services.GetRequiredService<IOllamaProcessControl>());
        Assert.NotNull(host.Services.GetRequiredService<IOllamaInstaller>());
    }

    [Fact]
    public void TheSupervisorIsNotRegisteredForAnOpenAiBackend()
    {
        // A vLLM or hosted upstream is somebody else's server; it is not ours to restart.
        using var host = BuildNode(
            ("Ollama:Supervisor:Enabled", "true"),
            ("Backend:Type", "openai"),
            ("OpenAi:BaseUrl", "http://localhost:8000/v1"));

        Assert.IsType<NoBackendSupervisor>(host.Services.GetRequiredService<IBackendSupervisor>());
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), service => service is OllamaSupervisor);
    }

    [Theory]
    [InlineData("http://ollama.internal:11434/")]
    [InlineData("http://host.docker.internal:11434/")]
    [InlineData("http://10.0.0.7:11434/")]
    public void TheSupervisorIsNotRegisteredForANonLoopbackEndpoint(string endpoint)
    {
        // A shared Ollama serving four nodes, bounced because one node's link hiccuped past the
        // probe timeout, is a four-node outage caused by the node with the worst network.
        using var host = BuildNode(
            ("Ollama:Supervisor:Enabled", "true"),
            ("Ollama:Endpoint", endpoint));

        Assert.IsType<NoBackendSupervisor>(host.Services.GetRequiredService<IBackendSupervisor>());
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), service => service is OllamaSupervisor);

        // But it does not go quiet about it: an operator who asked for supervision is told why
        // they are not getting it.
        Assert.Contains(host.Services.GetServices<IHostedService>(), service => service is OllamaSupervisorDisabledNotice);
    }

    [Theory]
    [InlineData("http://localhost:11434/")]
    [InlineData("http://127.0.0.1:11434/")]
    [InlineData("http://[::1]:11434/")]
    public void LoopbackIsRecognisedInEveryShapeItIsWrittenIn(string endpoint)
    {
        using var host = BuildNode(
            ("Ollama:Supervisor:Enabled", "true"),
            ("Ollama:Endpoint", endpoint));

        Assert.IsType<OllamaSupervisor>(host.Services.GetRequiredService<IBackendSupervisor>());
    }

    [Fact]
    public void ABadSupervisorValueFailsTheHostNamingTheKey()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => BuildNode(
            ("Ollama:Supervisor:Enabled", "true"),
            // A probe that outlives its own tick makes the consecutive-failure threshold
            // meaningless.
            ("Ollama:Supervisor:ProbeTimeout", "00:00:30")).Services
            .GetRequiredService<IOptions<OllamaSupervisorOptions>>().Value);

        Assert.Contains("Ollama:Supervisor:ProbeTimeout", ex.Message);
    }

    [Fact]
    public void ABadSupervisorValueIsIgnoredWhileTheSupervisorIsOff()
    {
        using var host = BuildNode(("Ollama:Supervisor:ProbeTimeout", "-00:00:30"));

        Assert.NotNull(host.Services.GetRequiredService<IOptions<OllamaSupervisorOptions>>().Value);
    }

    private static IHost BuildNode(params (string Key, string? Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Coordinator:Url"] = "http://localhost:5080/",
            ["Coordinator:EnrollmentSecret"] = "test-secret",
            ["Ollama:Endpoint"] = "http://localhost:11434/",
            ["Node:Name"] = "test-node",
        };

        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddInferHubNode();

        return builder.Build();
    }

    private static IInferenceBackend ResolveBackend(string? backendType, string? baseUrl = "http://localhost:8000/v1")
    {
        var builder = Host.CreateApplicationBuilder();

        var settings = new Dictionary<string, string?>
        {
            ["Coordinator:Url"] = "http://localhost:5080/",
            ["Ollama:Endpoint"] = "http://localhost:11434/",
            ["Backend:Type"] = backendType,
            ["OpenAi:BaseUrl"] = baseUrl,
        };

        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddInferHubNode();

        using var host = builder.Build();
        return host.Services.GetRequiredService<IInferenceBackend>();
    }
}
