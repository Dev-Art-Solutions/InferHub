using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using InferHub.Node.Vector;
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
        builder.Services.AddSingleton<CoordinatorConnection>();
        builder.Services.AddHostedService<Worker>();

        return builder;
    }
}
