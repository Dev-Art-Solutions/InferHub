using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Shared.Contracts;

namespace InferHub.Shared.Images;

/// <summary>
/// The status words OpenAI's Videos API uses, and the mapping from the job states this project
/// already had (phase 57, D1).
/// </summary>
/// <remarks>
/// <para>
/// Phase 47 built <c>/api/images/jobs</c> on an explicit premise — <em>"OpenAI has no asynchronous
/// Images API to adopt"</em> — and phase-21 D3's rule is to adopt the dialect clients already speak
/// and invent only where there is none. For video there <b>is</b> one, and it is asynchronous by
/// construction, so this phase invents nothing: <see cref="ImageJobStates"/> is still the state
/// machine and this is a rendering of it.
/// </para>
/// <para>
/// The mapping loses one distinction on purpose. <c>cancelling</c> renders as <c>in_progress</c>
/// because a job that has been asked to stop is still running (47 D3 — the worker answers from its
/// per-step callback and may well finish), and inventing a status word OpenAI's clients do not know
/// would break the typed enums that are the whole reason for adopting the dialect. What the caller
/// asked for is not lost: the <c>DELETE</c> that asked returned success.
/// </para>
/// </remarks>
public static class VideoStatuses
{
    public const string Queued = "queued";

    public const string InProgress = "in_progress";

    public const string Completed = "completed";

    public const string Failed = "failed";

    /// <summary>
    /// The one word this dialect does not have. A job whose bytes are gone — read once, evicted or
    /// past its retention window — is <c>completed</c> with nothing left to fetch, and the
    /// <c>/content</c> route is the <c>410</c> that says which (47 D6). Reporting it as
    /// <c>failed</c> would say the render did not happen.
    /// </summary>
    public static string From(string? jobState) => jobState switch
    {
        ImageJobStates.Queued => Queued,
        ImageJobStates.Running or ImageJobStates.Cancelling => InProgress,
        ImageJobStates.Succeeded or ImageJobStates.Expired => Completed,
        _ => Failed
    };
}

/// <summary>
/// A video's geometry. It is <see cref="ImageSize"/>'s pair with a <b>different grid</b>, and the
/// difference is not cosmetic (phase 57, D4).
/// </summary>
/// <remarks>
/// <para>
/// <c>WanPipeline.check_inputs</c> in the pinned <c>diffusers==0.36.0</c> requires height and width
/// divisible by <b>16</b>, where every image pipeline this project ships downsamples by 8. Parsing a
/// video size through <see cref="ImageSize.TryParse"/> would accept <c>840x480</c>, hand it to the
/// worker and produce a <c>ValueError</c> out of a library four minutes into a job — the exact shape
/// of refusal-at-the-wrong-end this codebase spends its edge validation avoiding.
/// </para>
/// <para>
/// What the edge still cannot decide is whether a recipe <em>offers</em> this size. That is the
/// worker's catalogue (46 D6's deviation, unchanged), and the refusal names the list.
/// </para>
/// </remarks>
public static class VideoSizes
{
    /// <summary>Every latent video pipeline in the pinned wheel downsamples by this.</summary>
    public const int Grid = 16;

    public const int MinDimension = 64;

    /// <summary>
    /// Lower than <see cref="ImageSize.MaxDimension"/> on purpose: 4096² × 81 frames is not a
    /// request any card in this catalogue can serve, and admitting it at the edge only moves the
    /// refusal to somewhere it costs a dispatch.
    /// </summary>
    public const int MaxDimension = 2048;

    public static bool TryParse(string? value, out ImageSize size, out string error)
    {
        size = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "size is empty";
            return false;
        }

