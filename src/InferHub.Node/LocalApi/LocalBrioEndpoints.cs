using InferHub.Node.Tools;
using InferHub.Shared.Brio;
using InferHub.Shared.Contracts;
using InferHub.Shared.OpenAi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InferHub.Node.LocalApi;

/// <summary>
/// <c>POST /v1/brio</c>, served by the node itself (phase 94, D2) — the hub's route with routing
/// deleted, on the same day (phase-41 D8).
/// </summary>
/// <remarks>
/// It takes a slot from the same <see cref="LocalConcurrencyGate"/> a chat does: it is the same
/// engine, and colibri admits one request per KV slot whatever the route (37 D9 — one key must not
/// mean two things). Mapped unconditionally; a node with no scorer answers 40 D4's 503 naming
/// <c>score</c>, which is better than a 404 for something this node could serve if configured to.
/// </remarks>
internal static class LocalBrioEndpoints
{
    public static IEndpointRouteBuilder MapLocalBrioEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/brio", HandleAsync);
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        ToolExecutor executor,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (LocalApiEndpoints.CapabilityDisabled(httpContext, CapabilityKinds.Score, out var refusal))
        {
            return Error(503, refusal, OpenAiErrorTypes.ApiError, code: "capability_disabled");
        }

        using var reader = new StreamReader(httpContext.Request.Body);
        var raw = await reader.ReadToEndAsync(cancellationToken);

        if (BrioRequest.TryParse(raw, out var invalid) is not { } request)
        {
            return Error(400, invalid, OpenAiErrorTypes.InvalidRequest);
        }

        if (!executor.Provides(CapabilityKinds.Score, request.Model))
        {
            httpContext.Response.Headers.RetryAfter = ToolExecutor.CapabilityRetryAfterSeconds.ToString();

            return Error(
                503,
                $"this node does not provide '{CapabilityKinds.Score}' for model '{request.Model}'",
                OpenAiErrorTypes.ApiError,
                code: "capability_unavailable");
        }

        var logger = loggerFactory.CreateLogger("InferHub.Node.LocalApi.Brio");
        httpContext.Response.Headers[LocalApiEndpoints.ServedByHeader] = LocalApiEndpoints.ServedBySolo;

        return await LocalApiEndpoints.WithSlotAsync(
            httpContext,
            httpContext.RequestServices.GetService<LocalConcurrencyGate>(),
            async () =>
            {
                var job = new ToolJob(Guid.NewGuid(), CapabilityKinds.Score, request.Model, request.Raw);
                var outcome = BrioRenderer.Render(await executor.RunAsync(job, cancellationToken));

                // The form and the count, never the state or an option (rule 7).
                logger.LogInformation(
                    "Brio {JobId} ({Model}, {Form} x{Count}): {Status}, {Tokens} tokens read",
                    job.JobId,
                    request.Model,
                    request.Form,
                    request.Count,
                    outcome.Status,
                    outcome.Tokens);

                return Render(httpContext, outcome);
            },
            (retryAfter, reason) => Error(
                503,
                $"node is {reason}; retry in {retryAfter}s",
                OpenAiErrorTypes.ApiError,
                code: "server_busy"),
            cancellationToken);
    }

    private static IResult Render(HttpContext httpContext, BrioOutcome outcome)
    {
        if (!outcome.IsError)
        {
            return Results.Text(outcome.Json!, "application/json");
        }

        if (outcome.RetryAfterSeconds is { } retryAfter)
        {
            httpContext.Response.Headers.RetryAfter = retryAfter.ToString();
        }

        return Error(outcome.Status, outcome.Error!, outcome.ErrorType, code: outcome.ErrorCode);
    }

    private static IResult Error(int status, string message, string type, string? code = null)
        => Results.Json(
            OpenAiErrorEnvelope.Create(message, type, code, param: null),
            LocalApiEndpoints.JsonOptions,
            statusCode: status);
}
