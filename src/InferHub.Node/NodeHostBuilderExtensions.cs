using InferHub.Node.Backends;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
using InferHub.Node.LocalApi;
using InferHub.Node.Retrieval;
using InferHub.Node.Tools;
using InferHub.Node.Vector;
using InferHub.Shared.Ingestion;
using InferHub.Shared.Vector;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OllamaClient;
using OllamaClient.Extensions;

namespace InferHub.Node;

/// <summary>
/// Shared composition root for the node. Both the cross-platform console host
/// (<c>InferHub.Node</c>) and the Windows-service host (<c>InferHub.Node.WindowsService</c>)
/// wire their services through this one extension, so the two can never drift.
/// </summary>
public static class NodeHostBuilderExtensions
{
    public static IHostApplicationBuilder AddInferHubNode(this IHostApplicationBuilder builder)
    {
        builder.Services
            .AddOptions<CoordinatorOptions>()
            .Bind(builder.Configuration.GetSection(CoordinatorOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<CoordinatorOptions>, CoordinatorOptionsValidator>();

        builder.Services
            .AddOptions<NodeOptions>()
            .Bind(builder.Configuration.GetSection(NodeOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<NodeOptions>, NodeOptionsValidator>();

        builder.Services
            .AddOptions<OllamaOptions>()
            .Bind(builder.Configuration.GetSection(OllamaOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<OllamaOptions>, OllamaOptionsValidator>();

        builder.Services
            .AddOptions<OllamaSupervisorOptions>()
            .Bind(builder.Configuration.GetSection(OllamaSupervisorOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<OllamaSupervisorOptions>, OllamaSupervisorOptionsValidator>();

        builder.Services
            .AddOptions<LocalApiOptions>()
            .Bind(builder.Configuration.GetSection(LocalApiOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<LocalApiOptions>, LocalApiOptionsValidator>();

        builder.Services
            .AddOptions<LocalRetrievalOptions>()
            .Bind(builder.Configuration.GetSection(LocalRetrievalOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<LocalRetrievalOptions>>(
            _ => new LocalRetrievalOptionsValidator(builder.Configuration));

        builder.Services
            .AddOptions<ToolOptions>()
            .Bind(builder.Configuration.GetSection(ToolOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<ToolOptions>, ToolOptionsValidator>();

        // Phase 46. The same section name and the same class the coordinator binds
        // (InferHub.Shared.Images.ImageEdgeOptions), so a request refused on a hub is refused
        // identically here — the parity is by construction rather than by a suite catching it later.
        builder.Services
            .AddOptions<InferHub.Shared.Images.ImageEdgeOptions>()
            .Bind(builder.Configuration.GetSection(InferHub.Shared.Images.ImageEdgeOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<
            IValidateOptions<InferHub.Shared.Images.ImageEdgeOptions>,
            Configuration.ImageEdgeOptionsValidator>();

        // Phase 56. Durability is the same key, the same class and the same window on both hosts —
        // 41 D8's pattern for the fifth time, and what keeps "does a solo node keep an image longer
        // than a hub does" a question that cannot have two answers.
        builder.Services.AddSingleton<InferHub.Shared.Images.IImageJobArchive>(sp =>
            InferHub.Shared.Images.ImageJobArchives.Create(
                sp.GetRequiredService<IOptions<InferHub.Shared.Images.ImageEdgeOptions>>().Value.Jobs,
                (message, ex) => sp.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("InferHub.Images.Archive")
                    .LogWarning(ex, "{Message}", message)));

        builder.Services.Configure<BackendOptions>(builder.Configuration.GetSection(BackendOptions.SectionName));

        builder.Services
            .AddOptions<OpenAiBackendOptions>()
            .Bind(builder.Configuration.GetSection(OpenAiBackendOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<OpenAiBackendOptions>, OpenAiBackendOptionsValidator>();

        builder.Services
            .AddOptions<VectorReplicaOptions>()
            .Bind(builder.Configuration.GetSection(VectorReplicaOptions.SectionName));
        builder.Services.AddSingleton<ReplicaStore>();

        var ollamaOptions = builder.Configuration
            .GetSection(OllamaOptions.SectionName)
            .Get<OllamaOptions>() ?? new OllamaOptions();

        builder.Services.AddOllamaClient(cfg =>
        {
            cfg.OllamaEndpoint = ollamaOptions.Endpoint;
        });

        // OllamaClient resolves a *named* HttpClient, so its timeout is ours to set — and it
        // has to be, or inference inherits HttpClient's 100s default and a cold large model
        // gets cancelled long before the coordinator's 300s dispatcher timeout would fire.
        builder.Services.AddHttpClient(nameof(OllamaHttpClient), http =>
        {
            http.Timeout = ollamaOptions.RequestTimeout;
        });
        // The upstream client's timeout and auth are per-request (they come from options, which
        // can reload); the factory is here only to own the pooled handler.
        builder.Services.AddHttpClient(OpenAiBackend.HttpClientName);

        builder.Services.AddSingleton<INodeIdentity, FileNodeIdentity>();
        builder.Services.AddSingleton<IInferenceBackend>(services =>
        {
            var options = services.GetRequiredService<IOptions<BackendOptions>>().Value;

            return options.Normalized() switch
            {
                BackendOptions.Ollama => services.GetRequiredService<OllamaBackend>(),
                BackendOptions.OpenAi => services.GetRequiredService<OpenAiBackend>(),
                var type => throw new InvalidOperationException($"Unsupported inference backend '{type}'.")
            };
        });
        builder.Services.AddSingleton<OllamaBackend>();
        builder.Services.AddSingleton<OpenAiBackend>();
        builder.Services.AddSingleton<InferenceExecutor>();
        builder.Services.AddSingleton<ModelCommandExecutor>();
        // Phase 43. Always registered, and inert without a profile: its effective state starts as
        // the node's own configuration, so a fleet that defines no profile behaves exactly as v3.10.
        builder.Services.AddSingleton<Profiles.NodeProfileApplier>();
        builder.Services.AddSingleton<CoordinatorConnection>();
        builder.Services.AddHostedService<Worker>();

        // Always registered, and it logs one line either way (phase 39, D6). A node that cannot
        // see a GPU is a supported deployment; a node that cannot *tell you* is the bug.
        builder.Services.AddHostedService<GpuReport>();

        AddOllamaSupervision(builder, ollamaOptions);
        AddToolRuntime(builder);
        AddRetrieval(builder);
        AddLocalApi(builder);

        return builder;
    }

    /// <summary>
    /// Phase 44. The retrieval stack, registered on every node and constructing nothing until a
    /// corpus is actually started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the shape phase 38 could not have: back then the only way to have a corpus was to boot
    /// with one, so DI built it. From v3.12 a coordinator can assign a corpus to a <em>running</em>
    /// node, so the store lives behind <see cref="RetrievalHost"/> and DI holds only the seams around
    /// it — which cost nothing on a node that never gets one.
    /// </para>
    /// <para>
    /// The three absences of phase-38 D5/D6 are unchanged and each is still a decision: no
    /// <c>IPdfTextExtractor</c> (rule 5 scopes <c>PdfPig</c> to the coordinator by name, so a PDF is a
    /// 415), no replication or healing (a node-owned corpus is never a replication target — phase-44
    /// D1), and no <c>postgres</c> (D2, refused by name).
    /// </para>
    /// </remarks>
    private static void AddRetrieval(IHostApplicationBuilder builder)
    {
        // Nothing to route a vector query to: node replicas are a fleet feature, and a node-owned
        // corpus has no replicas by construction.
        builder.Services.AddSingleton<IVectorQueryRouter, NullVectorQueryRouter>();
        builder.Services.AddSingleton<IEmbeddingDispatcher, LocalEmbeddingDispatcher>();
        builder.Services.AddSingleton<IReranker, LocalReranker>();

        // No /metrics on a node (phase-37 D5), so the pipelines' counters go nowhere. The numbers an
        // operator can act on are reported to the hub (D6) and read off /api/status.
        builder.Services.AddSingleton<IRetrievalMetrics>(NullRetrievalMetrics.Instance);

        // No IPdfTextExtractor argument: the seam refuses PDF when none is registered (phase-38 D5).
        builder.Services.AddSingleton(_ => new TextExtractor());

        builder.Services.AddSingleton<RetrievalHost>();
        builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<RetrievalHost>());
    }

    /// <summary>
    /// Phase 41. The node's second kind of engine: supervised subprocess workers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off by default, and the seam is registered either way</b> — <c>NoToolRuntime</c> when
    /// <c>Tools:Enabled</c> is false, so nothing downstream branches on the feature existing
    /// (phase-36 D8's <c>IBackendSupervisor</c> shape). A node that changes no config on upgrading
    /// to v3.9 registers that stand-in, spawns no process, reads no manifest directory and declares
    /// no tool capability.
    /// </para>
    /// <para>
    /// <c>Tools:Allowed</c> is applied inside the runtime rather than here, on purpose: a manifest
    /// that is present but not allowed has to be <em>loaded and logged</em> so "I put the file there
    /// and nothing happened" is answerable, and a composition root that filtered it out would have
    /// nothing to report.
    /// </para>
    /// </remarks>
    private static void AddToolRuntime(IHostApplicationBuilder builder)
    {
        var tools = builder.Configuration
            .GetSection(ToolOptions.SectionName)
            .Get<ToolOptions>() ?? new ToolOptions();

        // Registered either way, so CoordinatorConnection and LocalApi hold one shape rather than a
        // nullable. Over NoToolRuntime it answers every job with "this node does not provide it".
        builder.Services.AddSingleton<ToolExecutor>();

        // Phase 47. The solo job surface, registered either way for the same reason: the routes are
        // mapped unconditionally and answer a 503 naming the capability when nothing serves images,
        // rather than a 404 that reads as "wrong URL" on a node that could serve them if configured.
        builder.Services.AddSingleton<LocalApi.LocalImageJobRunner>();
        builder.Services.AddHostedService<LocalApi.LocalImageJobSweeper>();

        if (!tools.Enabled)
        {
            builder.Services.TryAddSingleton<IToolRuntime, NoToolRuntime>();
            return;
        }

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ProcessToolRuntime>();
        builder.Services.AddSingleton<IToolRuntime>(services => services.GetRequiredService<ProcessToolRuntime>());
        builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<ProcessToolRuntime>());
    }

    /// <summary>
    /// Phase 37. Registers only what solo mode needs, and only when it is on — a node with the
    /// feature off must be the v3.4 worker exactly: no Kestrel, no listening socket, no middleware.
    /// The host <em>shape</em> is chosen by <see cref="NodeHostFactory"/>; this is the services half.
    /// </summary>
    private static void AddLocalApi(IHostApplicationBuilder builder)
    {
        var localApi = builder.Configuration
            .GetSection(LocalApiOptions.SectionName)
            .Get<LocalApiOptions>() ?? new LocalApiOptions();

        if (!localApi.Enabled)
        {
            return;
        }

        var node = builder.Configuration
            .GetSection(NodeOptions.SectionName)
            .Get<NodeOptions>() ?? new NodeOptions();

        // Unbounded means no gate object at all rather than a gate nobody can exhaust: a semaphore
        // with an infinite count is still a lock every request takes (phase-37 D9).
        if (node.MaxConcurrency is not null)
        {
            builder.Services.AddSingleton<LocalConcurrencyGate>();
        }
    }


    /// <summary>
    /// Phase 36. The supervisor is registered only when all three of these hold, and each
    /// rejection is a deliberate one:
    /// <list type="bullet">
    /// <item><c>Ollama:Supervisor:Enabled</c> — restarting a process is a side effect an operator
    /// opts into, not one they discover.</item>
    /// <item><c>Backend:Type=ollama</c> — a vLLM or hosted upstream is not ours to restart.</item>
    /// <item><c>Ollama:Endpoint</c> is loopback — a shared Ollama serving four nodes, bounced
    /// because <em>one</em> node's link hiccuped past the probe timeout, is a four-node outage
    /// caused by the node with the worst network. A process may only be restarted by something on
    /// the same machine that can see it actually wedged. (Amended in phase 39: the bundled image
    /// runs Ollama <em>inside</em> the container on <c>127.0.0.1</c>, which satisfies this gate
    /// naturally — that address is inside the network namespace, so it cannot be anyone else's
    /// server. Before phase 39 the container case was covered for free by having no local Ollama
    /// to reach at all.)</item>
    /// </list>
    /// </summary>
    private static void AddOllamaSupervision(IHostApplicationBuilder builder, OllamaOptions ollamaOptions)
    {
        var supervisorOptions = builder.Configuration
            .GetSection(OllamaSupervisorOptions.SectionName)
            .Get<OllamaSupervisorOptions>() ?? new OllamaSupervisorOptions();

        if (!supervisorOptions.Enabled)
        {
            NoSupervision(builder);
            return;
        }

        var backendType = (builder.Configuration
            .GetSection(BackendOptions.SectionName)
            .Get<BackendOptions>() ?? new BackendOptions()).Normalized();

        var reason = backendType != BackendOptions.Ollama
            ? $"{BackendOptions.SectionName}:{nameof(BackendOptions.Type)} is '{backendType}', and an OpenAI-compatible upstream is somebody else's server to restart"
            : !IsLoopback(ollamaOptions.Endpoint)
                ? $"{OllamaOptions.SectionName}:{nameof(OllamaOptions.Endpoint)} '{ollamaOptions.Endpoint}' is not loopback, and a remote or shared Ollama is not this node's to restart"
                : null;

        if (reason is not null)
        {
            builder.Services.AddSingleton<IHostedService>(services =>
                new OllamaSupervisorDisabledNotice(
                    reason,
                    services.GetRequiredService<ILogger<OllamaSupervisorDisabledNotice>>()));

            NoSupervision(builder);
            return;
        }

        builder.Services.TryAddSingleton(TimeProvider.System);

        // The probe's own client. It is NOT redundant with the inference client: that one waits
        // five minutes for a cold 70B load, and probing over it would mean a wedged Ollama takes
        // a quarter of an hour to cross a three-probe threshold.
        builder.Services.AddHttpClient(OllamaProbe.HttpClientName, http =>
        {
            http.BaseAddress = new Uri(ollamaOptions.Endpoint);
            http.Timeout = supervisorOptions.ProbeTimeout;
        })
        .ConfigurePrimaryHttpMessageHandler(() => OllamaProbe.CreateHandler(supervisorOptions.ProbeTimeout));

        builder.Services.AddHttpClient(OllamaInstaller.HttpClientName, http =>
        {
            http.Timeout = TimeSpan.FromMinutes(10);
        });

        builder.Services.AddSingleton<IOllamaProbe, OllamaProbe>();
        builder.Services.AddSingleton<IOllamaProcessControl, OllamaProcessControl>();
        builder.Services.AddSingleton<IOllamaInstaller, OllamaInstaller>();
        builder.Services.AddSingleton<OllamaSupervisor>();
        builder.Services.AddSingleton<IBackendSupervisor>(services => services.GetRequiredService<OllamaSupervisor>());
        builder.Services.AddSingleton<IHostedService>(services => services.GetRequiredService<OllamaSupervisor>());
    }

    /// <summary>The always-present stand-in, so nothing downstream has to know the feature exists.</summary>
    private static void NoSupervision(IHostApplicationBuilder builder)
        => builder.Services.TryAddSingleton<IBackendSupervisor, NoBackendSupervisor>();

    private static bool IsLoopback(string endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback;
}
