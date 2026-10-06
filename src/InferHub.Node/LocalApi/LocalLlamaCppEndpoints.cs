using InferHub.Node.Tools;
using InferHub.Shared.Brio;
using InferHub.Shared.Contracts;
using InferHub.Shared.LlamaCpp;
using InferHub.Shared.OpenAi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InferHub.Node.LocalApi;

/// <summary>
/// <c>/v1/llamacpp/{operation}</c> and <c>/v1/rerank</c>, served by the node itself (phase 96, D4/D5)
/// — the hub's routes with routing deleted, on the same day (phase-41 D8).
/// </summary>
/// <remarks>
/// Each takes a <see cref="LocalConcurrencyGate"/> slot as a chat does: it is the same engine (37 D9).
/// Mapped unconditionally; a node that serves neither answers 40 D4's 503 naming the capability.
/// </remarks>
internal static class LocalLlamaCppEndpoints
{
    public static IEndpointRouteBuilder MapLocalLlamaCppEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/llamacpp/{operation}", HandlePostAsync);
        app.MapGet("/v1/llamacpp/{operation}", HandleGetAsync);
        app.MapPost("/v1/rerank", HandleRerankAsync);
        return app;
    }

    private static async Task<IResult> HandlePostAsync(
        string operation,
        HttpContext httpContext,
        ToolExecutor executor,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!LlamaCppNative.IsPost(operation))
        {
            return UnknownOperation(operation);
        }

        using var reader = new StreamReader(httpContext.Request.Body);
        var raw = await reader.ReadToEndAsync(cancellationToken);

        if (LlamaCppNative.ModelOf(raw, out var invalid) is not { } model)
        {
            return Error(400, invalid, OpenAiErrorTypes.InvalidRequest);
        }

        return await RunAsync(httpContext, executor, loggerFactory, operation, new ToolJob(Guid.NewGuid(), CapabilityKinds.LlamaCpp, model, LlamaCppNative.Payload(operation, raw)), null, cancellationToken);
    }

    private static async Task<IResult> HandleGetAsync(
        string operation,
        HttpContext httpContext,
        ToolExecutor executor,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!LlamaCppNative.IsGet(operation))
        {
            return UnknownOperation(operation);
        }

        var model = httpContext.Request.Query["model"].ToString().Trim();

        if (model.Length == 0)
        {
            return Error(400, "model is required: ?model=<name>", OpenAiErrorTypes.InvalidRequest);
        }

        return await RunAsync(httpContext, executor, loggerFactory, operation, new ToolJob(Guid.NewGuid(), CapabilityKinds.LlamaCpp, model, LlamaCppNative.Payload(operation, null)), null, cancellationToken);
    }

    private static async Task<IResult> HandleRerankAsync(
        HttpContext httpContext,
        ToolExecutor executor,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(httpContext.Request.Body);
        var raw = await reader.ReadToEndAsync(cancellationToken);

        if (RerankRequest.TryParse(raw, out var invalid) is not { } request)
        {
            return Error(400, invalid, OpenAiErrorTypes.InvalidRequest);
        }

        return await RunAsync(httpContext, executor, loggerFactory, "rerank", new ToolJob(Guid.NewGuid(), CapabilityKinds.Rerank, request.Model, request.JobPayload()), request, cancellationToken);
    }

    private static async Task<IResult> RunAsync(
        HttpContext httpContext,
        ToolExecutor executor,
        ILoggerFactory loggerFactory,
        string operation,
        ToolJob job,
        RerankRequest? rerank,
        CancellationToken cancellationToken)
    {
        if (LocalApiEndpoints.CapabilityDisabled(httpContext, job.Capability, out var refusal))
        {
            return Error(503, refusal, OpenAiErrorTypes.ApiError, code: "capability_disabled");
        }

        if (!executor.Provides(job.Capability, job.Model))
        {
            httpContext.Response.Headers.RetryAfter = ToolExecutor.CapabilityRetryAfterSeconds.ToString();

            return Error(
                503,
                $"this node does not provide '{job.Capability}' for model '{job.Model}'",
                OpenAiErrorTypes.ApiError,
                code: "capability_unavailable");
        }

        var logger = loggerFactory.CreateLogger("InferHub.Node.LocalApi.LlamaCpp");
        httpContext.Response.Headers[LocalApiEndpoints.ServedByHeader] = LocalApiEndpoints.ServedBySolo;

        return await LocalApiEndpoints.WithSlotAsync(
            httpContext,
            httpContext.RequestServices.GetService<LocalConcurrencyGate>(),
            async () =>
            {
                var outcome = LlamaCppNative.Render(await executor.RunAsync(job, cancellationToken));

                // The operation, the model and the outcome; never a body, a query or a document (rule 7).
                logger.LogInformation(
                    "llama.cpp {Operation} {JobId} ({Model}): {Status}",
                    operation,
                    job.JobId,
                    job.Model,
                    outcome.Status);

                if (outcome.IsError)
                {
                    if (outcome.RetryAfterSeconds is { } retryAfter)
                    {
                        httpContext.Response.Headers.RetryAfter = retryAfter.ToString();
                    }

                    return Error(outcome.Status, outcome.Error!, outcome.ErrorType, code: outcome.ErrorCode);
                }

                if (rerank is null)
                {
                    return Results.Text(outcome.Json!, "application/json");
                }

                return rerank.Render(outcome.Json!) is { } rendered
                    ? Results.Text(rendered, "application/json")
                    : Error(502, "the reranker did not return one score per document", OpenAiErrorTypes.ApiError);
            },
            (retryAfter, reason) => Error(
                503,
                $"node is {reason}; retry in {retryAfter}s",
                OpenAiErrorTypes.ApiError,
                code: "server_busy"),
            cancellationToken);
    }

    private static IResult UnknownOperation(string operation)
        => Error(
            404,
            $"'{operation}' is not a llama.cpp route this node passes through; it passes "
            + $"POST {string.Join(", ", LlamaCppNative.PostOperations)} and GET {string.Join(", ", LlamaCppNative.GetOperations)}",
            OpenAiErrorTypes.NotFound,
            code: "unknown_operation");

    private static IResult Error(int status, string message, string type, string? code = null)
        => Results.Json(
            OpenAiErrorEnvelope.Create(message, type, code, param: null),
            LocalApiEndpoints.JsonOptions,
            statusCode: status);
}
