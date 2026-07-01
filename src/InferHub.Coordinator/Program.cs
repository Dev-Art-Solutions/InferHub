using System.Reflection;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ApiKeyOptions>(builder.Configuration.GetSection(ApiKeyOptions.SectionName));
builder.Services.AddSignalR();
builder.Services.AddSingleton<NodeAuthFilter>();
builder.Services.Configure<DispatcherOptions>(builder.Configuration.GetSection("Dispatcher"));
builder.Services.Configure<RouterOptions>(builder.Configuration.GetSection("Router"));
builder.Services.AddSingleton<Metrics>();
builder.Services.AddSingleton<INodeRegistry, NodeRegistry>();
builder.Services.AddSingleton<IAuditLog, AuditLog>();
builder.Services.AddSingleton<IConversationAffinity, ConversationAffinity>();
builder.Services.AddSingleton<InferHub.Coordinator.Services.IRouter, Router>();
builder.Services.AddSingleton<IDispatcher, Dispatcher>();
builder.Services.AddSingleton<INodeConnectionTracker, NodeConnectionTracker>();
builder.Services.AddSingleton<IEmbeddingDispatcher, EmbeddingDispatcher>();
builder.Services.AddHostedService<NodeReaper>();

builder.Services.Configure<VectorStoreOptions>(builder.Configuration.GetSection(VectorStoreOptions.SectionName));
builder.Services.AddSingleton<IValidateOptions<VectorStoreOptions>, VectorStoreOptionsValidator>();
builder.Services.AddOptions<VectorStoreOptions>().ValidateOnStart();
var vectorStoreEnabled = builder.Configuration.GetSection(VectorStoreOptions.SectionName)
    .GetValue<bool>(nameof(VectorStoreOptions.Enabled));
if (vectorStoreEnabled)
{
    builder.Services.AddSingleton<VectorEvents>();
    builder.Services.AddSingleton<LocalVectorStore>();
    builder.Services.AddSingleton<IVectorStore>(sp => sp.GetRequiredService<LocalVectorStore>());
    builder.Services.AddSingleton<ReplicaRegistry>();
    builder.Services.AddSingleton<ReplicationCoordinator>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ReplicationCoordinator>());
    builder.Services.AddSingleton<IVectorQueryRouter, VectorQueryRouter>();
    builder.Services.AddSingleton<HealingService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<HealingService>());
}

var app = builder.Build();

var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";

app.UseMiddleware<AdminApiKeyMiddleware>();
app.UseMiddleware<BearerApiKeyMiddleware>();

// Status page is read-only; serve it from wwwroot/ and surface /status as an alias.
var defaultFiles = new DefaultFilesOptions();
defaultFiles.DefaultFileNames.Clear();
defaultFiles.DefaultFileNames.Add("status.html");
app.UseDefaultFiles(defaultFiles);
app.UseStaticFiles();

app.MapGet("/status", () => Results.Redirect("/status.html"));
app.MapGet("/console", () => Results.Redirect("/console.html"));

app.MapGet("/health", (ILogger<Program> logger) =>
{
    logger.LogInformation("Health check requested");
    return Results.Ok(new { status = "ok", version });
});

app.MapGet("/api/tags", (INodeRegistry registry, ILogger<Program> logger) =>
{
    logger.LogInformation("Model tags requested");
    return Results.Ok(new OllamaTagsResponse(registry.DistinctModels().ToArray()));
});

app.MapGet("/api/nodes", (INodeRegistry registry) =>
{
    return Results.Ok(registry.Snapshot(DateTimeOffset.UtcNow));
});

app.MapStatusEndpoint(version);
app.MapInferenceEndpoints();
app.MapAdminEndpoints();

if (vectorStoreEnabled)
{
    app.MapVectorEndpoints();
}

app.MapHub<NodeHub>("/hubs/node");

app.Run();

public partial class Program;
