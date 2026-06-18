using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;
using Microsoft.AspNetCore.Http.Features;

namespace InferHub.Coordinator.Endpoints;

public static class InferenceEndpoints
{
    public const string ConversationHeader = "X-InferHub-Conversation";

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
        Metrics metrics,
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
            conversationKey: null,
            router,
            dispatcher,
            metrics,
            loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Generate"),
            cancellationToken);
    }

    private static async Task<IResult> HandleChatAsync(
        HttpRequest httpRequest,
        Services.IRouter router,
        IDispatcher dispatcher,
        Metrics metrics,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var rawJson = await ReadBodyAsync(httpRequest, cancellationToken);
        var request = Deserialize<ChatRequest>(rawJson);
        var conversationKey = ResolveConversationKey(httpRequest, request);

        return await HandleAsync(
            ChatKind,
            rawJson,
            request.Model,
            request.Stream,
            conversationKey,
            router,
            dispatcher,
            metrics,
            loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Chat"),
            cancellationToken);
    }

    private static async Task<IResult> HandleAsync(
        string kind,
        string rawJson,
        string? model,
        bool? stream,
        string? conversationKey,
        Services.IRouter router,
        IDispatcher dispatcher,
        Metrics metrics,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return Error(StatusCodes.Status400BadRequest, "model is required");
        }

        var node = router.Route(model, conversationKey);

        if (node is null)
        {
            return Error(StatusCodes.Status404NotFound, $"model '{model}' not found");
        }

        if (conversationKey is not null)
        {
            logger.LogInformation(
                "Routing {Kind} for conversation {ConversationKey} to node {NodeId} ({NodeName})",
                kind,
                conversationKey,
                node.NodeId,
                node.Name);
        }

        var job = new InferenceJob(Guid.NewGuid(), kind, rawJson);

        try
        {
            return await DispatchWithFailoverAsync(
                kind,
                rawJson,
                model,
                stream,
                conversationKey,
                node,
                job,
                router,
                dispatcher,
                metrics,
                logger,
                cancellationToken);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Job {JobId} for model {Model} timed out", job.JobId, model);
            return Error(StatusCodes.Status504GatewayTimeout, "inference request timed out");
        }
        catch (NodeDisconnectedException ex)
        {
            // We're here only if failover also failed (or was impossible). Surface a clean
            // 502 — the caller hasn't received any content yet for either path because the
            // streaming dispatcher only returns its reader after the first chunk arrives.
            logger.LogWarning(ex, "Job {JobId} for model {Model} could not be dispatched", job.JobId, model);
            return Error(StatusCodes.Status502BadGateway, "no node was able to handle the request");
        }
    }

    private static async Task<IResult> DispatchWithFailoverAsync(
        string kind,
        string rawJson,
        string model,
        bool? stream,
        string? conversationKey,
        RoutableNode node,
        InferenceJob job,
        Services.IRouter router,
        IDispatcher dispatcher,
        Metrics metrics,
        ILogger logger,
        CancellationToken cancellationToken)
    {
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
        catch (NodeDisconnectedException ex)
        {
            metrics.RecordFailoverAttempted();
            logger.LogWarning(
                "Node {NodeId} dropped before the job started — attempting failover",
                node.NodeId);

            var retryNode = router.Route(model, conversationKey, excludeConnectionId: ex.ConnectionId);

            if (retryNode is null)
            {
                logger.LogWarning(
                    "No alternate node available for failover of job {JobId} (model {Model})",
                    job.JobId,
                    model);
                throw;
            }

            // Issue a fresh job id so the dispatcher's pending tables stay coherent.
            var retryJob = job with { JobId = Guid.NewGuid() };

            logger.LogInformation(
                "Failing over {Kind} job {JobId} -> {NewJobId} to node {NodeId} ({NodeName})",
                kind,
                job.JobId,
                retryJob.JobId,
                retryNode.NodeId,
                retryNode.Name);

            if (stream is not false)
            {
                var chunks = await dispatcher.DispatchStreamAsync(retryNode, retryJob, cancellationToken);
                metrics.RecordFailoverSucceeded();
                return new StreamingInferenceResult(chunks);
            }

            var result = await dispatcher.DispatchAsync(retryNode, retryJob, cancellationToken);

            if (!result.Success)
            {
                metrics.RecordFailoverSucceeded();
                return Error(StatusCodes.Status502BadGateway, result.Error ?? "node failed to run inference");
            }

            metrics.RecordFailoverSucceeded();
            return Results.Text(result.ResponseJson ?? "{}", "application/json");
        }
    }

    private static string? ResolveConversationKey(HttpRequest httpRequest, ChatRequest request)
    {
        if (httpRequest.Headers.TryGetValue(ConversationHeader, out var header))
        {
            var value = header.ToString().Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return "h:" + value;
            }
        }

        return DeriveConversationKey(request.Messages);
    }

    internal static string? DeriveConversationKey(IReadOnlyList<ChatMessage>? messages)
    {
        if (messages is null || messages.Count == 0)
        {
            return null;
        }

        var firstSystem = messages.FirstOrDefault(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));
        var firstUser = messages.FirstOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));

        if (firstSystem is null && firstUser is null)
        {
            return null;
        }

        var seed = string.Concat(
            "system:",
            firstSystem?.Content ?? string.Empty,
            "\n\nuser:",
            firstUser?.Content ?? string.Empty);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return "a:" + Convert.ToHexString(hash, 0, 16);
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

            try
            {
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
            catch (NodeDisconnectedException ex)
            {
                // Stream started, then the node went away — emit a final error chunk so the
                // client doesn't hang waiting for the rest of the response.
                await WriteErrorChunkAsync(httpContext, ex.Message);
            }
            catch (TimeoutException ex)
            {
                await WriteErrorChunkAsync(httpContext, ex.Message);
            }
        }

        private static async Task WriteErrorChunkAsync(HttpContext httpContext, string message)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new { error = message, done = true }, JsonOptions);
                await httpContext.Response.WriteAsync(payload, httpContext.RequestAborted);
                await httpContext.Response.WriteAsync("\n", httpContext.RequestAborted);
                await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
            }
            catch (Exception)
            {
                // Client likely walked away — nothing useful to do here.
            }
        }
    }
}
