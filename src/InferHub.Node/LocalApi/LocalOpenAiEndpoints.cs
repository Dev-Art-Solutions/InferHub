using System.Text.Json;
using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;
using InferHub.Shared.OpenAi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace InferHub.Node.LocalApi;

/// <summary>
/// The OpenAI-compatible dialect, served by the node itself — the route a client actually swaps its
/// <c>base_url</c> to.
/// </summary>
/// <remarks>
/// Every handler is the same three moves as the hub's <c>OpenAiEndpoints</c>: translate the request
/// into an Ollama body with the shared <see cref="RequestTranslator"/>, run it, and render the
/// outcome with the shared <see cref="ResponseTranslator"/> / <see cref="IOpenAiStreamFormatter"/>.
/// Nothing about the dialect is defined here, which is the whole point — a divergent
/// <c>finish_reason</c> between the two hosts is the failure this phase is most likely to ship, and
/// <c>SoloParityTests</c> exists to catch it.
/// </remarks>
internal static class LocalOpenAiEndpoints
{
    private const string GenerateKind = "generate";
    private const string ChatKind = "chat";

    public static IEndpointRouteBuilder MapLocalOpenAiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat/completions", HandleChatCompletionsAsync);
        app.MapPost("/v1/completions", HandleCompletionsAsync);
        app.MapPost("/v1/embeddings", HandleEmbeddingsAsync);
        app.MapGet("/v1/models", HandleListModelsAsync);
        app.MapGet("/v1/models/{id}", HandleGetModelAsync);
        return app;
    }

    private static async Task<IResult> HandleChatCompletionsAsync(
        HttpContext httpContext,
        InferenceExecutor executor,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Node.LocalApi.ChatCompletions");

        if (LocalApiEndpoints.AsksForRetrieval(httpContext.Request))
        {
            return Error(new OpenAiRequestException(
                LocalApiEndpoints.RetrievalRefusal,
                StatusCodes.Status501NotImplemented,
                OpenAiErrorTypes.ApiError,
                code: "retrieval_unavailable"));
        }

        ChatCompletionRequest request;
        string ollamaJson;

        try
        {
            request = await ReadRequestAsync<ChatCompletionRequest>(httpContext.Request, cancellationToken);
            ollamaJson = RequestTranslator.ToOllamaChat(request);
        }
        catch (OpenAiRequestException ex)
        {
            return Error(ex);
        }

        var model = request.Model!;
        var stream = request.Stream ?? false;

        httpContext.Response.Headers[LocalApiEndpoints.ServedByHeader] = LocalApiEndpoints.ServedBySolo;

        return await LocalApiEndpoints.WithSlotAsync(
            httpContext,
            httpContext.RequestServices.GetService<LocalConcurrencyGate>(),
            async () =>
            {
                var id = ResponseTranslator.NewCompletionId();
                var created = ResponseTranslator.UnixNow();
                var job = new InferenceJob(Guid.NewGuid(), ChatKind, ollamaJson);

                if (stream)
                {
                    var includeUsage = request.StreamOptions?.IncludeUsage ?? false;
                    return new LocalApiEndpoints.LocalSseResult(
                        executor.StreamAsync(job, cancellationToken),
                        new ChatStreamFormatter(id, created, model, includeUsage),
                        logger);
                }

                var result = await executor.RunAsync(job, cancellationToken);

                if (!result.Success)
                {
                    return Error(new OpenAiRequestException(
                        NodeErrorText.Readable(result.Error),
                        StatusCodes.Status502BadGateway,
                        OpenAiErrorTypes.ApiError));
                }

                var ollama = ResponseTranslator.ParseChat(result.ResponseJson ?? "{}");

                if (ollama is null)
                {
                    return Error(new OpenAiRequestException(
                        "backend returned an unreadable response",
                        StatusCodes.Status502BadGateway,
                        OpenAiErrorTypes.ApiError));
                }

                return Results.Json(
                    ResponseTranslator.ToChatCompletion(ollama, id, created, model),
                    LocalApiEndpoints.JsonOptions);
            },
            Saturated,
            cancellationToken);
    }

    private static async Task<IResult> HandleCompletionsAsync(
        HttpContext httpContext,
        InferenceExecutor executor,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Node.LocalApi.Completions");

        CompletionRequest request;
        string ollamaJson;

        try
        {
            request = await ReadRequestAsync<CompletionRequest>(httpContext.Request, cancellationToken);
            ollamaJson = RequestTranslator.ToOllamaGenerate(request);
        }
        catch (OpenAiRequestException ex)
        {
            return Error(ex);
        }

        var model = request.Model!;
        var stream = request.Stream ?? false;

        httpContext.Response.Headers[LocalApiEndpoints.ServedByHeader] = LocalApiEndpoints.ServedBySolo;

        return await LocalApiEndpoints.WithSlotAsync(
            httpContext,
            httpContext.RequestServices.GetService<LocalConcurrencyGate>(),
            async () =>
            {
                var id = ResponseTranslator.NewLegacyCompletionId();
                var created = ResponseTranslator.UnixNow();
                var job = new InferenceJob(Guid.NewGuid(), GenerateKind, ollamaJson);

                if (stream)
                {
                    var includeUsage = request.StreamOptions?.IncludeUsage ?? false;
                    return new LocalApiEndpoints.LocalSseResult(
                        executor.StreamAsync(job, cancellationToken),
                        new CompletionStreamFormatter(id, created, model, includeUsage),
                        logger);
                }

                var result = await executor.RunAsync(job, cancellationToken);

                if (!result.Success)
                {
                    return Error(new OpenAiRequestException(
                        NodeErrorText.Readable(result.Error),
                        StatusCodes.Status502BadGateway,
                        OpenAiErrorTypes.ApiError));
                }

                var ollama = ResponseTranslator.ParseGenerate(result.ResponseJson ?? "{}");

                if (ollama is null)
                {
                    return Error(new OpenAiRequestException(
                        "backend returned an unreadable response",
                        StatusCodes.Status502BadGateway,
                        OpenAiErrorTypes.ApiError));
                }

                return Results.Json(
                    ResponseTranslator.ToCompletion(ollama, id, created, model),
                    LocalApiEndpoints.JsonOptions);
            },
            Saturated,
            cancellationToken);
    }

    private static async Task<IResult> HandleEmbeddingsAsync(
        HttpContext httpContext,
        InferenceExecutor executor,
        CancellationToken cancellationToken)
    {
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

        // The Python SDK asks for base64 unless told otherwise, so this branch is the common one.
        var base64 = string.Equals(request.EncodingFormat, "base64", StringComparison.OrdinalIgnoreCase);

        httpContext.Response.Headers[LocalApiEndpoints.ServedByHeader] = LocalApiEndpoints.ServedBySolo;

        var result = await executor.RunAsync(
            new InferenceJob(Guid.NewGuid(), "embed", ollamaJson),
            cancellationToken);

        if (!result.Success)
        {
            return Error(new OpenAiRequestException(
                NodeErrorText.Readable(result.Error),
                StatusCodes.Status502BadGateway,
                OpenAiErrorTypes.ApiError));
        }

        var ollama = JsonSerializer.Deserialize<EmbedResponse>(
            result.ResponseJson ?? "{}",
            LocalApiEndpoints.JsonOptions);

        if (ollama is null || ollama.Embeddings.Count == 0)
        {
            return Error(new OpenAiRequestException(
                "embed response had no vectors",
                StatusCodes.Status502BadGateway,
                OpenAiErrorTypes.ApiError));
        }

        return Results.Json(
            ResponseTranslator.ToEmbeddings(ollama, request.Model!, base64),
            LocalApiEndpoints.JsonOptions);
    }

    private static async Task<IResult> HandleListModelsAsync(
        IInferenceBackend backend,
        IOptions<NodeOptions> nodeOptions,
        CancellationToken cancellationToken)
    {
        var created = ResponseTranslator.UnixNow();
        var models = LocalApiEndpoints.VisibleModels(
            await backend.ListModelsAsync(cancellationToken),
            nodeOptions.Value);

        return Results.Json(
            new ModelList([.. models.Select(model => new OpenAiModel(model.Name, created, "inferhub"))]),
            LocalApiEndpoints.JsonOptions);
    }

    private static async Task<IResult> HandleGetModelAsync(
        string id,
        IInferenceBackend backend,
        IOptions<NodeOptions> nodeOptions,
        CancellationToken cancellationToken)
    {
        var models = LocalApiEndpoints.VisibleModels(
            await backend.ListModelsAsync(cancellationToken),
            nodeOptions.Value);

        var match = models.FirstOrDefault(model => string.Equals(model.Name, id, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return Error(new OpenAiRequestException(
                $"model '{id}' not found",
                StatusCodes.Status404NotFound,
                OpenAiErrorTypes.NotFound,
                param: "model",
                code: "model_not_found"));
        }

        return Results.Json(
            new OpenAiModel(match.Name, ResponseTranslator.UnixNow(), "inferhub"),
            LocalApiEndpoints.JsonOptions);
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
            return JsonSerializer.Deserialize<T>(body, LocalApiEndpoints.JsonOptions)
                ?? throw new OpenAiRequestException("request body is required");
        }
        catch (JsonException ex)
        {
            throw new OpenAiRequestException($"invalid JSON: {ex.Message}");
        }
    }

    private static IResult Saturated(int retryAfterSeconds)
        => Error(new OpenAiRequestException(
            $"node is at its configured concurrency limit; retry in {retryAfterSeconds}s",
            StatusCodes.Status503ServiceUnavailable,
            OpenAiErrorTypes.ApiError,
            code: "server_busy"));

    private static IResult Error(OpenAiRequestException ex)
        => Results.Json(
            OpenAiErrorEnvelope.Create(ex.Message, ex.Type, ex.Code, ex.Param),
            LocalApiEndpoints.JsonOptions,
            statusCode: ex.StatusCode);
}
