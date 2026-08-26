using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
using InferHub.Node.Retrieval;
using InferHub.Node.Tools;
using InferHub.Shared.Ingestion;
using InferHub.Shared.Vector;
using InferHub.Node.LocalApi;
using Microsoft.AspNetCore.Builder;
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
        Assert.IsType<UpstreamBackend>(ResolveBackend("openai"));

        // The switch is case- and whitespace-tolerant; a config file with "OpenAI" is not a
        // reason to fail to boot.
        Assert.IsType<UpstreamBackend>(ResolveBackend(" OpenAI "));
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

    // ---- phase 39: the GPU report ----------------------------------------------------------

    [Fact]
    public void TheGpuReportIsAlwaysRegisteredAndDoesNoIoToBuild()
    {
        using var host = BuildNode();

        // Unlike the supervisor, this one is unconditional: every node says what it can see. A
        // node that cannot use a GPU is a supported deployment; a node that cannot *tell you* is
        // the bug the phase exists to prevent. The probe itself runs in StartAsync, so resolving
        // it — here, on a build agent with no driver — must be free.
        Assert.Contains(host.Services.GetServices<IHostedService>(), service => service is GpuReport);
    }

    [Fact]
    public void TheVectorStoreOnlyModeStartsNoInferenceProcess()
    {
        // Phase-39 D10, in DI: mode 3 works precisely because the supervisor is the only thing in
        // the bundled image that would ever start Ollama. If anything else ever acquires that
        // job, this mode silently grows a process it was chosen to avoid.
        using var host = BuildNode(
            ("Ollama:Supervisor:Enabled", "false"),
            ("LocalApi:Enabled", "true"),
            ("LocalApi:Retrieval:Enabled", "true"),
            ("Coordinator:Enabled", "false"));

        Assert.IsType<NoBackendSupervisor>(host.Services.GetRequiredService<IBackendSupervisor>());
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), service => service is OllamaSupervisor);
        Assert.Null(host.Services.GetService<IOllamaProcessControl>());
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

    // ---- phase 37: solo mode is registered only when it is on ------------------------------

    [Fact]
    public void SoloModeCostsTheDefaultNodeNothing()
    {
        using var host = BuildNode();

        // No web host, no concurrency gate, and — the part that matters — no listening socket.
        Assert.IsNotType<WebApplication>(host);
        Assert.Null(host.Services.GetService<LocalConcurrencyGate>());
        Assert.False(host.Services.GetRequiredService<IOptions<LocalApiOptions>>().Value.Enabled);
    }

    [Fact]
    public void TheSharedCompositionRootStillWorksUnderAWebHost()
    {
        // The pleasant half of D3: WebApplicationBuilder implements IHostApplicationBuilder, so
        // AddInferHubNode needed no signature change and one composition root still guards both
        // hosts. If this ever needs a second overload, that property has been lost.
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Coordinator:Url"] = "http://localhost:5080/",
            ["Ollama:Endpoint"] = "http://localhost:11434/",
            ["Node:Name"] = "test-node",
            ["LocalApi:Enabled"] = "true",
        });

        builder.AddInferHubNode();
        using var app = builder.Build();

        Assert.NotNull(app.Services.GetRequiredService<IInferenceBackend>());
        Assert.NotNull(app.Services.GetRequiredService<InferenceExecutor>());
        Assert.NotNull(app.Services.GetRequiredService<CoordinatorConnection>());
    }

    [Fact]
    public void TheConcurrencyGateExistsOnlyWhenBothSoloAndACapAreSet()
    {
        using var capped = BuildNode(("LocalApi:Enabled", "true"), ("Node:MaxConcurrency", "2"));
        Assert.NotNull(capped.Services.GetService<LocalConcurrencyGate>());

        // Unbounded means no gate object at all, not a gate nobody can exhaust: a semaphore with
        // an infinite count is still a lock every request takes.
        using var uncapped = BuildNode(("LocalApi:Enabled", "true"));
        Assert.Null(uncapped.Services.GetService<LocalConcurrencyGate>());

        // And a cap without solo mode stays advisory, exactly as it has been since phase 9.
        using var meshed = BuildNode(("Node:MaxConcurrency", "2"));
        Assert.Null(meshed.Services.GetService<LocalConcurrencyGate>());
    }

    // ---- phase 38: solo retrieval is registered only when it is on -------------------------

    /// <summary>
    /// <b>Amended in phase 44 (D3).</b> A node no longer resolves its store from DI — a coordinator
    /// can assign a corpus to a <em>running</em> node, and a singleton is constructed once. What must
    /// still be true is the thing this test was always about: a node that asked for nothing holds no
    /// corpus. So the assertion moves from "no store is registered" to "no store exists", which is
    /// the stronger of the two.
    /// </summary>
    [Fact]
    public void SoloRetrievalCostsTheDefaultNodeNothing()
    {
        using var plain = BuildNode();
        using var solo = BuildNode(("LocalApi:Enabled", "true"));

        // Retrieval is a second, separate opt-in — the phase-22 D5 / phase-36 D6 shape.
        foreach (var host in new[] { plain, solo })
        {
            var retrieval = host.Services.GetRequiredService<RetrievalHost>();

            Assert.Null(retrieval.Current);
            Assert.Null(retrieval.TryLease());

            // Nothing that would have opened a store, either: the seams around it are registered
            // and cost a dictionary entry each.
            Assert.Null(host.Services.GetService<IVectorStore>());
            Assert.Null(host.Services.GetService<RetrievalPipeline>());
            Assert.Null(host.Services.GetService<IngestionPipeline>());
        }
    }

    [Fact]
    public async Task SoloRetrievalStartsTheSharedStackAndNothingFleetShaped()
    {
        var directory = Path.Combine(Path.GetTempPath(), "inferhub-comp-" + Guid.NewGuid().ToString("N"));

        using var host = BuildNode(
            ("LocalApi:Enabled", "true"),
            ("Coordinator:Enabled", "false"),
            ("LocalApi:Retrieval:Enabled", "true"),
            ("LocalApi:Retrieval:DataDirectory", directory));

        var retrieval = host.Services.GetRequiredService<RetrievalHost>();

        try
        {
            // Composing the container must not touch the disk — the corpus opens when the hosted
            // service starts it, not when the container is built (phase-44 D3, and phase-33's rule
            // that a constructor opens no connection).
            Assert.False(Directory.Exists(directory));

            await retrieval.StartAsync(CancellationToken.None);

            using var lease = retrieval.TryLease();
            Assert.NotNull(lease);
            Assert.NotNull(lease!.Corpus.Retrieval);
            Assert.NotNull(lease.Corpus.Ingestion);
            Assert.NotNull(lease.Corpus.Documents);
            Assert.True(Directory.Exists(directory));

            // Nothing to route a vector query to, and nothing to replicate to: node replicas are a
            // fleet feature, and a node-owned corpus is never a replication target (phase-44 D1).
            Assert.IsType<NullVectorQueryRouter>(host.Services.GetRequiredService<IVectorQueryRouter>());

            // The one that has to stay true for rule 5: PdfPig is scoped to the coordinator by name,
            // so the node never registers the extractor and a PDF is a clean 415 (phase-38 D5).
            Assert.Null(host.Services.GetService<IPdfTextExtractor>());
        }
        finally
        {
            await retrieval.StopCorpusAsync(CancellationToken.None);
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    // ---- phase 41: the tool runtime is registered only when it is on -----------------------

    [Fact]
    public void ANodeThatChangesNoConfigGetsTheNoOpToolRuntimeAndSpawnsNothing()
    {
        using var host = BuildNode();

        // The seam is always there so no caller branches on the feature existing (phase-36 D8's
        // IBackendSupervisor shape) — but it is the stand-in, and nothing hosted comes with it.
        Assert.IsType<NoToolRuntime>(host.Services.GetRequiredService<IToolRuntime>());
        Assert.Null(host.Services.GetService<ProcessToolRuntime>());
        Assert.DoesNotContain(
            host.Services.GetServices<IHostedService>(),
            service => service is ProcessToolRuntime);

        // The executor is registered either way, over the stand-in. It answers every job with
        // "this node does not provide it" rather than being a null both hosts have to check.
        Assert.NotNull(host.Services.GetService<ToolExecutor>());
        Assert.Empty(host.Services.GetRequiredService<IToolRuntime>().Capabilities);
    }

    [Fact]
    public void ToolsEnabledRegistersTheRealRuntimeAsAHostedService()
    {
        using var host = BuildNode(("Tools:Enabled", "true"), ("Tools:Allowed:0", "echo"));

        Assert.IsType<ProcessToolRuntime>(host.Services.GetRequiredService<IToolRuntime>());
        Assert.Contains(
            host.Services.GetServices<IHostedService>(),
            service => service is ProcessToolRuntime);
    }

    /// <summary>
    /// Opt in twice. <c>Tools:Allowed</c> is the ceiling phase 43's coordinator can never raise, so
    /// naming tools without enabling the feature must not read as "it is on".
    /// </summary>
    [Fact]
    public void AllowingToolsWithoutEnablingThemFailsTheHostNamingBothKeys()
    {
        var failure = Assert.Throws<OptionsValidationException>(() =>
            BuildNode(("Tools:Allowed:0", "whisper")).Services
                .GetRequiredService<IOptions<ToolOptions>>().Value);

        Assert.Contains("Tools:Allowed", failure.Message);
        Assert.Contains("Tools:Enabled", failure.Message);
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

    // ---- phase 67: the Upstream: section, and the vendor types -----------------------

    /// <summary>
    /// 67 D3. The pre-67 section still binds, and a node that changes no config gets exactly the
    /// upstream it had — this is what "byte-identical" means for every deployment since v2.4.
    /// </summary>
    [Fact]
    public void TheLegacyOpenAiSectionStillConfiguresTheUpstream()
    {
        var options = ResolveUpstreamOptions(new()
        {
            ["Backend:Type"] = "openai",
            ["OpenAi:BaseUrl"] = "http://vllm:8000/v1",
            ["OpenAi:TimeoutSeconds"] = "120"
        });

        Assert.Equal("http://vllm:8000/v1", options.BaseUrl);
        Assert.Equal(120, options.TimeoutSeconds);
    }

    [Fact]
    public void TheUpstreamSectionIsWhatANewDeploymentWrites()
    {
        var options = ResolveUpstreamOptions(new()
        {
            ["Backend:Type"] = "anthropic",
            ["Upstream:MaxTokens"] = "8192",
            ["Upstream:Models:Include:0"] = "claude-opus-5"
        });

        Assert.Equal(8192, options.MaxTokens);
        Assert.Equal(["claude-opus-5"], options.Models.Include);

        // No BaseUrl written, and the vendor's own is what the client will be pointed at.
        Assert.Equal("https://api.anthropic.com/v1", options.ResolvedBaseUrl(BackendOptions.Anthropic));
    }

    [Fact]
    public void BothSectionsWrittenAndDisagreeingIsAStartupFailureNamingBoth()
    {
        // 65 D1's rule, one host over: which upstream receives a prompt is not decided by which
        // of two sections a binder happened to apply last.
        var ex = Assert.Throws<OptionsValidationException>(() => Validated(new()
        {
            ["Backend:Type"] = "openai",
            ["OpenAi:BaseUrl"] = "http://old:8000/v1",
            ["Upstream:BaseUrl"] = "http://new:8000/v1"
        }));

        Assert.Contains("OpenAi:BaseUrl", ex.Message);
        Assert.Contains("Upstream:BaseUrl", ex.Message);
    }

    [Fact]
    public void BothSectionsWrittenAndAgreeingIsFine()
    {
        // Not a conflict — a deployment mid-migration writes both and means one thing.
        using var host = Build(new()
        {
            ["Backend:Type"] = "openai",
            ["OpenAi:BaseUrl"] = "http://vllm:8000/v1",
            ["Upstream:BaseUrl"] = "http://vllm:8000/v1"
        });

        Assert.NotNull(host.Services.GetRequiredService<IOptions<UpstreamBackendOptions>>().Value);
        Assert.Equal("openai", host.Services.GetRequiredService<IInferenceBackend>().Name);
    }

    [Theory]
    [InlineData("openrouter")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    public void AVendorTypeWithNoAllowlistRefusesToBootAndNamesTheKey(string type)
    {
        // 67 D5. OpenRouter lists 419 ids and Gemini around fifty; a node that reported the
        // catalogue would be telling the hub it can chat with an image model.
        var ex = Assert.Throws<OptionsValidationException>(() => Validated(new()
        {
            ["Backend:Type"] = type
        }));

        Assert.Contains("Models:Include", ex.Message);
    }

    [Fact]
    public void TheNodeWideModelFilterCountsAsTheAllowlist()
    {
        // Node:Models is the filter this node has had since phase 9, and naming the models there
        // is just as explicit as naming them under Upstream:.
        using var host = Build(new()
        {
            ["Backend:Type"] = "gemini",
            ["Node:Models:Include:0"] = "models/gemini-2.5-flash"
        });

        Assert.NotNull(host.Services.GetRequiredService<IOptions<UpstreamBackendOptions>>().Value);
        var backend = host.Services.GetRequiredService<IInferenceBackend>();
        Assert.Equal("gemini", backend.Name);
        Assert.Equal(["chat", "embed"], backend.Kinds);
    }

    [Fact]
    public void AnOpenAiNodeWithNoAllowlistStillBoots()
    {
        // Deliberately not held to D5: it is usually one vLLM serving one model, and every such
        // deployment since v2.4 has an empty allowlist.
        using var host = Build(new()
        {
            ["Backend:Type"] = "openai",
            ["OpenAi:BaseUrl"] = "http://vllm:8000/v1"
        });

        Assert.NotNull(host.Services.GetRequiredService<IOptions<UpstreamBackendOptions>>().Value);
        Assert.Equal("openai", host.Services.GetRequiredService<IInferenceBackend>().Name);
    }

    [Fact]
    public void AnAnthropicNodeDeclaresChatAndNotEmbedThroughTheComposedBackend()
    {
        using var host = Build(new()
        {
            ["Backend:Type"] = "anthropic",
            ["Upstream:Models:Include:0"] = "claude-opus-5"
        });

        Assert.NotNull(host.Services.GetRequiredService<IOptions<UpstreamBackendOptions>>().Value);
        Assert.Equal(["chat"], host.Services.GetRequiredService<IInferenceBackend>().Kinds);
    }


    /// <summary>
    /// Builds the host <b>and forces the upstream options to validate</b>. <c>ValidateOnStart</c>
    /// runs at host start, so a test that only builds asserts nothing — the house pattern is to
    /// resolve the value.
    /// </summary>
    private static UpstreamBackendOptions Validated(Dictionary<string, string?> settings)
        => Build(settings).Services.GetRequiredService<IOptions<UpstreamBackendOptions>>().Value;

    private static UpstreamBackendOptions ResolveUpstreamOptions(Dictionary<string, string?> settings)
    {
        using var host = Build(settings);
        return host.Services.GetRequiredService<IOptions<UpstreamBackendOptions>>().Value;
    }

    private static IHost Build(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder();

        settings["Coordinator:Url"] = "http://localhost:5080/";
        settings["Ollama:Endpoint"] = "http://localhost:11434/";

        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddInferHubNode();

        return builder.Build();
    }
}