        var parts = value.Trim().ToLowerInvariant().Split('x');

        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height))
        {
            error = $"size '{value}' is not a 'WIDTHxHEIGHT' pair, for example '832x480'";
            return false;
        }

        if (width is < MinDimension or > MaxDimension || height is < MinDimension or > MaxDimension)
        {
            error = $"size '{value}' is outside {MinDimension}-{MaxDimension} pixels per side";
            return false;
        }

        if (width % Grid != 0 || height % Grid != 0)
        {
            error = $"size '{value}' must have both sides a multiple of {Grid} — a video pipeline "
                    + "downsamples by 16 where an image pipeline downsamples by 8, and this is one "
                    + "of the two grids that differ";
            return false;
        }

        size = new ImageSize(width, height);
        return true;
    }
}

/// <summary>
/// What the edge can decide about a video request before a node is chosen, and the one place it is
/// decided so a hub and a solo node cannot disagree (phase 57, D7).
/// </summary>
/// <param name="MaxResponseBytes">
/// Resolved against <c>Tools:MaxAttachmentBytes</c> exactly as <see cref="ImageLimits"/> is, and for
/// the same reason: exceeding the SignalR message cap <em>tears the connection down</em> rather than
/// failing the message, which is the bug that shipped v3.10.0 dead on arrival.
/// </param>
/// <remarks>
/// <b>There is deliberately no <c>Fits</c> here.</b> An image's upper bound is
/// <c>pixels × 4</c> — uncompressed RGBA, which no PNG can exceed — and an mp4's size is an
/// <em>encoder's</em> output, which no arithmetic at the edge predicts. So the budget is not
/// pre-checked against a geometry: the edge refuses a <c>(size, seconds)</c> pair the recipe does not
/// offer (D5) and the node refuses an oversized attachment before it crosses the wire. Guessing here
/// would produce a refusal that is wrong in both directions.
/// </remarks>
public sealed record VideoLimits(long MaxResponseBytes)
{
    /// <summary>
    /// The longest clip the edge will accept a request for at all. A ceiling on the <em>syntax</em>,
    /// not on the catalogue — the recipe's own list is shorter and the worker names it.
    /// </summary>
    public const double MaxSeconds = 60d;

    public static VideoLimits Default { get; } = new(ToolAttachmentLimits.DefaultMaxBytes);
}

