using InferHub.Node.Backends;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
using InferHub.Node.Vector;
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
        builder.Services.AddSingleton<CoordinatorConnection>();
        builder.Services.AddHostedService<Worker>();

        AddOllamaSupervision(builder, ollamaOptions);

        return builder;
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
    /// the same machine that can see it actually wedged. (This also covers the container case for
    /// free: a node image cannot restart an Ollama on its host, and its endpoint is by definition
    /// not loopback.)</item>
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
