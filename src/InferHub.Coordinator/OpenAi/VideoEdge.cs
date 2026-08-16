using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;
using InferHub.Shared.OpenAi;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.OpenAi;

/// <summary>
/// The ASP.NET-shaped half of the Videos API: reading a header, resolving an id, writing a status.
/// </summary>
/// <remarks>
/// Phase-37 D6's line, for the sixth time: <b>the sentences come from <c>InferHub.Shared</c> and the
/// ten lines that touch <c>HttpContext</c> are per host.</b> Design rule 2 keeps ASP.NET out of the
/// shared library, so this file and its solo twin are the plumbing — and everything either of them
/// could get *differently* wrong (the object's keys, the expiry arithmetic, the three sentences a
/// missing clip earns) is decided in <see cref="VideoRenderer"/>, once.
/// </remarks>
internal static class VideoEdge
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The response budget, clamped by the attachment cap for phase-46 D4's reason: exceeding the
    /// SignalR message cap tears the connection down rather than failing the message.
    /// </summary>
    public static VideoLimits Limits(HttpContext httpContext)
    {
        var images = httpContext.RequestServices.GetRequiredService<IOptions<ImageEdgeOptions>>().Value;
        var tools = httpContext.RequestServices.GetRequiredService<IOptions<ToolEdgeOptions>>().Value;

        return new VideoLimits(Math.Min(images.MaxResponseBytes, tools.MaxAttachmentBytes));
    }

    /// <summary>
    /// The record behind a <c>video_…</c> id, scoped to this client <b>and</b> to the video surface.
    /// </summary>
    /// <remarks>
    /// A malformed id, another client's id and an <em>image</em> job's id are all the same null and
    /// therefore the same <c>404</c> (phase-25 D4). "That id is real but it is a picture" tells a
    /// caller something about an id they were never meant to reason about.
    /// </remarks>
    public static ImageJobRecord? Find(HttpContext httpContext, ImageJobStore store, string id) =>
        VideoRenderer.TryParseIdentifier(id, out var parsed)
            ? store.Find(parsed, BearerApiKeyMiddleware.ClientOf(httpContext).Id, CapabilityKinds.IsVideo)
            : null;

    public static long? ExpiresAt(ImageJobRecord record, ImageJobStore store) =>
        VideoRenderer.ExpiresAt(record, store.Options);

    public static IResult NothingToFetch(ImageJobRecord record, ImageJobStore store, string id)
    {
        var (status, message, code) = VideoRenderer.Unavailable(record, store.Options, id);

        return Results.Json(
            OpenAiErrorEnvelope.Create(message, OpenAiErrorTypes.InvalidRequest, code, null),
            JsonOptions,
            statusCode: status);
    }
}
