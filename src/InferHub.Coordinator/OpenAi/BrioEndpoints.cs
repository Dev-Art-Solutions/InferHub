using System.Text.Json;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Brio;
using InferHub.Shared.Contracts;
using InferHub.Shared.OpenAi;

namespace InferHub.Coordinator.OpenAi;

/// <summary>
/// <c>POST /v1/brio</c> (phase 94): a closed question answered with a distribution over the allowed
/// answers, by a colibri node that reads each option's log-probability instead of generating.
/// </summary>
/// <remarks>
/// <para>
/// <b>The client surface is colibri's own, byte for byte</b> (D2) — phase-42 D1's argument: a shape
/// somebody already wrote clients for beats one we designed. The edge reads the model and the form
/// and passes the body through; the engine validates the rest and its <c>400</c> comes back as a
/// <c>400</c> in its own words. The node-facing side is a <c>ToolJob</c> with capability
/// <c>score</c> (D1, 40 D3), so the dispatcher, failover and deadlines learned nothing.
/// </para>
/// <para>
/// Under <c>/v1</c>, which <c>BearerApiKeyMiddleware</c> guards (21 D2). Rule 7: the log line
/// carries the model, the form, the count and the tokens — a <c>state</c> is a document somebody
/// wanted judged, and an option is part of the question.
/// </para>
/// </remarks>
public static class BrioEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapBrioEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/brio", HandleAsync).AddEndpointFilter(DispatchDeadlineFilter.OpenAi);
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        Services.IRouter router,
        INodeRegistry registry,
        IToolDispatcher tools,
        Metrics metrics,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.OpenAi.Brio");
        metrics.RecordOpenAiRequest();

        using var reader = new StreamReader(httpContext.Request.Body);
        var raw = await reader.ReadToEndAsync(cancellationToken);

        if (BrioRequest.TryParse(raw, out var invalid) is not { } request)
        {
            return Error(400, invalid, OpenAiErrorTypes.InvalidRequest);
        }

        // Admission before routing (25 D4): a scoped-out model is the same 404 as a missing one, and
        // the 503 below can never be used to probe for a model this client cannot reach.
        var context = InferenceCore.ClientContext.From(httpContext);
        var admission = context.Admission.TryAdmit(context.Client, request.Model);

        if (!admission.Allowed)
        {
            logger.LogInformation(
                "Rejected brio for client {ClientId}: {Status} {Message}",
                context.Client.Id,
                admission.Status,
                admission.Message);

            return Rejected(httpContext, admission);
        }

        using var lease = admission.Lease;

        var node = router.Route(request.Model, capability: CapabilityKinds.Score);

        if (node is null)
        {
            return NoNode(httpContext, registry, request.Model);
        }

        var job = new ToolJob(Guid.NewGuid(), CapabilityKinds.Score, request.Model, request.Raw);
        ToolResult result;

        try
        {
            result = await tools.DispatchToolAsync(node, job, cancellationToken);
        }
        catch (NodeDisconnectedException)
        {
            return Error(
                502,
                "the node handling this request disconnected before it answered",
                OpenAiErrorTypes.ApiError,
                code: "node_lost");
        }

        var outcome = BrioRenderer.Render(result);

        logger.LogInformation(
            "Brio {JobId} ({Model}, {Form} x{Count}) on node {NodeId}: {Status}, {Tokens} tokens read",
            job.JobId,
            request.Model,
            request.Form,
            request.Count,
            node.NodeId,
            outcome.Status,
            outcome.Tokens);

        httpContext.Response.Headers[InferenceCore.ServedByHeader] = "node";

        if (outcome.IsError)
        {
            if (outcome.RetryAfterSeconds is { } retryAfter)
            {
                httpContext.Response.Headers.RetryAfter = retryAfter.ToString();
            }

            return Error(outcome.Status, outcome.Error!, outcome.ErrorType, code: outcome.ErrorCode);
        }

        // D3: everything the engine read, as prompt tokens; a failed job is never billed (42 D7).
        context.Usage.RecordTokens(context.Client, CapabilityKinds.Score, request.Model, outcome.Tokens, 0);

        return Results.Text(outcome.Json!, "application/json");
    }

    /// <summary>40 D4/D5, as the audio routes render it: fleet state is a 503, an unknown model the 404.</summary>
    private static IResult NoNode(HttpContext httpContext, INodeRegistry registry, string model)
    {
        if (ToolEndpoints.KnownToTheFleet(registry, model))
        {
            httpContext.Response.Headers.RetryAfter = ToolEndpoints.CapabilityRetryAfterSeconds.ToString();

            return Error(
                503,
                $"no node currently provides '{CapabilityKinds.Score}' for model '{model}'; " +
                "Brio needs a node with Backend:Type=colibri",
                OpenAiErrorTypes.ApiError,
                code: "capability_unavailable");
        }

        return Error(404, $"model '{model}' not found", OpenAiErrorTypes.NotFound, code: "model_not_found");
    }

    private static IResult Rejected(HttpContext httpContext, AdmissionDecision admission)
    {
        if (admission.RetryAfterSeconds is { } retryAfter)
        {
            httpContext.Response.Headers.RetryAfter = retryAfter.ToString();
        }

        var (type, code) = admission.Status switch
        {
            404 => (OpenAiErrorTypes.NotFound, "model_not_found"),
            429 => (OpenAiErrorTypes.RateLimit, "rate_limit_exceeded"),
            402 => (OpenAiErrorTypes.RateLimit, "insufficient_quota"),
            _ => (OpenAiErrorTypes.ApiError, (string?)null)
        };

        return Error(admission.Status, admission.Message!, type, code: code);
    }

    private static IResult Error(int status, string message, string type, string? code = null)
        => Results.Json(
            OpenAiErrorEnvelope.Create(message, type, code, param: null),
            JsonOptions,
            statusCode: status);
}
