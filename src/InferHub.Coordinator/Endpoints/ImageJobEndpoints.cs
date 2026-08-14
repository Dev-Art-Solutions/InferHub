using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;
using InferHub.Shared.OpenAi;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Endpoints;

/// <summary>
/// The async image-job surface (phase 47): <c>POST /api/images/jobs</c> and its four companions.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is InferHub's own contract under <c>/api</c>, and that is not a second dialect</b> (D1).
/// OpenAI has no asynchronous Images API to adopt; phase-21's rule is "adopt the dialect clients
/// already speak", and where there is none the rule does not say "invent an OpenAI-shaped one" — it
/// says what phase-40 D3 said about <c>ToolJob</c>: work with no existing shape travels as its own
/// honest contract. So this is in the house style of <c>/api/admin/models/{model}/ensure</c>, under
/// the <c>/api</c> prefix <c>BearerApiKeyMiddleware</c> already guards.
/// </para>
/// <para>
/// <b>Considered and rejected: a <c>background: true</c> field on <c>/v1/images/generations</c></b>
/// returning a non-OpenAI body. It makes one route return two incompatible shapes depending on a
/// flag, which every typed SDK gets wrong, and it turns "is this endpoint OpenAI-compatible?" into a
/// question with a footnote. This repository has refused that trade five times (21 D3, 22 D1,
/// 34 D1, 38 D2, 40 D3).
/// </para>
/// <para>
/// <b>Every route is client-scoped, and a job that is not yours is a <c>404</c></b> — byte-identical
/// to one that does not exist, never a <c>403</c>. Phase-25 D4's reasoning: a client must not learn
/// what exists but is not theirs. <c>ImageJobAuthTests</c> fails if that ever becomes a 403.
/// </para>
/// </remarks>
public static class ImageJobEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Matches every other saturation refusal in the project (phase-25 D5, phase-40 D5).</summary>
    private const int QueueFullRetryAfterSeconds = 30;

    public static IEndpointRouteBuilder MapImageJobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/images/jobs", SubmitAsync);
        app.MapGet("/api/images/jobs", List);
        app.MapGet("/api/images/jobs/{id}", Get);
        app.MapGet("/api/images/jobs/{id}/events", WatchAsync);
        app.MapGet("/api/images/jobs/{id}/content/{index:int}", Content);
        app.MapDelete("/api/images/jobs/{id}", Cancel);
        return app;
    }

    /// <summary>
    /// Submits a job: JSON for a generation, multipart for an edit or a variation (phase 50).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One route, two content types — not one route with a flag.</b> Phase-47 D1 refused a
    /// <c>background: true</c> field precisely because it made one route answer two incompatible
    /// <em>response</em> shapes; here the response is the same job document either way, and the
    /// request shape is decided by <c>Content-Type</c>, which is what content types are for. An
    /// edit is multipart because it carries a file, exactly as OpenAI's own edits route is.
    /// </para>
    /// <para>
    /// <b>A multipart submission must name its <c>operation</c>.</b> Defaulting it would let a
    /// typo in a field name turn a variation into an edit, and this is InferHub's own contract
    /// (D1) where ceremony is cheaper than a silent substitution.
    /// </para>
    /// </remarks>
    private static async Task<IResult> SubmitAsync(
        HttpContext httpContext,
        ImageJobRegistry jobs,
        INodeRegistry registry,
        CancellationToken cancellationToken)
    {
        IImageRequest? request;

        if (httpContext.Request.HasFormContentType)
        {
            var operation = (await httpContext.Request.ReadFormAsync(cancellationToken))["operation"].FirstOrDefault();

            if (!ImageOperations.IsEditing(operation?.Trim().ToLowerInvariant()))
            {
                return Error(
                    400,
                    $"a multipart image job must name its operation: '{ImageOperations.Edit}' or " +
                    $"'{ImageOperations.Variation}'. Send JSON to generate.",
                    OpenAiErrorTypes.InvalidRequest,
                    param: "operation");
            }

            var (edit, refusal) = await ImageEndpointSupport.ReadEditAsync(
                httpContext,
                ImageEndpointSupport.Limits(httpContext),
                ImageEndpointSupport.AttachmentCap(httpContext),
                operation!.Trim().ToLowerInvariant(),
                cancellationToken);

            if (refusal is not null)
            {
                return refusal;
            }

            request = edit!;
        }
        else
        {
            using var reader = new StreamReader(httpContext.Request.Body);
            var raw = await reader.ReadToEndAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(raw))
            {
                return Error(400, "request body is required", OpenAiErrorTypes.InvalidRequest);
            }

            request = ImageGenerationRequest.TryParse(
                raw,
                name => httpContext.Request.Headers.TryGetValue(name, out var values) ? values.ToString() : null,
                ImageEndpointSupport.Limits(httpContext),
                out var invalid,
                out var invalidParam);

            if (request is null)
            {
                return Error(400, invalid, OpenAiErrorTypes.InvalidRequest, param: invalidParam);
            }
        }

        var client = BearerApiKeyMiddleware.ClientOf(httpContext);
        var admission = httpContext.RequestServices.GetRequiredService<AdmissionControl>()
            .TryAdmit(client, request.Model, UsageUnits.MegapixelSteps);

        if (!admission.Allowed)
        {
            return ImageEndpointSupport.Rejected(httpContext, admission);
        }

        // Refused before anything is queued: a capability nobody provides is fleet state and gets
        // the saturation shape, a model nobody holds is still the 404 (phase-40 D4). Accepting a
        // job nothing on the fleet can ever run would be a queue position that means nothing.
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
                $"the image job queue is full ({jobs.Store.Options.MaxQueueDepth} waiting, Images:Jobs:MaxQueueDepth)",
                OpenAiErrorTypes.ApiError,
                code: "queue_full");
        }

        // 202 with a place in line, not a wait-then-503: the client already accepted asynchrony, so
        // making it retry would be strictly worse than telling it where it is (D5).
        httpContext.Response.Headers.Location = $"/api/images/jobs/{record.Id}";

        return Results.Text(
            ImageJobView.Render(record, jobs.QueuePosition(record.Id)),
            ImageJobView.ContentType,
            statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// This client's jobs, oldest first (phase 51).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Phase 49 deferred this route here by name</b>, and it is the one thing phase 51 adds to the
    /// API: a console that shows "what is this fleet rendering right now, and at which step" cannot
    /// be built out of five routes that all need a job id you already have. Everything else the
    /// Images panel needs, 46–50 already expose.
    /// </para>
    /// <para>
    /// <b>Client-scoped, like every other route here</b> — <see cref="ImageJobStore.ForClient"/>,
    /// never a fleet-wide listing. An admin console showing every tenant's job ids would be
    /// phase-25 D4 undone by a UI, and the ids are the capability: holding one is how you fetch the
    /// picture. The console therefore holds its own <em>client</em> key, exactly as the documents
    /// panel does.
    /// </para>
    /// <para>
    /// <b>It is not a gallery.</b> A finished job's bytes are gone in five minutes and on first read
    /// (47 D6), so this lists <em>work</em>, not results — a job whose images have been delivered is
    /// still here, and still says so, with nothing to fetch.
    /// </para>
    /// </remarks>
    private static IResult List(HttpContext httpContext, ImageJobRegistry jobs)
    {
        var client = BearerApiKeyMiddleware.ClientOf(httpContext);

        var records = jobs.Store.ForClient(client.Id)
            .Select(record => ImageJobView.Describe(record, jobs.QueuePosition(record.Id)))
            .ToArray();

        return Results.Text(
            JsonSerializer.Serialize(
                new
                {
                    jobs = records,

                    // The queue's own depth, so the panel can say "3 waiting" without counting rows
                    // it may not be allowed to see.
                    queued = jobs.Store.Queued().Count,
                    active = jobs.Store.ActiveCount(),
                    retainedBytes = jobs.Store.RetainedBytes(),
                    retentionSeconds = jobs.Store.Options.RetentionSeconds
                },
                ImageJobView.JsonOptions),
            ImageJobView.ContentType);
    }

    private static IResult Get(HttpContext httpContext, ImageJobRegistry jobs, string id)
    {
        if (!TryFind(httpContext, jobs, id, out var record))
        {
            return NotFound(id);
        }

        return Results.Text(ImageJobView.Render(record, jobs.QueuePosition(record.Id)), ImageJobView.ContentType);
    }

    /// <summary>
    /// <c>queued → running(step 7/28) → succeeded</c>, framed by hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The framing is written out rather than taken from a package, exactly as phase-21's SSE and
    /// phase-28's exposition format are: a stream of six-field objects does not justify a
    /// dependency. Phase-26 D2's precedent is exact — a model pull takes minutes and relays progress
    /// on an existing SSE channel — with one deliberate difference: image progress is
    /// <b>client-facing</b>, so it does not go on <c>/api/admin/stream</c>. An admin channel
    /// carrying tenants' job ids is an authorization mistake waiting for somebody to notice.
    /// </para>
    /// <para>
    /// It ends when the job does. A stream that stayed open past a terminal state would leave every
    /// <c>curl -N</c> hanging on a job that finished, which is the shape of bug that gets diagnosed
    /// as "the hub is slow".
    /// </para>
    /// </remarks>
    private static async Task WatchAsync(
        HttpContext httpContext,
        ImageJobRegistry jobs,
        string id,
        CancellationToken cancellationToken)
    {
        if (!TryFind(httpContext, jobs, id, out var record))
        {
            await Results.Json(
                    OpenAiErrorEnvelope.Create($"image job '{id}' not found", OpenAiErrorTypes.NotFound, "job_not_found", "id"),
                    JsonOptions,
                    statusCode: StatusCodes.Status404NotFound)
                .ExecuteAsync(httpContext);

            return;
        }

        httpContext.Response.Headers.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache, no-store";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";
        await httpContext.Response.Body.FlushAsync(cancellationToken);

        var wake = new SemaphoreSlim(0, 1);

        void OnChanged(ImageJobRecord changed)
        {
            if (changed.Id == record.Id)
            {
                try
                {
                    wake.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Already awake. The writer re-reads the record, so a coalesced wake loses
                    // nothing but a duplicate frame.
                }
            }
        }

        jobs.Store.Changed += OnChanged;

        try
        {
            var lastRevision = -1L;
            var due = true;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (due || record.Revision != lastRevision)
                {
                    lastRevision = record.Revision;
                    due = false;
                    await WriteEventAsync(httpContext.Response, record, jobs.QueuePosition(record.Id), cancellationToken);

                    if (record.IsTerminal)
                    {
                        return;
                    }
                }

                // The keepalive is what stops an idle proxy closing a two-minute stream, and it
                // re-sends the current state rather than a bare comment, so a client that
                // reconnected mid-run needs no separate GET to catch up.
                due = !await wake.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The client walked away. The job keeps running — watching is not owning.
        }
        finally
        {
            jobs.Store.Changed -= OnChanged;
            wake.Dispose();
        }
    }

    private static async Task WriteEventAsync(
        HttpResponse response,
        ImageJobRecord record,
        int? queuePosition,
        CancellationToken cancellationToken)
    {
        var payload = ImageJobView.Render(record, queuePosition);
        await response.WriteAsync($"event: {record.State}\n", cancellationToken);
        await response.WriteAsync($"data: {payload}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// One image, once. An expired job is a <c>410</c> that says <em>why</em> rather than a
    /// <c>404</c> that reads like a bug: "you were too late" and "that never existed" are different
    /// problems with different fixes, and only one of them is the caller's mistake.
    /// </summary>
    private static IResult Content(HttpContext httpContext, ImageJobRegistry jobs, string id, int index)
    {
        if (!TryFind(httpContext, jobs, id, out var record))
        {
            return NotFound(id);
        }

        var client = BearerApiKeyMiddleware.ClientOf(httpContext);

        if (jobs.TryTakeContent(record.Id, client.Id, index) is { } image)
        {
            // Phase 49, D4. A caller fetching one image has nowhere else to learn this: the JSON
            // that carried it is a different request, and one they may never have made.
            httpContext.Response.Headers[ImageProjections.Header] = ImageProjections.Normalise(image.Projection);

            // Phase 55, and absent unless a repair was asked for — so a request that sends no
            // seam-repair header is answered exactly as v3.22 answered it, headers included.
            foreach (var (name, value) in SeamRepairModes.HeadersFor(image))
            {
                httpContext.Response.Headers[name] = value;
            }

            return Results.Bytes(image.Bytes, image.MediaType);
        }

        if (record.State == ImageJobStates.Expired)
        {
            return Error(
                410,
                $"image job '{id}' no longer holds its images ({record.Reason}). Results live in memory for " +
                $"{jobs.Store.Options.RetentionSeconds}s and are dropped on delivery unless Images:Jobs:KeepAfterRead is on; " +
                "nothing was written to disk.",
                OpenAiErrorTypes.InvalidRequest,
                code: "job_expired");
        }

        if (record.State != ImageJobStates.Succeeded)
        {
            return Error(
                409,
                $"image job '{id}' is {record.State}; there is nothing to fetch yet",
                OpenAiErrorTypes.InvalidRequest,
                code: "job_not_ready");
        }

        return Error(
            404,
            $"image job '{id}' has no image {index}",
            OpenAiErrorTypes.NotFound,
            param: "index",
            code: "image_not_found");
    }

    private static IResult Cancel(HttpContext httpContext, ImageJobRegistry jobs, string id)
    {
        if (!TryFind(httpContext, jobs, id, out var record))
        {
            return NotFound(id);
        }

        var client = BearerApiKeyMiddleware.ClientOf(httpContext);

        if (!jobs.Cancel(record.Id, client.Id))
        {
            return Error(
                409,
                $"image job '{id}' is already {record.State}",
                OpenAiErrorTypes.InvalidRequest,
                code: "job_terminal");
        }

        // Best effort, and the body says so out loud rather than in the docs only: a job cancelled
        // at step 27 of 28 may still succeed, and a client that then reads `succeeded` gets its
        // image. Pretending otherwise would mean discarding a finished result to honour a state
        // name.
        return Results.Text(
            ImageJobView.Render(jobs.Find(record.Id, client.Id) ?? record, null),
            ImageJobView.ContentType,
            statusCode: StatusCodes.Status202Accepted);
    }

    private static bool TryFind(
        HttpContext httpContext,
        ImageJobRegistry jobs,
        string id,
        out ImageJobRecord record)
    {
        record = null!;

        if (!Guid.TryParse(id, out var parsed))
        {
            // A malformed id is the same 404 as a well-formed one that is not yours. Anything else
            // would let a caller distinguish "this shape is a job id here" from "this one is not".
            return false;
        }

        var found = jobs.Find(parsed, BearerApiKeyMiddleware.ClientOf(httpContext).Id);

        if (found is null)
        {
            return false;
        }

        record = found;
        return true;
    }

    private static IResult NotFound(string id) =>
        Error(404, $"image job '{id}' not found", OpenAiErrorTypes.NotFound, param: "id", code: "job_not_found");

    private static IResult Error(int status, string message, string type, string? param = null, string? code = null)
        => Results.Json(
            OpenAiErrorEnvelope.Create(message, type, code, param),
            JsonOptions,
            statusCode: status);
}
