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

        builder.Services.Configure<BackendOptions>(builder.Configuration.GetSection("Backend"));

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
        builder.Services.AddSingleton<INodeIdentity, FileNodeIdentity>();
        builder.Services.AddSingleton<IInferenceBackend>(services =>
        {
            var options = services.GetRequiredService<IOptions<BackendOptions>>().Value;
            var backendType = string.IsNullOrWhiteSpace(options.Type) ? "ollama" : options.Type.Trim();

            return backendType.ToLowerInvariant() switch
            {
                "ollama" => services.GetRequiredService<OllamaBackend>(),
                var type => throw new InvalidOperationException($"Unsupported inference backend '{type}'.")
            };
        });
        builder.Services.AddSingleton<OllamaBackend>();
        builder.Services.AddSingleton<InferenceExecutor>();
        builder.Services.AddSingleton<CoordinatorConnection>();
        builder.Services.AddHostedService<Worker>();

        return builder;
    }
}
