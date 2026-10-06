using System.Text.Json;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Brio;
using InferHub.Shared.Contracts;
using InferHub.Shared.LlamaCpp;
using InferHub.Shared.OpenAi;

namespace InferHub.Coordinator.OpenAi;

/// <summary>
/// Phase 96: llama.cpp's own routes through the fleet (<c>/v1/llamacpp/{operation}</c>, D4) and the
/// fleet's rerank route (<c>/v1/rerank</c>, D5). Both are a <see cref="ToolJob"/> routed by model —
/// Brio's path (94 D1/D2), so the dispatcher, failover and deadlines learned nothing.
/// </summary>
/// <remarks>
/// Under <c>/v1</c>, which <c>BearerApiKeyMiddleware</c> guards (21 D2). Rule 7: a log line carries
/// the operation, the model, the outcome and a count — never a body, a query or a document.
/// </remarks>
public static class LlamaCppEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapLlamaCppEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/llamacpp/{operation}", HandlePostAsync).AddEndpointFilter(DispatchDeadlineFilter.OpenAi);
        app.MapGet("/v1/llamacpp/{operation}", HandleGetAsync).AddEndpointFilter(DispatchDeadlineFilter.OpenAi);
        app.MapPost("/v1/rerank", HandleRerankAsync).AddEndpointFilter(DispatchDeadlineFilter.OpenAi);
        return app;
    }

    private static async Task<IResult> HandlePostAsync(
        string operation,
        HttpContext httpContext,
        Services.IRouter router,
        INodeRegistry registry,
        IToolDispatcher tools,
        Metrics metrics,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        metrics.RecordOpenAiRequest();

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

        return await RunNativeAsync(httpContext, operation, model, LlamaCppNative.Payload(operation, raw), router, registry, tools, loggerFactory, cancellationToken);
    }

    private static async Task<IResult> HandleGetAsync(
        string operation,
        HttpContext httpContext,
        Services.IRouter router,
        INodeRegistry registry,
        IToolDispatcher tools,
        Metrics metrics,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        metrics.RecordOpenAiRequest();

        if (!LlamaCppNative.IsGet(operation))
        {
            return UnknownOperation(operation);
        }

        var model = httpContext.Request.Query["model"].ToString().Trim();

        if (model.Length == 0)
        {
            return Error(400, "model is required: ?model=<name>", OpenAiErrorTypes.InvalidRequest);
        }

        return await RunNativeAsync(httpContext, operation, model, LlamaCppNative.Payload(operation, null), router, registry, tools, loggerFactory, cancellationToken);
    }

    private static async Task<IResult> RunNativeAsync(
        HttpContext httpContext,
        string operation,
        string model,
        string payload,
        Services.IRouter router,
        INodeRegistry registry,
        IToolDispatcher tools,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.OpenAi.LlamaCpp");
        var job = new ToolJob(Guid.NewGuid(), CapabilityKinds.LlamaCpp, model, payload);

        var (result, refusal, nodeId) = await DispatchAsync(httpContext, job, router, registry, tools, cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        var outcome = LlamaCppNative.Render(result!);
        var (prompt, completion) = outcome.IsError || !LlamaCppNative.Generates(operation)
            ? (0L, 0L)
            : LlamaCppNative.Tokens(outcome.Json!);

        logger.LogInformation(
            "llama.cpp {Operation} {JobId} ({Model}) on node {NodeId}: {Status}, {Prompt}+{Completion} tokens",
            operation,
            job.JobId,
            model,
            nodeId,
            outcome.Status,
            prompt,
            completion);

        if (outcome.IsError)
        {
            return Failure(httpContext, outcome);
        }

        // D4: what generated is billed, against the client's quota like a chat; a failed job never is.
        if (prompt + completion > 0)
        {
            var context = InferenceCore.ClientContext.From(httpContext);
            context.Usage.RecordTokens(context.Client, CapabilityKinds.LlamaCpp, model, prompt, completion);
        }

        return Results.Text(outcome.Json!, "application/json");
    }

    private static async Task<IResult> HandleRerankAsync(
        HttpContext httpContext,
        Services.IRouter router,
        INodeRegistry registry,
        IToolDispatcher tools,
        Metrics metrics,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.OpenAi.Rerank");
        metrics.RecordOpenAiRequest();

        using var reader = new StreamReader(httpContext.Request.Body);
        var raw = await reader.ReadToEndAsync(cancellationToken);

        if (RerankRequest.TryParse(raw, out var invalid) is not { } request)
        {
            return Error(400, invalid, OpenAiErrorTypes.InvalidRequest);
        }

        var job = new ToolJob(Guid.NewGuid(), CapabilityKinds.Rerank, request.Model, request.JobPayload());
        var (result, refusal, nodeId) = await DispatchAsync(httpContext, job, router, registry, tools, cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        var outcome = LlamaCppNative.Render(result!);
        var rendered = outcome.IsError ? null : request.Render(outcome.Json!);

        logger.LogInformation(
            "Rerank {JobId} ({Model}, {Count} documents) on node {NodeId}: {Status}",
            job.JobId,
            request.Model,
            request.Documents.Count,
            nodeId,
            outcome.IsError ? outcome.Status : rendered is null ? 502 : 200);

        if (outcome.IsError)
        {
            return Failure(httpContext, outcome);
        }

        return rendered is null
            ? Error(502, "the reranker did not return one score per document", OpenAiErrorTypes.ApiError)
            : Results.Text(rendered, "application/json");
    }

    /// <summary>Admission, routing and the hop — Brio's order (25 D4, 40 D4/D5).</summary>
    private static async Task<(ToolResult? Result, IResult? Refusal, string? NodeId)> DispatchAsync(
        HttpContext httpContext,
        ToolJob job,
        Services.IRouter router,
        INodeRegistry registry,
        IToolDispatcher tools,
        CancellationToken cancellationToken)
    {
        // Admission before routing: a scoped-out model is the same 404 as a missing one.
        var context = InferenceCore.ClientContext.From(httpContext);
        var admission = context.Admission.TryAdmit(context.Client, job.Model);

        if (!admission.Allowed)
        {
            return (null, Rejected(httpContext, admission), null);
        }

        using var lease = admission.Lease;
        var node = router.Route(job.Model, capability: job.Capability);

        if (node is null)
        {
            return (null, NoNode(httpContext, registry, job.Capability, job.Model), null);
        }

        httpContext.Response.Headers[InferenceCore.ServedByHeader] = "node";

        try
        {
            return (await tools.DispatchToolAsync(node, job, cancellationToken), null, node.NodeId);
        }
        catch (NodeDisconnectedException)
        {
            return (null, Error(502, "the node handling this request disconnected before it answered", OpenAiErrorTypes.ApiError, code: "node_lost"), node.NodeId);
        }
    }

    private static IResult UnknownOperation(string operation)
        => Error(
            404,
            $"'{operation}' is not a llama.cpp route this fleet passes through; it passes "
            + $"POST {string.Join(", ", LlamaCppNative.PostOperations)} and GET {string.Join(", ", LlamaCppNative.GetOperations)}",
            OpenAiErrorTypes.NotFound,
            code: "unknown_operation");

    private static IResult NoNode(HttpContext httpContext, INodeRegistry registry, string capability, string model)
    {
        if (ToolEndpoints.KnownToTheFleet(registry, model))
        {
            httpContext.Response.Headers.RetryAfter = ToolEndpoints.CapabilityRetryAfterSeconds.ToString();

            return Error(
                503,
                $"no node currently provides '{capability}' for model '{model}'; "
                + (capability == CapabilityKinds.Rerank
                    ? "a reranker is a llama.cpp model with Reranking: true, or a cross-encoder tool"
                    : "the llama.cpp routes need a node with a llamacpp engine"),
                OpenAiErrorTypes.ApiError,
                code: "capability_unavailable");
        }

        return Error(404, $"model '{model}' not found", OpenAiErrorTypes.NotFound, code: "model_not_found");
    }

    private static IResult Failure(HttpContext httpContext, BrioOutcome outcome)
    {
        if (outcome.RetryAfterSeconds is { } retryAfter)
        {
            httpContext.Response.Headers.RetryAfter = retryAfter.ToString();
        }

        return Error(outcome.Status, outcome.Error!, outcome.ErrorType, code: outcome.ErrorCode);
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
