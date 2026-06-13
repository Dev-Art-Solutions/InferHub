using InferHub.Node;
using InferHub.Node.Backends;
using Microsoft.Extensions.Options;
using OllamaClient.Extensions;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<BackendOptions>(builder.Configuration.GetSection("Backend"));
builder.Services.AddOllamaClient(cfg =>
{
    cfg.OllamaEndpoint = builder.Configuration["Ollama:Endpoint"] ?? "http://localhost:11434/";
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
builder.Services.AddSingleton<CoordinatorConnection>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
