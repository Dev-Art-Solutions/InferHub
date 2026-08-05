using System.Text.Json;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;
using InferHub.Shared.OpenAi;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.OpenAi;

/// <summary>
/// Text to image, on OpenAI's Images API (phase 46): <c>POST /v1/images/generations</c>.
/// </summary>
/// <remarks>
/// <para>
/// The client surface is OpenAI's, exactly — <b>D1</b>, and it is phase 21's argument for the third
/// time. Every SDK already speaks it, so pointing an existing app at your own card is a base-URL
/// change. There is no <em>Ollama</em> image dialect to be the other side of a translation, so as
/// with audio there is exactly one client shape and the node-facing side is a <c>ToolJob</c>
/// (phase-40 D3) — <b>no protocol change at all</b>, which is the thing the capability seam was
/// built to make possible.
/// </para>
/// <para>
/// These routes sit under <c>/v1</c>, which <c>BearerApiKeyMiddleware</c> guards (phase-21 D2) —
/// <c>ImageEndpointTests.TheImageRouteRejectsAMissingKey</c> fails if that ever stops being true,
/// because a client-facing route under an unguarded prefix is an unauthenticated inference API and
/// this one occupies a GPU for a minute per call.
/// </para>
/// <para>
/// <b>Nothing here is retained</b> (D5). The image bytes are held for the dispatch, base64'd into
/// the response and dropped: no temp file, no cache, no URL to fetch later, and no log line
/// containing the prompt at any level. A prompt is content in exactly the sense rule 7 means — it is
/// what somebody wanted — and the picture is the answer. The log line carries the model, the image
/// count and the outcome. <c>ImagePrivacyTests</c> asserts it against a capturing logger.
/// </para>
/// </remarks>
public static class ImageEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapImageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/images/generations", HandleGenerationAsync);
        return app;
    }

    private static async Task<IResult> HandleGenerationAsync(
        HttpContext httpContext,
        Services.IRouter router,
        INodeRegistry registry,
        IToolDispatcher tools,
        Metrics metrics,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.OpenAi.Images");
        metrics.RecordOpenAiRequest();

        using var reader = new StreamReader(httpContext.Request.Body);
        var raw = await reader.ReadToEndAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return Error(400, "request body is required", OpenAiErrorTypes.InvalidRequest);
        }

        var request = ImageGenerationRequest.TryParse(
            raw,
            name => httpContext.Request.Headers.TryGetValue(name, out var values) ? values.ToString() : null,
            Limits(httpContext),
            out var invalid,
            out var invalidParam);

        if (request is null)
        {
            return Error(400, invalid, OpenAiErrorTypes.InvalidRequest, param: invalidParam);
        }

        var context = InferenceCore.ClientContext.From(httpContext);
        var admission = context.Admission.TryAdmit(context.Client, request.Model, UsageUnits.MegapixelSteps);

        if (!admission.Allowed)
        {
            logger.LogInformation(
                "Rejected image generation for client {ClientId}: {Status} {Message}",
                context.Client.Id,
                admission.Status,
                admission.Message);

            return Rejected(httpContext, admission);
        }

        using var lease = admission.Lease;

        var node = router.Route(request.Model, capability: CapabilityKinds.Image);

        if (node is null)
        {
            return NoNode(httpContext, registry, request.Model);
        }

        var job = new ToolJob(Guid.NewGuid(), CapabilityKinds.Image, request.Model, request.ToToolPayload());

        var result = await tools.DispatchToolAsync(node, job, cancellationToken);
        var outcome = ImageRenderer.Generation(result, request, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // Model, count, megapixel-steps, outcome. Never the prompt, and never a byte of the image.
        logger.LogInformation(
            "Image job {JobId} ({Model}) on node {NodeId}: {Status}, {Images} image(s), {Units:F1} megapixel-steps",
            job.JobId,
            request.Model,
            node.NodeId,
            outcome.Status,
            outcome.ImageCount,
            outcome.Units);

        httpContext.Response.Headers[InferenceCore.ServedByHeader] = "node";

        if (!outcome.IsError)
        {
            context.Usage.RecordUnits(context.Client, CapabilityKinds.Image, request.Model, outcome.Units, outcome.UnitKind);
            metrics.RecordToolUnits(CapabilityKinds.Image, request.Model, outcome.Units, outcome.UnitKind);
        }

        return Render(httpContext, outcome);
    }

    private static ImageLimits Limits(HttpContext httpContext)
    {
        var images = httpContext.RequestServices.GetService<IOptions<ImageEdgeOptions>>()?.Value ?? new ImageEdgeOptions();

        var attachments = httpContext.RequestServices.GetService<IOptions<ToolEdgeOptions>>()?.Value.MaxAttachmentBytes
            ?? ToolAttachmentLimits.DefaultMaxBytes;

        return images.Resolve(attachments);
    }

    /// <summary>
    /// Phase-40 D5, unchanged and reused rather than re-argued: a capability nobody provides is
    /// fleet state and gets the saturation shape; a model nobody holds at all is still the 404 that
    /// is byte-identical to the one a scoped-out model produces (phase-25 D4).
    /// </summary>
    private static IResult NoNode(HttpContext httpContext, INodeRegistry registry, string model)
    {
        if (ToolEndpoints.KnownToTheFleet(registry, model))
        {
            httpContext.Response.Headers.RetryAfter = ToolEndpoints.CapabilityRetryAfterSeconds.ToString();

            return Error(
                503,
                $"no node currently provides '{CapabilityKinds.Image}' for model '{model}'",
                OpenAiErrorTypes.ApiError,
                code: "capability_unavailable");
        }

        return Error(404, $"model '{model}' not found", OpenAiErrorTypes.NotFound, param: "model", code: "model_not_found");
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

    /// <summary>
    /// The ten lines phase-37 D6 keeps per host. Everything above them — the status, the envelope,
    /// the base64 — was decided by <see cref="ImageRenderer"/>, which the solo node calls too.
    /// </summary>
    internal static IResult Render(HttpContext httpContext, ImageOutcome outcome)
    {
        if (outcome.IsError)
        {
            if (outcome.RetryAfterSeconds is { } retryAfter)
            {
                httpContext.Response.Headers.RetryAfter = retryAfter.ToString();
            }

            return Error(outcome.Status, outcome.Error!, outcome.ErrorType, outcome.ErrorParam, outcome.ErrorCode);
        }

        return Results.Text(outcome.Json ?? "{}", ImageRenderer.ContentType);
    }

    private static IResult Error(int status, string message, string type, string? param = null, string? code = null)
        => Results.Json(
            OpenAiErrorEnvelope.Create(message, type, code, param),
            JsonOptions,
            statusCode: status);
}
