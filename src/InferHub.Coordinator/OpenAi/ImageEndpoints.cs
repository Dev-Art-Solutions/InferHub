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
/// <b>Phase 47 turned this route into "submit a job and wait for it", and nothing about it changed
/// for a caller.</b> Same body, same headers, same envelope, same statuses; the request now runs
/// through <see cref="ImageJobRegistry"/> so a synchronous call and an asynchronous one queue in the
/// same line and are metered by the same code — the alternative, two paths to a GPU with two ideas
/// of fairness, is how a fleet develops a fast lane nobody documented. <c>ImageSyncCompatTests</c>
/// asserts the response is byte-identical to 3.14 modulo ids. What is new is an honest ceiling:
/// past <c>Images:SyncMaxWaitSeconds</c> the answer is a <c>503</c> naming the async route, rather
/// than a connection held open until a proxy in the path cuts it with a status nobody can act on.
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
    public static IEndpointRouteBuilder MapImageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/images/generations", HandleGenerationAsync);
        return app;
    }

    private static async Task<IResult> HandleGenerationAsync(
        HttpContext httpContext,
        ImageJobRegistry jobs,
        INodeRegistry registry,
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
            return ImageEndpointSupport.Error(400, "request body is required", OpenAiErrorTypes.InvalidRequest);
        }

        var request = ImageGenerationRequest.TryParse(
            raw,
            name => httpContext.Request.Headers.TryGetValue(name, out var values) ? values.ToString() : null,
            ImageEndpointSupport.Limits(httpContext),
            out var invalid,
            out var invalidParam);

        if (request is null)
        {
            return ImageEndpointSupport.Error(400, invalid, OpenAiErrorTypes.InvalidRequest, param: invalidParam);
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

            return ImageEndpointSupport.Rejected(httpContext, admission);
        }

        if (ImageEndpointSupport.NoNode(httpContext, registry, request.Model, admission.Lease) is { } refusal)
        {
            return refusal;
        }

        var record = jobs.TrySubmit(context.Client, request, admission.Lease);

        if (record is null)
        {
            httpContext.Response.Headers.RetryAfter = "30";

            return ImageEndpointSupport.Error(
                503,
                $"the image job queue is full ({jobs.Store.Options.MaxQueueDepth} waiting, Images:Jobs:MaxQueueDepth)",
                OpenAiErrorTypes.ApiError,
                code: "queue_full");
        }

        var budget = TimeSpan.FromSeconds(Math.Max(
            1,
            httpContext.RequestServices.GetService<IOptions<ImageEdgeOptions>>()?.Value.SyncMaxWaitSeconds
                ?? new ImageEdgeOptions().SyncMaxWaitSeconds));

        var finished = await ImageJobWait.ForTerminalAsync(jobs.Store, record, budget, cancellationToken);

        httpContext.Response.Headers[InferenceCore.ServedByHeader] = "node";

        if (!finished)
        {
            // The job is still running and keeps running — the caller has a real id and can watch
            // it. Cancelling it here would throw away a minute of GPU because an HTTP client got
            // bored, which is a decision only the caller can make.
            httpContext.Response.Headers.RetryAfter = "5";

            return ImageEndpointSupport.Error(
                503,
                $"this image is taking longer than {budget.TotalSeconds:F0}s (Images:SyncMaxWaitSeconds). It is still " +
                $"running as job {record.Id}: watch GET /api/images/jobs/{record.Id}/events and collect it from " +
                $"GET /api/images/jobs/{record.Id}/content/0.",
                OpenAiErrorTypes.ApiError,
                code: "job_still_running");
        }

        return Render(httpContext, jobs, record);
    }

    /// <summary>
    /// The 3.14 response, reproduced from the job the request became.
    /// </summary>
    /// <remarks>
    /// The outcome is <b>not</b> logged here: <see cref="ImageJobRegistry"/> already logged it, once,
    /// where a job actually finishes — so a synchronous call and an asynchronous one produce the same
    /// single line, and <c>ImagePrivacyTests</c>'s "exactly one line, and it names the model rather
    /// than the prompt" stays a statement about the hub rather than about one route.
    /// </remarks>
    private static IResult Render(HttpContext httpContext, ImageJobRegistry jobs, ImageJobRecord record)
    {
        if (record.State != ImageJobStates.Succeeded)
        {
            // The status the *worker's* answer earned, carried on the job. Flattening every failure
            // to a 502 here would lose the 400 a refused size earns and the 503 a busy tool earns —
            // phase-29 D6's inference by the back door, reached by discarding information rather
            // than by guessing.
            if (record.Failure is { } failure)
            {
                if (failure.RetryAfterSeconds is { } retryAfter)
                {
                    httpContext.Response.Headers.RetryAfter = retryAfter.ToString();
                }

                return ImageEndpointSupport.Error(
                    failure.Status,
                    failure.Message ?? $"the image job ended {record.State}",
                    failure.ErrorType,
                    failure.ErrorParam,
                    failure.ErrorCode);
            }

            return ImageEndpointSupport.Error(
                // A cancel can only have come from this client's own DELETE, so it is a conflict
                // between two of their requests rather than a server failure.
                record.Reason == ImageJobReasons.Cancelled ? 409 : 502,
                record.Error ?? $"the image job ended {record.State}",
                OpenAiErrorTypes.ApiError,
                code: record.Reason);
        }

        // Read-once applies here too, and that is the point of routing the sync path through the
        // same store: one code path decides when bytes go away, so "the synchronous route quietly
        // keeps a copy" is not a state this hub can be in.
        var summary = record.Summary;
        var images = jobs.Store.TryTakeAll(record.Id, record.ClientId);

        if (images.Count == 0)
        {
            return ImageEndpointSupport.Error(502, "the image worker returned no image", OpenAiErrorTypes.ApiError);
        }

        // The envelope is built by ImageRenderer for both hosts (phase 49). Two hand-written copies
        // of the field list is one field away from a difference a caller can see, and this release
        // adds four fields to it.
        return Results.Text(
            ImageRenderer.Envelope(images, summary, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ImageRenderer.ContentType);
    }
}
