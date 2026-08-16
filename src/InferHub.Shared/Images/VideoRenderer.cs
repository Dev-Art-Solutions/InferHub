using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Shared.Contracts;
using InferHub.Shared.OpenAi;

namespace InferHub.Shared.Images;

/// <summary>
/// The OpenAI <c>video</c> object and the video tool result, written in exactly one place so a hub
/// and a solo node cannot describe the same clip differently (phase 57, D1).
/// </summary>
/// <remarks>
/// This is <see cref="ImageRenderer"/>'s lesson applied before the bug rather than after it: three
/// surfaces built the Images envelope by hand until phase 49, and the divergence that produced
/// (<c>revised_prompt: null</c> on the hub, the key absent on a solo node) ran for three releases
/// under a parity suite. So the object is dictionaries in one method, and which keys are
/// <em>present</em> is part of the contract rather than a serializer option.
/// </remarks>
public static class VideoRenderer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public const string ContentType = "application/json";

    /// <summary>What a video attachment is, when the worker did not say.</summary>
    public const string DefaultMediaType = "video/mp4";

    /// <summary>
    /// Turns a finished video tool result into what the job should hold. The <em>bytes</em> are never
    /// inlined in the video object — OpenAI's dialect fetches them from <c>/content</c>, which is the
    /// one place 46 D5's "no URL" refusal and this API's shape agree without argument.
    /// </summary>
    public static ImageOutcome Render(ToolResult result, VideoGenerationRequest request)
    {
        if (ImageRenderer.Failure(result) is { } failure)
        {
            return failure;
        }

        var attachments = result.Attachments ?? [];

        if (attachments.Count == 0)
        {
            return new ImageOutcome { Status = 502, Error = "the video worker returned no video" };
        }

        if (attachments[0].Bytes.Length == 0)
        {
            return new ImageOutcome { Status = 502, Error = "the video worker returned an empty file" };
        }

        var report = VideoWorkerReport.TryParse(result.Payload);
        var produced = report?.Video;

        var clip = new ImageJobImage(
            attachments[0].Bytes,
            string.IsNullOrWhiteSpace(attachments[0].MediaType) ? DefaultMediaType : attachments[0].MediaType,
            produced?.Size ?? request.Size,
            produced?.Seed,
            Steps: produced?.Steps ?? report?.Steps ?? request.Steps,
            Seconds: produced?.Seconds ?? request.Seconds);

        return new ImageOutcome
        {
            Status = 200,
            Units = MegapixelSteps(produced),
            UnitKind = UsageUnitKinds.MegapixelSteps,

            // Both, always — see UsageUnitKinds.VideoSeconds. The quota is spent in the first and the
            // question a human asks is answered by the second.
            SecondaryUnits = produced?.Seconds ?? 0d,
            SecondaryUnitKind = UsageUnitKinds.VideoSeconds,
            ImageCount = 1,
            Images = [clip],
            Summary = report?.Warnings is { Count: > 0 } warnings
                ? new ImageJobSummary(false, null, warnings)
                : ImageJobSummary.None
        };
    }

    /// <summary>
    /// <c>width × height × frames × steps / 1e6</c>, from what the worker <em>produced</em>.
    /// </summary>
    /// <remarks>
    /// Frames are in it because a video transformer denoises the whole latent stack on every step, so
    /// a 5-second clip really is ~30 SDXL images' worth of card and a counter that said otherwise
    /// would be wrong in the direction that scales with usage — which is the argument
    /// <see cref="UsageUnitKinds.MegapixelSteps"/>'s own docstring already makes about images.
    /// A recipe that clamped either number is billed for what it ran, not for what was asked.
    /// </remarks>
    public static double MegapixelSteps(GeneratedVideo? video) =>
        video is { Size: { } size, Steps: > 0, Frames: > 0 }
            ? size.Megapixels * video.Frames.Value * video.Steps!.Value
            : 0d;

    /// <summary>
    /// The <c>video</c> object, OpenAI's shape, written once.
    /// </summary>
    /// <param name="expiresAtUnixSeconds">
    /// When the bytes stop being fetchable. It is the retention window applied to the completion —
    /// the same window <see cref="ImageJobStore"/> enforces — rather than a promise this renderer
    /// makes on its own, so a client that plans around it is planning around the truth.
    /// </param>
    public static string Object(ImageJobRecord record, long? expiresAtUnixSeconds)
    {
        var video = new Dictionary<string, object?>
        {
            ["id"] = Identifier(record.Id),
            ["object"] = "video",
            ["model"] = record.Model,
            ["status"] = VideoStatuses.From(record.State),
            ["progress"] = Progress(record),
            ["created_at"] = record.CreatedAt.ToUnixTimeSeconds()
        };

        if (record.CompletedAt is { } completed)
        {
            video["completed_at"] = completed.ToUnixTimeSeconds();
        }

        if (expiresAtUnixSeconds is { } expires)
        {
            video["expires_at"] = expires;
        }

        // Absence stays absence (28 D5): before the worker answers, the hub genuinely does not know
        // the geometry unless the caller named it, and the recipe's own default is the node's
        // business (46 D6). A zero here would be the hub declaring a model's native resolution.
        if (record.Size is { } size)
        {
            video["size"] = size.ToString();
        }

        if (record.Seconds is { } seconds)
        {
            video["seconds"] = Math.Round(seconds, 2);
        }

        // OpenAI carries the failure inside the object rather than only on the HTTP status, because a
        // poll that returns 200 with `status: failed` is the normal way a client learns about it.
        if (record.Failure is { } failure)
        {
            video["error"] = new Dictionary<string, object?>
            {
                ["code"] = failure.ErrorCode ?? ImageJobReasons.WorkerError,
                ["message"] = failure.Message ?? record.Error
            };
        }
        else if (record.State is ImageJobStates.Failed or ImageJobStates.Cancelled)
        {
            video["error"] = new Dictionary<string, object?>
            {
                ["code"] = record.Reason ?? ImageJobReasons.WorkerError,
                ["message"] = record.Error
            };
        }

        return JsonSerializer.Serialize(video, Json);
    }

    /// <summary>
    /// OpenAI's <c>progress</c>: an integer percentage, derived from the step counts phase 47's
    /// chunk path already carries (D2).
    /// </summary>
    /// <remarks>
    /// <b>It never reaches 100 before the job is complete</b>, and that is not cosmetic: a client
    /// that sees 100 and stops polling has stopped one round trip before the bytes exist. The last
    /// step's frame is therefore capped at 99 and only a terminal state reports 100.
    /// </remarks>
    public static int Progress(ImageJobRecord record)
    {
        if (record.State is ImageJobStates.Succeeded or ImageJobStates.Expired)
        {
            return 100;
        }

        if (record.State is ImageJobStates.Queued || record.Step is not { } step || record.TotalSteps is not { } total)
        {
            return 0;
        }

        return total <= 0 ? 0 : Math.Clamp((int)(step * 100L / total), 0, 99);
    }

    /// <summary>
    /// When the bytes stop being fetchable: the completion plus the retention window, or null while
    /// there is nothing to expire.
    /// </summary>
    /// <remarks>
    /// Derived from the window the store actually enforces rather than stated separately, so the
    /// field cannot drift from the behaviour — which is the whole of 56 D2 rendered as a number.
    /// </remarks>
    public static long? ExpiresAt(ImageJobRecord record, ImageJobOptions options) =>
        record.CompletedAt is { } completed && record.State == ImageJobStates.Succeeded
            ? completed.AddSeconds(Math.Max(1, options.RetentionSeconds)).ToUnixTimeSeconds()
            : null;

    /// <summary>
    /// Why there are no bytes, as a status, a code and a sentence — written once so a hub and a solo
    /// node answer a fetched-twice clip identically.
    /// </summary>
    /// <remarks>
    /// A <c>410</c> that names <em>which</em> of the three ways it went beats a <c>404</c> that reads
    /// like a bug: "you were too late", "somebody already fetched it" and "that never existed" are
    /// three problems with three fixes and only one of them is the caller's mistake.
    /// </remarks>
    public static (int Status, string Message, string Code) Unavailable(
        ImageJobRecord record,
        ImageJobOptions options,
        string id)
    {
        if (record.State == ImageJobStates.Expired)
        {
            return (
                410,
                $"video '{id}' no longer holds its bytes ({record.Reason}). Results live for "
                + $"{options.RetentionSeconds}s and are dropped on delivery unless "
                + "Images:Jobs:KeepAfterRead is on; "
                + (options.Persists()
                    ? "the copy under Images:Jobs:DataDirectory was unlinked in the same operation."
                    : "nothing was written to disk."),
                "video_expired");
        }

        if (record.State != ImageJobStates.Succeeded)
        {
            return (
                409,
                $"video '{id}' is {VideoStatuses.From(record.State)}; there is nothing to fetch yet",
                "video_not_ready");
        }

        return (404, $"video '{id}' has no content", "video_not_found");
    }

    /// <summary>
    /// The id a client sees. OpenAI's ids are prefixed strings, and a bare GUID in a field a typed
    /// SDK prints beside <c>video_…</c> ids from another provider is a small, permanent confusion.
    /// </summary>
    public static string Identifier(Guid id) => "video_" + id.ToString("N");

    /// <summary>
    /// Reads one back. A malformed id is <c>false</c> and therefore the same <c>404</c> an unknown
    /// one earns — never a <c>400</c>, because "that is not a valid id" tells a caller their guess
    /// was well-formed enough to be checked.
    /// </summary>
    public static bool TryParseIdentifier(string? value, out Guid id)
    {
        id = Guid.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var body = trimmed.StartsWith("video_", StringComparison.Ordinal) ? trimmed[6..] : trimmed;

        return Guid.TryParse(body, out id);
    }
}

