using System.Text.Json;
using System.Threading.Channels;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;
using Microsoft.AspNetCore.Http.Features;

namespace InferHub.Coordinator.Endpoints;

public static class InferenceEndpoints
{
    private const string GenerateKind = "generate";
    private const string ChatKind = "chat";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapInferenceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/generate", HandleGenerateAsync);
        app.MapPost("/api/chat", HandleChatAsync);
        return app;
    }

    private static async Task<IResult> HandleGenerateAsync(
        HttpRequest httpRequest,
        Services.IRouter router,
        IDispatcher dispatcher,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var rawJson = await ReadBodyAsync(httpRequest, cancellationToken);
        var request = Deserialize<GenerateRequest>(rawJson);

        return await HandleAsync(
            GenerateKind,
            rawJson,
            request.Model,
            request.Stream,
            router,
            dispatcher,
            loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Generate"),
            cancellationToken);
    }

    private static async Task<IResult> HandleChatAsync(
        HttpRequest httpRequest,
        Services.IRouter router,
        IDispatcher dispatcher,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var rawJson = await ReadBodyAsync(httpRequest, cancellationToken);
        var request = Deserialize<ChatRequest>(rawJson);

        return await HandleAsync(
            ChatKind,
            rawJson,
            request.Model,
            request.Stream,
            router,
            dispatcher,
            loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Chat"),
            cancellationToken);
    }

    private static async Task<IResult> HandleAsync(
        string kind,
        string rawJson,
        string? model,
        bool? stream,
        Services.IRouter router,
        IDispatcher dispatcher,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return Error(StatusCodes.Status400BadRequest, "model is required");
        }

        var node = router.Route(model);

        if (node is null)
        {
            return Error(StatusCodes.Status404NotFound, $"model '{model}' not found");
        }

        var job = new InferenceJob(Guid.NewGuid(), kind, rawJson);

        try
        {
            if (stream is not false)
            {
                var chunks = await dispatcher.DispatchStreamAsync(node, job, cancellationToken);
                return new StreamingInferenceResult(chunks);
            }

            var result = await dispatcher.DispatchAsync(node, job, cancellationToken);

            if (!result.Success)
            {
                return Error(StatusCodes.Status502BadGateway, result.Error ?? "node failed to run inference");
            }

            return Results.Text(result.ResponseJson ?? "{}", "application/json");
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Job {JobId} for model {Model} timed out", job.JobId, model);
            return Error(StatusCodes.Status504GatewayTimeout, "inference request timed out");
        }
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new BadHttpRequestException("request body is required", StatusCodes.Status400BadRequest);
        }

        return body;
    }

    private static T Deserialize<T>(string rawJson)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(rawJson, JsonOptions)
                ?? throw new BadHttpRequestException("request body is required", StatusCodes.Status400BadRequest);
        }
        catch (JsonException ex)
        {
            throw new BadHttpRequestException($"invalid JSON: {ex.Message}", StatusCodes.Status400BadRequest);
        }
    }

    private static IResult Error(int statusCode, string message)
    {
        return Results.Json(new { error = message }, statusCode: statusCode);
    }

    private sealed class StreamingInferenceResult(ChannelReader<InferenceChunk> chunks) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            httpContext.Response.ContentType = "application/x-ndjson";

            await foreach (var chunk in chunks.ReadAllAsync(httpContext.RequestAborted))
            {
                await httpContext.Response.WriteAsync(chunk.ResponseJson, httpContext.RequestAborted);
                await httpContext.Response.WriteAsync("\n", httpContext.RequestAborted);
                await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);

                if (chunk.Done)
                {
                    break;
                }
            }
        }
    }
}
