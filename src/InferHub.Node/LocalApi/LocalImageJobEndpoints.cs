using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Node.Tools;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;
using InferHub.Shared.OpenAi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InferHub.Node.LocalApi;

/// <summary>
/// The five job routes, served by the node itself (phase 47; phase-41 D8).
/// </summary>
/// <remarks>
/// Same paths, same bodies, same statuses as the coordinator's. A developer who wrote against a hub
/// and then ran one <c>docker run</c> on their own box should not have to change a line, and a
/// difference in either direction is a difference a caller sees — which is why the parts a caller
/// can observe (<see cref="ImageJobView"/>, <see cref="ImageJobStore"/>,
/// <see cref="ImageGenerationRequest"/>) are the coordinator's own classes rather than a second
/// implementation of them.
/// </remarks>
internal static class LocalImageJobEndpoints
{
    /// <summary>Solo has no tenancy, so every job on this box belongs to the same caller.</summary>
    /// <remarks>
    /// Phase-38 D9's line: the hub's client-scope check has no counterpart here, because a node has
    /// no client model to scope against. The store still keys on an owner — using one constant
    /// rather than deleting the parameter keeps the two hosts' code the same shape, so the check
    /// cannot quietly go missing on the side that does need it.
    /// </remarks>
    private const string SoloClient = "solo";

