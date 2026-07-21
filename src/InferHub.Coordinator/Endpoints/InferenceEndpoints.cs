using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;
using Microsoft.AspNetCore.Http.Features;

namespace InferHub.Coordinator.Endpoints;

public static class InferenceEndpoints
{
    public const string ConversationHeader = "X-InferHub-Conversation";
    public const string RetrieveHeader = "X-InferHub-Retrieve";
    public const string RetrieveKHeader = "X-InferHub-Retrieve-K";
    public const string RetrieveModelHeader = "X-InferHub-Retrieve-Model";
    public const string RetrieveModeHeader = "X-InferHub-Retrieve-Mode";
    public const string RerankHeader = "X-InferHub-Rerank";
    public const string SourcesHeader = "X-InferHub-Sources";

    private const string GenerateKind = "generate";
    private const string ChatKind = "chat";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapInferenceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/generate", HandleGenerateAsync);
        app.MapPost("/api/chat", HandleChatAsync);
        app.MapPost("/api/embed", HandleEmbedAsync);
        app.MapPost("/api/embeddings", HandleLegacyEmbeddingsAsync);
        return app;
    }

    private static async Task<IResult> HandleEmbedAsync(
        HttpRequest httpRequest,
        IEmbeddingDispatcher embeddings,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Embed");
        var rawJson = await ReadBodyAsync(httpRequest, cancellationToken);

        try
        {
            var responseJson = await embeddings.DispatchEmbedAsync(rawJson, modelOverride: null, cancellationToken);
            return Results.Text(responseJson, "application/json");
        }
        catch (NoEmbeddingNodeException ex)
        {
            logger.LogWarning(ex, "No embedding node available for model {Model}", ex.Model);
            return Error(StatusCodes.Status404NotFound, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Embed request failed");
            return Error(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (NodeDisconnectedException ex)
        {
            logger.LogWarning(ex, "Embedding node disconnected mid-flight");
            return Error(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    private static async Task<IResult> HandleLegacyEmbeddingsAsync(
        HttpRequest httpRequest,
        IEmbeddingDispatcher embeddings,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.LegacyEmbeddings");
        var rawJson = await ReadBodyAsync(httpRequest, cancellationToken);

        var legacy = Deserialize<EmbeddingsRequest>(rawJson);
        if (string.IsNullOrWhiteSpace(legacy.Model))
        {
            return Error(StatusCodes.Status400BadRequest, "model is required");
        }
        if (string.IsNullOrWhiteSpace(legacy.Prompt))
        {
            return Error(StatusCodes.Status400BadRequest, "prompt is required");
        }

        // Translate the legacy single-string body to the modern batch shape so the
        // node-side path stays unified (one job kind, one backend method).
        var modern = new EmbedRequest
        {
            Model = legacy.Model,
            Input = JsonSerializer.SerializeToElement(legacy.Prompt),
            KeepAlive = legacy.KeepAlive
        };
        var modernJson = JsonSerializer.Serialize(modern, JsonOptions);

        try
        {
            var responseJson = await embeddings.DispatchEmbedAsync(modernJson, modelOverride: legacy.Model, cancellationToken);
            var modernResponse = JsonSerializer.Deserialize<EmbedResponse>(responseJson, JsonOptions);

            if (modernResponse is null || modernResponse.Embeddings.Count == 0)
            {
                return Error(StatusCodes.Status502BadGateway, "embed response had no vectors");
            }

            var legacyResponse = new EmbeddingsResponse { Embedding = modernResponse.Embeddings[0] };
            return Results.Json(legacyResponse, JsonOptions);
        }
        catch (NoEmbeddingNodeException ex)
        {
            logger.LogWarning(ex, "No embedding node available for model {Model}", ex.Model);
            return Error(StatusCodes.Status404NotFound, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Legacy embeddings request failed");
            return Error(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (NodeDisconnectedException ex)
        {
            logger.LogWarning(ex, "Embedding node disconnected mid-flight");
            return Error(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    private static async Task<IResult> HandleGenerateAsync(
        HttpContext httpContext,
        Services.IRouter router,
        IDispatcher dispatcher,
        IFallbackDispatcher fallback,
        Metrics metrics,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var httpRequest = httpContext.Request;
        var rawJson = await ReadBodyAsync(httpRequest, cancellationToken);
        var request = Deserialize<GenerateRequest>(rawJson);
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Generate");

        try
        {
            (rawJson, var sources) = await ApplyRetrievalAsync(
                httpContext,
                rawJson,
                retrieval => ApplyGenerateRetrievalAsync(httpContext, rawJson, request, retrieval, cancellationToken),
                cancellationToken);
            if (sources is not null)
            {
                httpContext.Response.Headers[SourcesHeader] = sources;
            }
        }
        catch (CollectionNotVisibleException ex)
        {
            return Error(StatusCodes.Status404NotFound, ex.Message);
        }
        catch (RetrievalUnavailableException ex)
        {
            logger.LogWarning(ex, "Retrieval unavailable for generate request");
            return Error(StatusCodes.Status424FailedDependency, ex.Message);
        }

        return await HandleAsync(
            httpContext,
            GenerateKind,
            rawJson,
            request.Model,
            request.Stream,
            conversationKey: null,
            router,
            dispatcher,
            fallback,
            metrics,
            logger,
            cancellationToken);
    }

    private static async Task<IResult> HandleChatAsync(
        HttpContext httpContext,
        Services.IRouter router,
        IDispatcher dispatcher,
        IFallbackDispatcher fallback,
        Metrics metrics,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var httpRequest = httpContext.Request;
        var rawJson = await ReadBodyAsync(httpRequest, cancellationToken);
        var request = Deserialize<ChatRequest>(rawJson);
        var conversationKey = ResolveConversationKey(httpRequest, request);
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.Endpoints.Chat");

        try
        {
            (rawJson, var sources) = await ApplyRetrievalAsync(
                httpContext,
                rawJson,
                retrieval => ApplyChatRetrievalAsync(httpContext, rawJson, request, retrieval, cancellationToken),
                cancellationToken);
            if (sources is not null)
            {
                httpContext.Response.Headers[SourcesHeader] = sources;
            }
        }
        catch (CollectionNotVisibleException ex)
        {
            return Error(StatusCodes.Status404NotFound, ex.Message);
        }
        catch (RetrievalUnavailableException ex)
        {
            logger.LogWarning(ex, "Retrieval unavailable for chat request");
            return Error(StatusCodes.Status424FailedDependency, ex.Message);
        }

        return await HandleAsync(
            httpContext,
            ChatKind,
            rawJson,
            request.Model,
            request.Stream,
            conversationKey,
            router,
            dispatcher,
            fallback,
            metrics,
            logger,
            cancellationToken);
    }

    private static Task<RetrievalOutcome> ApplyChatRetrievalAsync(
        HttpContext httpContext,
        string rawJson,
        ChatRequest request,
        RetrievalRequest retrieval,
        CancellationToken cancellationToken)
    {
        var pipeline = httpContext.RequestServices.GetService<RetrievalPipeline>()
            ?? throw new RetrievalUnavailableException("vector store is disabled; retrieval header cannot be honoured");
        return pipeline.AugmentChatAsync(rawJson, request, retrieval, cancellationToken);
    }

    private static Task<RetrievalOutcome> ApplyGenerateRetrievalAsync(
        HttpContext httpContext,
        string rawJson,
        GenerateRequest request,
        RetrievalRequest retrieval,
        CancellationToken cancellationToken)
    {
        var pipeline = httpContext.RequestServices.GetService<RetrievalPipeline>()
            ?? throw new RetrievalUnavailableException("vector store is disabled; retrieval header cannot be honoured");
        return pipeline.AugmentGenerateAsync(rawJson, request, retrieval, cancellationToken);
    }

    private static async Task<(string RawJson, string? Sources)> ApplyRetrievalAsync(
        HttpContext httpContext,
        string rawJson,
        Func<RetrievalRequest, Task<RetrievalOutcome>> augment,
        CancellationToken cancellationToken)
    {
        if (!TryReadRetrievalHeader(httpContext, out var retrieval))
        {
            return (rawJson, Sources: null);
        }

        var outcome = await augment(retrieval);
        if (!outcome.WasAugmented)
        {
            return (rawJson, Sources: null);
        }

        var sources = JsonSerializer.Serialize(outcome.Sources, JsonOptions);
        return (outcome.RawJson, Sources: sources);
    }

    // internal: the OpenAI surface honours the same retrieval headers, and a second parser
    // would be a second set of defaults to keep in sync.
    internal static bool TryReadRetrievalHeader(HttpContext context, out RetrievalRequest retrieval)
    {
        var request = context.Request;
        retrieval = default!;
        if (!request.Headers.TryGetValue(RetrieveHeader, out var raw))
        {
            return false;
        }

        var collection = raw.ToString().Trim();
        if (string.IsNullOrEmpty(collection))
        {
            return false;
        }

        // Inline retrieval names a collection too, so it is scoped like every other path that does
        // (phase 31). Checked here rather than in the pipeline: this is the one parser both client
        // dialects share, and the check must land before any embedding work is dispatched.
        if (!CollectionAccessPolicy.CanAccess(context, collection))
        {
            throw new CollectionNotVisibleException(collection);
        }

        int? k = null;
        if (request.Headers.TryGetValue(RetrieveKHeader, out var rawK)
            && int.TryParse(rawK.ToString(), out var parsedK)
            && parsedK > 0)
        {
            k = parsedK;
        }

        string? model = null;
        if (request.Headers.TryGetValue(RetrieveModelHeader, out var rawModel))
        {
            var value = rawModel.ToString().Trim();
            if (!string.IsNullOrEmpty(value))
            {
                model = value;
            }
        }

        // An unknown mode is a client error, not a silent fall back to vector — a caller who asked
        // for hybrid and got vector without being told would draw the wrong conclusion from the
        // results. Same for a non-boolean rerank flag.
        string? mode = null;
        if (request.Headers.TryGetValue(RetrieveModeHeader, out var rawMode))
        {
            var value = rawMode.ToString().Trim();
            if (!string.IsNullOrEmpty(value))
            {
                if (!RetrievalModes.TryParse(value, out _))
                {
                    throw new BadHttpRequestException(
                        $"invalid {RetrieveModeHeader} '{value}'; expected vector, keyword or hybrid",
                        StatusCodes.Status400BadRequest);
                }
                mode = value;
            }
        }

        bool? rerank = null;
        if (request.Headers.TryGetValue(RerankHeader, out var rawRerank))
        {
            var value = rawRerank.ToString().Trim();
            if (!string.IsNullOrEmpty(value))
            {
                if (!bool.TryParse(value, out var parsedRerank))
                {
                    throw new BadHttpRequestException(
                        $"invalid {RerankHeader} '{value}'; expected true or false",
                        StatusCodes.Status400BadRequest);
                }
                rerank = parsedRerank;
            }
        }

        retrieval = new RetrievalRequest(collection, k, model, mode, rerank);
        return true;
    }

    // Routing, failover and cloud burst live in InferenceCore; this method only renders the
    // outcome in the Ollama dialect. Keep it that way — the OpenAI surface shares the same core.
    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        string kind,
        string rawJson,
        string? model,
        bool? stream,
        string? conversationKey,
        Services.IRouter router,
        IDispatcher dispatcher,
        IFallbackDispatcher fallback,
        Metrics metrics,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var outcome = await InferenceCore.DispatchAsync(
            kind,
            rawJson,
            model,
            stream,
            conversationKey,
            InferenceCore.ClientContext.From(httpContext),
            router,
            dispatcher,
            fallback,
            metrics,
            logger,
            cancellationToken);

        if (outcome.IsError)
        {
            if (outcome.RetryAfterSeconds is { } retryAfter)
            {
                httpContext.Response.Headers.RetryAfter = retryAfter.ToString();
            }

            return Error(outcome.ErrorStatus!.Value, outcome.ErrorMessage!);
        }

        // Who answered. A request that left the fleet says so, on every response, always.
        httpContext.Response.Headers[InferenceCore.ServedByHeader] = outcome.ServedBy;

        if (outcome.Stream is { } chunks)
        {
            return new StreamingInferenceResult(chunks);
        }

        return Results.Text(outcome.ResponseJson ?? "{}", "application/json");
    }

    internal static string? ResolveConversationKey(HttpRequest httpRequest, ChatRequest request)
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