/// <summary>
/// The video worker's result payload. Every field is optional, for
/// <see cref="ImageWorkerReport"/>'s reason: a worker that returns bytes and no description still
/// produced a video, and failing the request over a missing field would turn a bookkeeping gap into
/// a user-visible outage.
/// </summary>
public sealed record VideoWorkerReport(int? Steps, GeneratedVideo? Video, IReadOnlyList<string>? Warnings)
{
    public static VideoWorkerReport? TryParse(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            var steps = Int(root, "steps");
            GeneratedVideo? video = null;

            if (root.TryGetProperty("videos", out var array)
                && array.ValueKind is JsonValueKind.Array
                && array.GetArrayLength() > 0)
            {
                var element = array[0];

                if (element.ValueKind is JsonValueKind.Object)
                {
                    var width = Int(element, "width");
                    var height = Int(element, "height");

                    video = new GeneratedVideo(
                        width is { } w && height is { } h ? new ImageSize(w, h) : null,
                        Int(element, "steps") ?? steps,
                        Long(element, "seed"),
                        Int(element, "frames"),
                        Number(element, "fps"),
                        Number(element, "seconds"));
                }
            }

            return new VideoWorkerReport(steps, video, Strings(root, "warnings"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static long? Long(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static double? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number && value.TryGetDouble(out var parsed)
            ? parsed
            : null;

    private static IReadOnlyList<string>? Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        var items = value.EnumerateArray()
            .Where(entry => entry.ValueKind is JsonValueKind.String)
            .Select(entry => entry.GetString()!)
            .ToArray();

        return items.Length > 0 ? items : null;
    }
}

/// <summary>
/// One clip as the worker described it. <see cref="Seconds"/> is <c>frames / fps</c> computed where
/// both are known — by the worker, which is the only place that knows what the encoder was told.
/// </summary>
public sealed record GeneratedVideo(
    ImageSize? Size,
    int? Steps,
    long? Seed,
    int? Frames,
    double? Fps,
    double? Seconds);
