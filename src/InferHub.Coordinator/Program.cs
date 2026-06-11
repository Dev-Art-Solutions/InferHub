using System.Reflection;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";

app.MapGet("/health", (ILogger<Program> logger) =>
{
    logger.LogInformation("Health check requested");
    return Results.Ok(new { status = "ok", version });
});

app.MapGet("/api/tags", (ILogger<Program> logger) =>
{
    logger.LogInformation("Model tags requested");
    return Results.Ok(new OllamaTagsResponse(Array.Empty<ModelInfo>()));
});

app.Run();
