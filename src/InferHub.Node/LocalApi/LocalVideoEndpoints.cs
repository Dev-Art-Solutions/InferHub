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
/// OpenAI's Videos API on a standalone node (phase 57; phase-41 D8).
/// </summary>
/// <remarks>
/// Solo gets the surface on the same day the hub does, for phase-37 D2's reason a fifth time: the
/// hub's version is this plus routing, and splitting a client-facing dialect across two releases
/// means building it twice and finding the difference in a parity suite later. Every sentence a
/// caller can read comes from <see cref="VideoRenderer"/>, so the two hosts cannot disagree about
/// what a clip says about itself.
/// </remarks>
internal static class LocalVideoEndpoints
{
    private const string SoloClient = "solo";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapLocalVideoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/videos", CreateAsync);
        app.MapGet("/v1/videos/{id}", Get);
        app.MapGet("/v1/videos/{id}/content", Content);
        app.MapDelete("/v1/videos/{id}", Delete);

        app.MapGet("/v1/videos", () => NotImplemented(
            "listing videos is not supported: this node holds no index of jobs to enumerate, and an "
            + "id is itself the capability to fetch the bytes. Keep the id POST /v1/videos returned."));

        app.MapPost("/v1/videos/{id}/remix", (string id) => NotImplemented(
            $"remixing '{id}' is not supported: nothing durable holds the request that made a video "
            + "— no prompt, no negative prompt, by design (rule 7) — so there is nothing here to "
            + "remix from. Send a new request with the prompt you want."));

        return app;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext httpContext,
        LocalImageJobRunner jobs,
        ToolExecutor executor,
        CancellationToken cancellationToken)
    {
        if (LocalApiEndpoints.CapabilityDisabled(httpContext, CapabilityKinds.Video, out var disabled))
        {
            return Error(503, disabled, OpenAiErrorTypes.ApiError, code: "capability_disabled");
        }

        using var reader = new StreamReader(httpContext.Request.Body);
        var raw = await reader.ReadToEndAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return Error(400, "request body is required", OpenAiErrorTypes.InvalidRequest);
        }

        var request = VideoGenerationRequest.TryParse(
            raw,
            name => httpContext.Request.Headers.TryGetValue(name, out var values) ? values.ToString() : null,
            Limits(httpContext),
            out var invalid,
            out var invalidParam);

        if (request is null)
        {
            return Error(400, invalid, OpenAiErrorTypes.InvalidRequest, param: invalidParam);
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
                $"the job queue is full ({jobs.Store.Options.MaxQueueDepth} waiting, Images:Jobs:MaxQueueDepth)",
                OpenAiErrorTypes.ApiError,
                code: "queue_full");
        }

        httpContext.Response.Headers.Location = $"/v1/videos/{VideoRenderer.Identifier(record.Id)}";

        return Results.Text(
            VideoRenderer.Object(record, VideoRenderer.ExpiresAt(record, jobs.Store.Options)),
            VideoRenderer.ContentType);
    }

    private static IResult Get(LocalImageJobRunner jobs, string id)
    {
        if (Find(jobs, id) is not { } record)
        {
            return NotFound(id);
        }

        return Results.Text(
            VideoRenderer.Object(record, VideoRenderer.ExpiresAt(record, jobs.Store.Options)),
            VideoRenderer.ContentType);
    }

    private static IResult Content(LocalImageJobRunner jobs, string id)
    {
        if (Find(jobs, id) is not { } record)
        {
            return NotFound(id);
        }

        if (jobs.Store.TryTakeContent(record.Id, SoloClient, 0) is { } clip)
        {
            return Results.Bytes(clip.Bytes, clip.MediaType);
        }

        var (status, message, code) = VideoRenderer.Unavailable(record, jobs.Store.Options, id);

        return Results.Json(
            OpenAiErrorEnvelope.Create(message, OpenAiErrorTypes.InvalidRequest, code, null),
            JsonOptions,
            statusCode: status);
    }

    private static IResult Delete(LocalImageJobRunner jobs, string id)
    {
        if (Find(jobs, id) is not { } record)
        {
            return NotFound(id);
        }

        jobs.Cancel(record.Id, SoloClient);
        jobs.Store.Drop(record.Id, SoloClient);

        return Results.Text(
            JsonSerializer.Serialize(
                new { id = VideoRenderer.Identifier(record.Id), @object = "video", deleted = true },
                JsonOptions),
            VideoRenderer.ContentType);
    }

    private static ImageJobRecord? Find(LocalImageJobRunner jobs, string id) =>
        VideoRenderer.TryParseIdentifier(id, out var parsed)
            ? jobs.Store.Find(parsed, SoloClient, CapabilityKinds.IsVideo)
            : null;

    private static VideoLimits Limits(HttpContext httpContext)
    {
        var images = httpContext.RequestServices.GetRequiredService<IOptions<ImageEdgeOptions>>().Value;
        var tools = httpContext.RequestServices.GetRequiredService<IOptions<ToolOptions>>().Value;

        return new VideoLimits(Math.Min(images.MaxResponseBytes, tools.MaxAttachmentBytes));
    }

    private static IResult NotFound(string id) =>
        Error(404, $"video '{id}' not found", OpenAiErrorTypes.NotFound, param: "id", code: "video_not_found");

    private static IResult NotImplemented(string message) =>
        Error(501, message, OpenAiErrorTypes.InvalidRequest, code: "not_supported");

    private static IResult Error(int status, string message, string type, string? param = null, string? code = null)
        => Results.Json(OpenAiErrorEnvelope.Create(message, type, code, param), JsonOptions, statusCode: status);
}