/// <summary>
/// A validated <c>POST /v1/videos</c> request, and the tool payload it becomes.
/// </summary>
/// <remarks>
/// <para>
/// <b>It implements <see cref="IImageRequest"/>, and that is the phase's cheapest decision</b>
/// (57 D10). Phase 47's queue, its per-step progress, its cooperative cancel, its read-once
/// retention and phase 56's archive are the same for a video as for an image, and a parallel
/// registry would be two ideas of fairness on one card — the thing 47 D1 built a single path to
/// prevent. The interface's name is wrong by one word and the wrongness is recorded rather than
/// papered over; renaming eleven types in the phase that adds a modality is 52 D4's mistake.
/// </para>
/// <para>
/// <b>The InferHub extensions travel as headers</b>, phase-46 D1's shape — <c>steps</c>,
/// <c>guidance</c> and <c>seed</c>. <c>seconds</c> and <c>size</c> are OpenAI's own body fields and
/// are honoured only at values the recipe declares; <c>negative_prompt</c> is a body field and never
/// a header, because it is the caller's own words and a header is the one part of a request every
/// proxy in the path writes down by default (rule 7).
/// </para>
/// </remarks>
public sealed record VideoGenerationRequest(
    string Model,
    string Prompt,
    string? NegativePrompt,
    ImageSize? Size,
    double? Seconds,
    int? Steps,
    double? Guidance,
    long? Seed) : IImageRequest
{
    public string Capability => CapabilityKinds.Video;

    /// <summary>One clip per request. OpenAI's Videos API has no <c>n</c>, and neither has this.</summary>
    public int Count => 1;

    /// <summary>Nothing travels in with a text-to-video generation. The prompt is the whole input.</summary>
    public IReadOnlyList<ToolAttachment> Attachments => [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public const string StepsHeader = "X-InferHub-Video-Steps";

    public const string GuidanceHeader = "X-InferHub-Video-Guidance";

    public const string SeedHeader = "X-InferHub-Video-Seed";

    /// <summary>
    /// Parses and validates the body and the three extension headers, in the order a caller hits
    /// them.
    /// </summary>
    public static VideoGenerationRequest? TryParse(
        string rawJson,
        Func<string, string?> header,
        VideoLimits limits,
        out string error,
        out string? errorParam)
    {
        _ = limits;
        error = string.Empty;
        errorParam = null;

        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON: {ex.Message}";
            return null;
        }

        if (root.ValueKind is not JsonValueKind.Object)
        {
            error = "the request body must be a JSON object";
            return null;
        }

        var model = Text(root, "model");

        if (string.IsNullOrWhiteSpace(model))
        {
            error = "model is required";
            errorParam = "model";
            return null;
        }

        var prompt = Text(root, "prompt");

        if (string.IsNullOrWhiteSpace(prompt))
        {
            error = "prompt is required";
            errorParam = "prompt";
            return null;
        }

        ImageSize? size = null;

        if (Text(root, "size") is { } rawSize && !string.IsNullOrWhiteSpace(rawSize))
        {
            if (!VideoSizes.TryParse(rawSize, out var parsed, out var sizeError))
            {
                error = sizeError;
                errorParam = "size";
                return null;
            }

            size = parsed;
        }

        // OpenAI spells this as a string in its own examples ("4", "8", "12") and typed SDKs send a
        // number. Both are accepted: refusing one of two spellings the ecosystem uses interchangeably
        // would be this API failing at the one job adopting a dialect is for.
        double? seconds = null;

        if (root.TryGetProperty("seconds", out var secondsElement)
            && secondsElement.ValueKind is not JsonValueKind.Null)
        {
            if (!TryReadSeconds(secondsElement, out var parsed))
            {
                error = "seconds must be a positive number of seconds, for example 5";
                errorParam = "seconds";
                return null;
            }

            if (parsed > VideoLimits.MaxSeconds)
            {
                error = $"seconds must be at most {VideoLimits.MaxSeconds:0} — and the model you "
                        + "named will have a shorter list of its own";
                errorParam = "seconds";
                return null;
            }

            seconds = parsed;
        }

        if (!ImageExtensions.TryInt(header, StepsHeader, 1, ImageExtensions.MaxSteps, out var steps, out error))
        {
            errorParam = StepsHeader;
            return null;
        }

        if (!ImageExtensions.TryDouble(header, GuidanceHeader, 0, 50, out var guidance, out error))
        {
            errorParam = GuidanceHeader;
            return null;
        }

        var seed = Integer(root, "seed");

        if (seed is null && !ImageExtensions.TryLong(header, SeedHeader, out seed, out error))
        {
            errorParam = SeedHeader;
            return null;
        }

        return new VideoGenerationRequest(
            model!.Trim(),
            prompt!,
            Blank(Text(root, "negative_prompt")),
            size,
            seconds,
            steps,
            guidance,
            seed);
    }

    /// <summary>
    /// What the worker receives. <c>op: "video"</c> is what routes it inside the worker's one entry
    /// point, and every field the caller did not name is absent — "use the recipe's default" is said
    /// by omission, because a number invented at the edge would be the edge deciding a model's native
    /// geometry (46 D6, and 480p is right for this model and wrong for the next one).
    /// </summary>
    public string ToToolPayload() => JsonSerializer.Serialize(
        new
        {
            op = "video",
            model = Model,
            prompt = Prompt,
            negative_prompt = NegativePrompt,
            width = Size?.Width,
            height = Size?.Height,
            seconds = Seconds,
            steps = Steps,
            guidance = Guidance,
            seed = Seed
        },
        Json);

    private static bool TryReadSeconds(JsonElement element, out double value)
    {
        value = 0;

        var read = element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(
                element.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value),
            _ => false
        };

        return read && value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Integer(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.Number
        && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
