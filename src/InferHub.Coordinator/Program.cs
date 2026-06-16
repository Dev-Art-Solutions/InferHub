using System.Reflection;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ApiKeyOptions>(builder.Configuration.GetSection(ApiKeyOptions.SectionName));
builder.Services.AddSignalR();
builder.Services.AddSingleton<NodeAuthFilter>();
builder.Services.Configure<DispatcherOptions>(builder.Configuration.GetSection("Dispatcher"));
builder.Services.AddSingleton<INodeRegistry, NodeRegistry>();
builder.Services.AddSingleton<InferHub.Coordinator.Services.IRouter, Router>();
builder.Services.AddSingleton<IDispatcher, Dispatcher>();
builder.Services.AddHostedService<NodeReaper>();

var app = builder.Build();

var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";

app.UseMiddleware<BearerApiKeyMiddleware>();

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

app.MapInferenceEndpoints();

app.MapHub<NodeHub>("/hubs/node");

app.Run();

public partial class Program;
