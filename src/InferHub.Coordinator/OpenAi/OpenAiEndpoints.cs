using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Ollama;
using InferHub.Shared.OpenAi;

namespace InferHub.Coordinator.OpenAi;

/// <summary>
/// The OpenAI-compatible edge. Every handler is the same three moves: translate the request
/// into an Ollama body, hand it to the shared <see cref="InferenceCore"/>, and render the
/// outcome in the OpenAI dialect. No routing, failover or retrieval logic lives here.
/// </summary>
public static class OpenAiEndpoints
{
    private const string GenerateKind = "generate";
    private const string ChatKind = "chat";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapOpenAiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat/completions", HandleChatCompletionsAsync);
        app.MapPost("/v1/completions", HandleCompletionsAsync);
        app.MapPost("/v1/embeddings", HandleEmbeddingsAsync);
        app.MapGet("/v1/models", HandleListModels);
        app.MapGet("/v1/models/{id}", HandleGetModel);
        return app;
    }

    private static async Task<IResult> HandleChatCompletionsAsync(
        HttpContext httpContext,
        Services.IRouter router,
        IDispatcher dispatcher,
        IFallbackDispatcher fallback,
        Metrics metrics,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.OpenAi.ChatCompletions");
        metrics.RecordOpenAiRequest();

        ChatCompletionRequest request;
        string ollamaJson;

        try
        {
            request = await ReadRequestAsync<ChatCompletionRequest>(httpContext.Request, cancellationToken);
            LogIgnoredFields(request.AdditionalProperties, request.User, logger);
            ollamaJson = RequestTranslator.ToOllamaChat(request);
        }
        catch (OpenAiRequestException ex)
        {
            return Error(ex);
        }

        var model = request.Model!;
        var stream = request.Stream ?? false;

        // The conversation key is derived from the translated Ollama body, so /v1 and /api
        // land on the same node for the same conversation.
        var ollamaRequest = JsonSerializer.Deserialize<ChatRequest>(ollamaJson, JsonOptions)!;
        var conversationKey = InferenceEndpoints.ResolveConversationKey(httpContext.Request, ollamaRequest);

        try
        {
            (ollamaJson, var sources) = await ApplyRetrievalAsync(
                httpContext,
                ollamaJson,
                ollamaRequest,
                cancellationToken);

            if (sources is not null)
            {
                httpContext.Response.Headers[InferenceEndpoints.SourcesHeader] = sources;
            }
        }
        catch (CollectionNotVisibleException ex)
        {
            // Same 404 the Ollama surface returns, in this dialect's envelope. Not a passthrough:
            // answering without the context the caller asked for, silently, is the wrong failure on
            // a tenancy boundary.
            return Error(new OpenAiRequestException(
                ex.Message,
                StatusCodes.Status404NotFound,
                OpenAiErrorTypes.NotFound,
                code: "collection_not_found"));
        }
        catch (RetrievalUnavailableException ex)
        {
            logger.LogWarning(ex, "Retrieval unavailable for OpenAI chat request");
            return Error(new OpenAiRequestException(
                ex.Message,
                StatusCodes.Status424FailedDependency,
                OpenAiErrorTypes.ApiError,
                code: "retrieval_unavailable"));
        }

        var outcome = await InferenceCore.DispatchAsync(
            ChatKind,
            ollamaJson,
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
            return DispatchError(httpContext, outcome);
        }

        httpContext.Response.Headers[InferenceCore.ServedByHeader] = outcome.ServedBy;

        var id = ResponseTranslator.NewCompletionId();
        var created = ResponseTranslator.UnixNow();

        if (outcome.Stream is { } chunks)
        {
            var includeUsage = request.StreamOptions?.IncludeUsage ?? false;
            return new OpenAiStreamingResult(
                chunks,
                new ChatStreamFormatter(id, created, model, includeUsage),
                logger);
        }

        var ollama = ResponseTranslator.ParseChat(outcome.ResponseJson ?? "{}");
        if (ollama is null)
        {
            return Error(new OpenAiRequestException(
                "node returned an unreadable response",
                StatusCodes.Status502BadGateway,
                OpenAiErrorTypes.ApiError));
        }

        return Results.Json(
            ResponseTranslator.ToChatCompletion(ollama, id, created, model),
            JsonOptions);
    }

    private static async Task<IResult> HandleCompletionsAsync(
        HttpContext httpContext,
        Services.IRouter router,
        IDispatcher dispatcher,
        IFallbackDispatcher fallback,
        Metrics metrics,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.OpenAi.Completions");
        metrics.RecordOpenAiRequest();

        CompletionRequest request;
        string ollamaJson;

        try
        {
            request = await ReadRequestAsync<CompletionRequest>(httpContext.Request, cancellationToken);
            LogIgnoredFields(request.AdditionalProperties, request.User, logger);
            ollamaJson = RequestTranslator.ToOllamaGenerate(request);
        }
        catch (OpenAiRequestException ex)
        {
            return Error(ex);
        }

        var model = request.Model!;
        var stream = request.Stream ?? false;

        var outcome = await InferenceCore.DispatchAsync(
            GenerateKind,
            ollamaJson,
            model,
            stream,
            conversationKey: null,
            InferenceCore.ClientContext.From(httpContext),
            router,
            dispatcher,
            fallback,
            metrics,
            logger,
            cancellationToken);

        if (outcome.IsError)
        {
            return DispatchError(httpContext, outcome);
        }

        httpContext.Response.Headers[InferenceCore.ServedByHeader] = outcome.ServedBy;

        var id = ResponseTranslator.NewLegacyCompletionId();
        var created = ResponseTranslator.UnixNow();

        if (outcome.Stream is { } chunks)
        {
            var includeUsage = request.StreamOptions?.IncludeUsage ?? false;
            return new OpenAiStreamingResult(
                chunks,
                new CompletionStreamFormatter(id, created, model, includeUsage),
                logger);
        }

        var ollama = ResponseTranslator.ParseGenerate(outcome.ResponseJson ?? "{}");
        if (ollama is null)
        {
            return Error(new OpenAiRequestException(
                "node returned an unreadable response",
                StatusCodes.Status502BadGateway,
                OpenAiErrorTypes.ApiError));
        }

        return Results.Json(
            ResponseTranslator.ToCompletion(ollama, id, created, model),
            JsonOptions);
    }

    private static async Task<IResult> HandleEmbeddingsAsync(
        HttpContext httpContext,
        IEmbeddingDispatcher embeddings,
        Metrics metrics,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.OpenAi.Embeddings");
        metrics.RecordOpenAiRequest();

        OpenAiEmbeddingsRequest request;
        string ollamaJson;

        try
        {
            request = await ReadRequestAsync<OpenAiEmbeddingsRequest>(httpContext.Request, cancellationToken);
            ollamaJson = RequestTranslator.ToOllamaEmbed(request);
        }
        catch (OpenAiRequestException ex)
        {
            return Error(ex);
        }

        // The Python SDK asks for base64 unless told otherwise, so this branch is the
        // common one rather than the exotic one.
        var base64 = string.Equals(request.EncodingFormat, "base64", StringComparison.OrdinalIgnoreCase);

        try
        {
            var responseJson = await embeddings.DispatchEmbedAsync(ollamaJson, request.Model, cancellationToken);
            var ollama = JsonSerializer.Deserialize<EmbedResponse>(responseJson, JsonOptions);

            if (ollama is null || ollama.Embeddings.Count == 0)
            {
                return Error(new OpenAiRequestException(
                    "embed response had no vectors",
                    StatusCodes.Status502BadGateway,
                    OpenAiErrorTypes.ApiError));
            }

            return Results.Json(
                ResponseTranslator.ToEmbeddings(ollama, request.Model!, base64),
                JsonOptions);
        }
        catch (NoEmbeddingNodeException ex)
        {
            logger.LogWarning(ex, "No embedding node available for model {Model}", ex.Model);
            return Error(new OpenAiRequestException(
                ex.Message,
                StatusCodes.Status404NotFound,
                OpenAiErrorTypes.NotFound,
                param: "model",
                code: "model_not_found"));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "OpenAI embeddings request failed");
            return Error(new OpenAiRequestException(ex.Message));
        }
        catch (NodeDisconnectedException ex)
        {
            logger.LogWarning(ex, "Embedding node disconnected mid-flight");
            return Error(new OpenAiRequestException(
                ex.Message,
                StatusCodes.Status502BadGateway,
                OpenAiErrorTypes.ApiError));
        }
    }

    private static IResult HandleListModels(INodeRegistry registry, Metrics metrics)
    {
        metrics.RecordOpenAiRequest();

        var created = ResponseTranslator.UnixNow();
        var models = registry.DistinctModels()
            .Select(model => new OpenAiModel(model.Name, created, "inferhub"))
            .ToArray();

        return Results.Json(new ModelList(models), JsonOptions);
    }

    private static IResult HandleGetModel(string id, INodeRegistry registry, Metrics metrics)
    {
        metrics.RecordOpenAiRequest();

        var match = registry.DistinctModels()
            .FirstOrDefault(model => string.Equals(model.Name, id, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return Error(new OpenAiRequestException(
                $"model '{id}' not found",
                StatusCodes.Status404NotFound,
                OpenAiErrorTypes.NotFound,
                param: "model",
                code: "model_not_found"));
        }

        return Results.Json(new OpenAiModel(match.Name, ResponseTranslator.UnixNow(), "inferhub"), JsonOptions);
    }

    /// <summary>
    /// Wiring, not new machinery: the same pipeline the Ollama surface uses, driven by the
    /// same header. An OpenAI client sets it once via <c>default_headers</c> and gets RAG.
    /// </summary>
    private static async Task<(string RawJson, string? Sources)> ApplyRetrievalAsync(
        HttpContext httpContext,
        string ollamaJson,
        ChatRequest ollamaRequest,
        CancellationToken cancellationToken)
    {
        if (!InferenceEndpoints.TryReadRetrievalHeader(httpContext, out var retrieval))
        {
            return (ollamaJson, null);
        }

        var pipeline = httpContext.RequestServices.GetService<RetrievalPipeline>()
            ?? throw new RetrievalUnavailableException("vector store is disabled; retrieval header cannot be honoured");

        var outcome = await pipeline.AugmentChatAsync(ollamaJson, ollamaRequest, retrieval, cancellationToken);

        if (!outcome.WasAugmented)
        {
            return (ollamaJson, null);
        }

        return (outcome.RawJson, JsonSerializer.Serialize(outcome.Sources, JsonOptions));
    }

    private static async Task<T> ReadRequestAsync<T>(HttpRequest httpRequest, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(httpRequest.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new OpenAiRequestException("request body is required");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions)
                ?? throw new OpenAiRequestException("request body is required");
        }
        catch (JsonException ex)
        {
            throw new OpenAiRequestException($"invalid JSON: {ex.Message}");
        }
    }

    // Accepted-and-ignored is a contract too: say so once, at debug, rather than let a
    // caller believe logprobs did something.
    private static void LogIgnoredFields(
        Dictionary<string, JsonElement>? extras,
        string? user,
        ILogger logger)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var ignored = new List<string>();

        if (user is not null)
        {
            ignored.Add("user");
        }

        if (extras is not null)
        {
            ignored.AddRange(extras.Keys);
        }

        if (ignored.Count > 0)
        {
            logger.LogDebug("Ignoring unsupported OpenAI fields: {Fields}", string.Join(", ", ignored));
        }
    }

    private static IResult DispatchError(HttpContext httpContext, InferenceCore.DispatchOutcome outcome)
    {
        var status = outcome.ErrorStatus!.Value;
        var message = outcome.ErrorMessage!;

        if (outcome.RetryAfterSeconds is { } retryAfter)
        {
            httpContext.Response.Headers.RetryAfter = retryAfter.ToString();
        }

        var (type, code, param) = status switch
        {
            StatusCodes.Status404NotFound => (OpenAiErrorTypes.NotFound, "model_not_found", "model"),
            StatusCodes.Status400BadRequest => (OpenAiErrorTypes.InvalidRequest, null, "model"),
            // The two phase-25 rejections, in the vocabulary an OpenAI SDK retries on.
            StatusCodes.Status429TooManyRequests => (OpenAiErrorTypes.RateLimit, "rate_limit_exceeded", (string?)null),
            StatusCodes.Status402PaymentRequired => (OpenAiErrorTypes.RateLimit, "insufficient_quota", (string?)null),
            _ => (OpenAiErrorTypes.ApiError, (string?)null, (string?)null)
        };

        return Error(new OpenAiRequestException(message, status, type, param, code));
    }

    private static IResult Error(OpenAiRequestException ex)
    {
        return Results.Json(
            OpenAiErrorEnvelope.Create(ex.Message, ex.Type, ex.Code, ex.Param),
            JsonOptions,
            statusCode: ex.StatusCode);
    }
}
