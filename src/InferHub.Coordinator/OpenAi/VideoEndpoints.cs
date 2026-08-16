using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;
using InferHub.Shared.OpenAi;

namespace InferHub.Coordinator.OpenAi;

/// <summary>
/// OpenAI's Videos API on the hub (phase 57, D1).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the phase's central decision and it is a reversal of nothing.</b> Phase 47 built
/// <c>/api/images/jobs</c> on the premise that <em>"OpenAI has no asynchronous Images API to
/// adopt"</em>, and phase-21 D3's rule is to adopt the dialect clients already speak and invent only
/// where there is none. For video there is one, and it is asynchronous by construction — create,
/// poll, fetch, delete — so nothing is invented here. The job model underneath is phase 47's,
/// unchanged (D10).
/// </para>
/// <para>
/// <b>Two of the dialect's routes are refused rather than mapped</b>, with a <c>501</c> that names
/// the reason. Listing enumerates a client's jobs, and this project has never had a route that does
/// (the images listing phase 51 added is a console-scoped exception nobody's SDK calls);
/// <c>remix</c> needs the original request kept after the job ends, which 56 D3 forbids in the one
/// sentence it is built on. A <c>404</c> would read as "an old hub"; a <c>501</c> that says why is
/// what 46 D5 does about <c>response_format=url</c>.
/// </para>
/// <para>
/// <b>The prefix guard already covers this.</b> <c>/v1</c> is in
/// <see cref="BearerApiKeyMiddleware.OpenAiPathPrefix"/>, so these routes are behind a key the moment
/// they are mapped — which is *checked* by a test rather than assumed (phase-21 D2).
/// </para>
/// </remarks>
public static class VideoEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const int QueueFullRetryAfterSeconds = 30;

    public static IEndpointRouteBuilder MapVideoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/videos", CreateAsync);
        app.MapGet("/v1/videos/{id}", Get);
        app.MapGet("/v1/videos/{id}/content", Content);
        app.MapDelete("/v1/videos/{id}", Delete);

        // Mapped so the refusal is a sentence rather than a 404 a client reads as "old hub".
        app.MapGet("/v1/videos", () => NotImplemented(
            "listing videos is not supported: this coordinator holds no client-scoped index of jobs, "
            + "and building one here would hand every caller a way to enumerate ids that are "
            + "themselves the capability to fetch the bytes. Keep the id POST /v1/videos returned."));

        app.MapPost("/v1/videos/{id}/remix", (string id) => NotImplemented(
            $"remixing '{id}' is not supported: nothing durable holds the request that made a video "
            + "— no prompt, no negative prompt, by design (rule 7) — so there is nothing here to "
            + "remix from. Send a new request with the prompt you want."));

        return app;
    }

    /// <summary>
    /// Submits a clip and answers with the <c>video</c> object, <c>status: queued</c>.
    /// </summary>
    /// <remarks>
    /// It is a <c>200</c> and not phase-47's <c>202</c>, because in this dialect the object <em>is</em>
    /// the answer and every SDK reads its <c>status</c>. A queue position has nowhere to live in the
    /// shape, so it is not invented; <c>progress: 0</c> is what a queued job reports and is true.
    /// </remarks>
    private static async Task<IResult> CreateAsync(
        HttpContext httpContext,
        ImageJobRegistry jobs,
        INodeRegistry registry,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(httpContext.Request.Body);
        var raw = await reader.ReadToEndAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return Error(400, "request body is required", OpenAiErrorTypes.InvalidRequest);
        }

        var request = VideoGenerationRequest.TryParse(
            raw,
            name => httpContext.Request.Headers.TryGetValue(name, out var values) ? values.ToString() : null,
            VideoEdge.Limits(httpContext),
            out var invalid,
            out var invalidParam);

        if (request is null)
        {
            return Error(400, invalid, OpenAiErrorTypes.InvalidRequest, param: invalidParam);
        }

        var client = BearerApiKeyMiddleware.ClientOf(httpContext);

        // The quota is megapixel-steps, because it is the same card an image spends (57 D6). A video
        // is a large number of them and that is the point: a counter that billed a five-second clip
        // like one picture would be wrong in the direction that scales with usage.
        var admission = httpContext.RequestServices.GetRequiredService<AdmissionControl>()
            .TryAdmit(client, request.Model, UsageUnits.MegapixelSteps);

        if (!admission.Allowed)
        {
            return ImageEndpointSupport.Rejected(httpContext, admission);
        }

        if (ImageEndpointSupport.NoNode(httpContext, registry, request.Model, admission.Lease, request.Capability)
            is { } unroutable)
        {
            return unroutable;
        }

        var record = jobs.TrySubmit(client, request, admission.Lease);

        if (record is null)
        {
            httpContext.Response.Headers.RetryAfter = QueueFullRetryAfterSeconds.ToString();

            return Error(
                503,
                $"the job queue is full ({jobs.Store.Options.MaxQueueDepth} waiting, Images:Jobs:MaxQueueDepth)",
                OpenAiErrorTypes.ApiError,
                code: "queue_full");
        }

        httpContext.Response.Headers.Location = $"/v1/videos/{VideoRenderer.Identifier(record.Id)}";

        return Results.Text(
            VideoRenderer.Object(record, VideoEdge.ExpiresAt(record, jobs.Store)),
            VideoRenderer.ContentType);
    }

    private static IResult Get(HttpContext httpContext, ImageJobRegistry jobs, string id)
    {
        if (VideoEdge.Find(httpContext, jobs.Store, id) is not { } record)
        {
            return NotFound(id);
        }

        return Results.Text(
            VideoRenderer.Object(record, VideoEdge.ExpiresAt(record, jobs.Store)),
            VideoRenderer.ContentType);
    }

    /// <summary>
    /// The bytes, once. An expired job is a <c>410</c> that says <em>which</em> of the three ways it
    /// went (delivered, evicted, retention lapsed) rather than a <c>404</c> that reads like a bug.
    /// </summary>
    private static IResult Content(HttpContext httpContext, ImageJobRegistry jobs, string id)
    {
        if (VideoEdge.Find(httpContext, jobs.Store, id) is not { } record)
        {
            return NotFound(id);
        }

        var client = BearerApiKeyMiddleware.ClientOf(httpContext);

        if (jobs.TryTakeContent(record.Id, client.Id, 0) is { } clip)
        {
            return Results.Bytes(clip.Bytes, clip.MediaType);
        }

        return VideoEdge.NothingToFetch(record, jobs.Store, id);
    }

    /// <summary>
    /// OpenAI's <c>DELETE</c>, which here is <em>cancel and drop</em> and is honest about the first
    /// half: a job cancelled at step 27 of 28 may still finish (47 D3), and discarding a finished
    /// clip to honour a state name would be worse than reporting what happened.
    /// </summary>
    private static IResult Delete(HttpContext httpContext, ImageJobRegistry jobs, string id)
    {
        if (VideoEdge.Find(httpContext, jobs.Store, id) is not { } record)
        {
            return NotFound(id);
        }

        var client = BearerApiKeyMiddleware.ClientOf(httpContext);
        jobs.Cancel(record.Id, client.Id);
        jobs.Store.Drop(record.Id, client.Id);

        return Results.Text(
            JsonSerializer.Serialize(
                new { id = VideoRenderer.Identifier(record.Id), @object = "video", deleted = true },
                JsonOptions),
            VideoRenderer.ContentType);
    }

    private static IResult NotFound(string id) =>
        Error(404, $"video '{id}' not found", OpenAiErrorTypes.NotFound, param: "id", code: "video_not_found");

    private static IResult NotImplemented(string message) =>
        Error(501, message, OpenAiErrorTypes.InvalidRequest, code: "not_supported");

    private static IResult Error(int status, string message, string type, string? param = null, string? code = null)
        => Results.Json(OpenAiErrorEnvelope.Create(message, type, code, param), JsonOptions, statusCode: status);
}