    public static IEndpointRouteBuilder MapLocalImageJobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/images/jobs", SubmitAsync);
        app.MapGet("/api/images/jobs", List);
        app.MapGet("/api/images/jobs/{id}", Get);
        app.MapGet("/api/images/jobs/{id}/events", WatchAsync);
        app.MapGet("/api/images/jobs/{id}/content/{index:int}", Content);
        app.MapDelete("/api/images/jobs/{id}", Cancel);
        return app;
    }

    private static async Task<IResult> SubmitAsync(
        HttpContext httpContext,
        LocalImageJobRunner jobs,
        ToolExecutor executor,
        CancellationToken cancellationToken)
    {
        IImageRequest? request;

        if (httpContext.Request.HasFormContentType)
        {
            // Same contract as the hub's: JSON generates, multipart edits, and a multipart
            // submission must name its operation rather than have one guessed for it.
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

            if (LocalApiEndpoints.CapabilityDisabled(httpContext, CapabilityKinds.ImageEdit, out var editDisabled))
            {
                return Error(503, editDisabled, OpenAiErrorTypes.ApiError, code: "capability_disabled");
            }

            var (edit, refusal) = await LocalImageForm.ReadEditAsync(
                httpContext,
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
            if (LocalApiEndpoints.CapabilityDisabled(httpContext, CapabilityKinds.Image, out var disabled))
            {
                return Error(503, disabled, OpenAiErrorTypes.ApiError, code: "capability_disabled");
            }

            using var reader = new StreamReader(httpContext.Request.Body);
            var raw = await reader.ReadToEndAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(raw))
            {
                return Error(400, "request body is required", OpenAiErrorTypes.InvalidRequest);
            }

            request = ImageGenerationRequest.TryParse(
                raw,
                name => httpContext.Request.Headers.TryGetValue(name, out var values) ? values.ToString() : null,
                Limits(httpContext),
                out var invalid,
                out var invalidParam);

            if (request is null)
            {
                return Error(400, invalid, OpenAiErrorTypes.InvalidRequest, param: invalidParam);
            }
        }

        if (!executor.Provides(request.Capability, request.Model))
        {
            httpContext.Response.Headers.RetryAfter = ToolExecutor.CapabilityRetryAfterSeconds.ToString();

            return Error(
                503,
                $"this node does not provide '{request.Capability}' for model '{request.Model}'",
                OpenAiErrorTypes.ApiError,
                code: "capability_unavailable");
        }

        var record = jobs.TrySubmit(SoloClient, request);

        if (record is null)
        {
            httpContext.Response.Headers.RetryAfter = "30";

            return Error(
                503,
                $"the image job queue is full ({jobs.Store.Options.MaxQueueDepth} waiting, Images:Jobs:MaxQueueDepth)",
                OpenAiErrorTypes.ApiError,
                code: "queue_full");
        }

        httpContext.Response.Headers.Location = $"/api/images/jobs/{record.Id}";

        return Results.Text(
            ImageJobView.Render(record, jobs.Store.QueuePosition(record.Id)),
            ImageJobView.ContentType,
            statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// Every job on this box, oldest first (phase 51). Same shape as the hub's.
    /// </summary>
    /// <remarks>
    /// Solo has no tenancy, so "this client's jobs" and "every job" are the same set here — the
    /// <see cref="SoloClient"/> constant is what keeps the two hosts' code the same shape rather
    /// than one of them growing a scope check the other lacks.
    /// </remarks>
    private static IResult List(LocalImageJobRunner jobs)
    {
        var records = jobs.Store.ForClient(SoloClient)
            .Select(record => ImageJobView.Describe(record, jobs.Store.QueuePosition(record.Id)))
            .ToArray();

        return Results.Text(
            JsonSerializer.Serialize(
                new
                {
                    jobs = records,
                    queued = jobs.Store.Queued().Count,
                    active = jobs.Store.ActiveCount(),
                    retainedBytes = jobs.Store.RetainedBytes(),
                    retentionSeconds = jobs.Store.Options.RetentionSeconds
                },
                ImageJobView.JsonOptions),
            ImageJobView.ContentType);
    }

    private static IResult Get(LocalImageJobRunner jobs, string id) =>
        Find(jobs, id) is { } record
            ? Results.Text(ImageJobView.Render(record, jobs.Store.QueuePosition(record.Id)), ImageJobView.ContentType)
            : NotFound(id);

    private static async Task WatchAsync(
        HttpContext httpContext,
        LocalImageJobRunner jobs,
        string id,
        CancellationToken cancellationToken)
    {
        if (Find(jobs, id) is not { } record)
        {
            await NotFound(id).ExecuteAsync(httpContext);
            return;
        }

        httpContext.Response.Headers.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache, no-store";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";
        await httpContext.Response.Body.FlushAsync(cancellationToken);

        var wake = new SemaphoreSlim(0, 1);

        void OnChanged(ImageJobRecord changed)
        {
            if (changed.Id != record.Id)
            {
                return;
            }

            try
            {
                wake.Release();
            }
            catch (SemaphoreFullException)
            {
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

                    var payload = ImageJobView.Render(record, jobs.Store.QueuePosition(record.Id));
                    await httpContext.Response.WriteAsync($"event: {record.State}\n", cancellationToken);
                    await httpContext.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
                    await httpContext.Response.Body.FlushAsync(cancellationToken);

                    if (record.IsTerminal)
                    {
                        return;
                    }
                }

                due = !await wake.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            jobs.Store.Changed -= OnChanged;
            wake.Dispose();
        }
    }

    private static IResult Content(HttpContext httpContext, LocalImageJobRunner jobs, string id, int index)
    {
        if (Find(jobs, id) is not { } record)
        {
            return NotFound(id);
        }

        if (jobs.Store.TryTakeContent(record.Id, SoloClient, index) is { } image)
        {
            // The same header the hub sets (phase 49, D4). A client must not be able to tell which
            // of the two answered it.
            httpContext.Response.Headers[ImageProjections.Header] = ImageProjections.Normalise(image.Projection);

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

    private static IResult Cancel(LocalImageJobRunner jobs, string id)
    {
        if (Find(jobs, id) is not { } record)
        {
            return NotFound(id);
        }

        if (!jobs.Cancel(record.Id, SoloClient))
        {
            return Error(
                409,
                $"image job '{id}' is already {record.State}",
                OpenAiErrorTypes.InvalidRequest,
                code: "job_terminal");
        }

        return Results.Text(
            ImageJobView.Render(jobs.Store.Find(record.Id, SoloClient) ?? record, null),
            ImageJobView.ContentType,
            statusCode: StatusCodes.Status202Accepted);
    }

    private static ImageJobRecord? Find(LocalImageJobRunner jobs, string id) =>
        Guid.TryParse(id, out var parsed) ? jobs.Store.Find(parsed, SoloClient) : null;

    private static ImageLimits Limits(HttpContext httpContext)
    {
        var images = httpContext.RequestServices.GetService<IOptions<ImageEdgeOptions>>()?.Value ?? new ImageEdgeOptions();

        var attachments = httpContext.RequestServices.GetService<IOptions<ToolOptions>>()?.Value.MaxAttachmentBytes
            ?? ToolAttachmentLimits.DefaultMaxBytes;

        return images.Resolve(attachments);
    }

    private static IResult NotFound(string id) =>
        Error(404, $"image job '{id}' not found", OpenAiErrorTypes.NotFound, param: "id", code: "job_not_found");

    private static IResult Error(int status, string message, string type, string? param = null, string? code = null)
        => Results.Json(
            OpenAiErrorEnvelope.Create(message, type, code, param),
            LocalApiEndpoints.JsonOptions,
            statusCode: status);
}
