using System.Text.Json;
using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;
using InferHub.Shared.Vector;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace InferHub.Node.LocalApi;

/// <summary>
/// The Ollama dialect, served by the node itself. Same routes and same bodies as the hub's
/// <c>InferenceEndpoints</c>, minus routing, retrieval, affinity, admission and failover — none of
/// which mean anything with one backend.
/// </summary>
internal static class LocalInferenceEndpoints
{
    private const string GenerateKind = "generate";
    private const string ChatKind = "chat";

    public static IEndpointRouteBuilder MapLocalInferenceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/generate", (HttpContext ctx, CancellationToken ct) => HandleAsync(ctx, GenerateKind, ct));
        app.MapPost("/api/chat", (HttpContext ctx, CancellationToken ct) => HandleAsync(ctx, ChatKind, ct));
        app.MapPost("/api/embed", HandleEmbedAsync);
        app.MapPost("/api/embeddings", HandleLegacyEmbeddingsAsync);
        app.MapGet("/api/tags", HandleTagsAsync);
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        string kind,
        CancellationToken cancellationToken)
    {
        var services = httpContext.RequestServices;
        var executor = services.GetRequiredService<InferenceExecutor>();
        var gate = services.GetService<LocalConcurrencyGate>();

        var rawJson = await LocalApiEndpoints.ReadBodyAsync(httpContext.Request, cancellationToken);

        var stream = kind == ChatKind
            ? LocalApiEndpoints.Deserialize<ChatRequest>(rawJson).Stream ?? true
            : LocalApiEndpoints.Deserialize<GenerateRequest>(rawJson).Stream ?? true;

        try
        {
            var (augmented, sources) = await LocalApiEndpoints.ApplyRetrievalAsync(
                httpContext, kind == ChatKind, rawJson, cancellationToken);

            rawJson = augmented;

            if (sources is not null)
            {
                httpContext.Response.Headers[LocalRetrievalHeader.SourcesHeader] = sources;
            }
        }
        catch (LocalApiEndpoints.RetrievalNotEnabledException)
        {
            return Results.Json(
                new { error = LocalApiEndpoints.RetrievalRefusal },
                LocalApiEndpoints.JsonOptions,
                statusCode: StatusCodes.Status501NotImplemented);
        }
        catch (RetrievalUnavailableException ex)
        {
            return Error(StatusCodes.Status424FailedDependency, ex.Message);
        }

        httpContext.Response.Headers[LocalApiEndpoints.ServedByHeader] = LocalApiEndpoints.ServedBySolo;

        return await LocalApiEndpoints.WithSlotAsync(
            httpContext,
            gate,
            async () =>
            {
                var job = new InferenceJob(Guid.NewGuid(), kind, rawJson);

                if (stream)
                {
                    return new LocalApiEndpoints.LocalNdjsonResult(executor.StreamAsync(job, cancellationToken));
                }

                var result = await executor.RunAsync(job, cancellationToken);

                if (!result.Success)
                {
                    // The unwrapping the hub does (phase-29 D6), for the same reason and with more
                    // force: there is no coordinator between the user and Ollama here, so a raw
                    // triple-encoded refusal would land straight in their terminal.
                    return Error(StatusCodes.Status502BadGateway, NodeErrorText.Readable(result.Error));
                }

                return Results.Text(result.ResponseJson ?? "{}", "application/json");
            },
            retryAfter => Saturated(retryAfter),
            cancellationToken);
    }

    private static async Task<IResult> HandleEmbedAsync(
        HttpContext httpContext,
        InferenceExecutor executor,
        CancellationToken cancellationToken)
    {
        var rawJson = await LocalApiEndpoints.ReadBodyAsync(httpContext.Request, cancellationToken);
        httpContext.Response.Headers[LocalApiEndpoints.ServedByHeader] = LocalApiEndpoints.ServedBySolo;

        var result = await executor.RunAsync(new InferenceJob(Guid.NewGuid(), "embed", rawJson), cancellationToken);

        return result.Success
            ? Results.Text(result.ResponseJson ?? "{}", "application/json")
            : Error(StatusCodes.Status502BadGateway, NodeErrorText.Readable(result.Error));
    }

    private static async Task<IResult> HandleLegacyEmbeddingsAsync(
        HttpContext httpContext,
        InferenceExecutor executor,
        CancellationToken cancellationToken)
    {
        var rawJson = await LocalApiEndpoints.ReadBodyAsync(httpContext.Request, cancellationToken);
        var legacy = LocalApiEndpoints.Deserialize<EmbeddingsRequest>(rawJson);

        if (string.IsNullOrWhiteSpace(legacy.Model))
        {
            return Error(StatusCodes.Status400BadRequest, "model is required");
        }

        if (string.IsNullOrWhiteSpace(legacy.Prompt))
        {
            return Error(StatusCodes.Status400BadRequest, "prompt is required");
        }

        // Same translation the hub does: the legacy single-string body becomes the modern batch
        // shape so there is one backend method rather than two.
        var modern = new EmbedRequest
        {
            Model = legacy.Model,
            Input = JsonSerializer.SerializeToElement(legacy.Prompt),
            KeepAlive = legacy.KeepAlive
        };

        httpContext.Response.Headers[LocalApiEndpoints.ServedByHeader] = LocalApiEndpoints.ServedBySolo;

        var result = await executor.RunAsync(
            new InferenceJob(Guid.NewGuid(), "embed", JsonSerializer.Serialize(modern, LocalApiEndpoints.JsonOptions)),
            cancellationToken);

        if (!result.Success)
        {
            return Error(StatusCodes.Status502BadGateway, NodeErrorText.Readable(result.Error));
        }

        var modernResponse = JsonSerializer.Deserialize<EmbedResponse>(
            result.ResponseJson ?? "{}",
            LocalApiEndpoints.JsonOptions);

        if (modernResponse is null || modernResponse.Embeddings.Count == 0)
        {
            return Error(StatusCodes.Status502BadGateway, "embed response had no vectors");
        }

        return Results.Json(
            new EmbeddingsResponse { Embedding = modernResponse.Embeddings[0] },
            LocalApiEndpoints.JsonOptions);
    }

    private static async Task<IResult> HandleTagsAsync(
        IInferenceBackend backend,
        IOptions<NodeOptions> nodeOptions,
        CancellationToken cancellationToken)
    {
        var models = await backend.ListModelsAsync(cancellationToken);
        var visible = LocalApiEndpoints.VisibleModels(models, nodeOptions.Value);

        // Node:Models:Include/Exclude is what this node advertises to a hub, so honouring it here
        // too means the same config produces the same catalogue in both deployment shapes.
        return Results.Json(new OllamaTagsResponse(visible), LocalApiEndpoints.JsonOptions);
    }

    private static IResult Saturated(int retryAfterSeconds)
        => Results.Json(
            new { error = $"node is at its configured concurrency limit; retry in {retryAfterSeconds}s" },
            LocalApiEndpoints.JsonOptions,
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult Error(int statusCode, string message)
        => Results.Json(new { error = message }, LocalApiEndpoints.JsonOptions, statusCode: statusCode);
}
