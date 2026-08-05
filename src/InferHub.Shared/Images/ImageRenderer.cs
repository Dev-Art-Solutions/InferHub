using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Shared.Contracts;
using InferHub.Shared.OpenAi;

namespace InferHub.Shared.Images;

/// <summary>
/// What the client sees for an image request, decided once for both hosts (phase 46).
/// </summary>
/// <remarks>
/// <para>
/// It is not an <c>IResult</c> and it must not become one: design rule 2 keeps ASP.NET out of
/// <c>InferHub.Shared</c>, and phase-37 D6 draws the line at exactly this level — the frame
/// <em>bodies</em> are shared, the ten lines that write them to a response are per host.
/// </para>
/// <para>
/// Phase 42 arrived here by finding the bug: its brief gave each host its own endpoint file and left
/// parity to a test, and "an unproducible format is a 400 on a hub and a 502 on a solo node" is
/// exactly the kind of difference a parity suite finds late. This phase starts where that one ended
/// up.
/// </para>
/// </remarks>
public sealed record ImageOutcome
{
    public required int Status { get; init; }

    /// <summary>The JSON body, already serialized. Images are base64 inside it (D5 — there is no URL).</summary>
    public string? Json { get; init; }

    public string? Error { get; init; }

    public string ErrorType { get; init; } = OpenAiErrorTypes.ApiError;

    public string? ErrorCode { get; init; }

    public string? ErrorParam { get; init; }

    public int? RetryAfterSeconds { get; init; }

    /// <summary>
    /// What to meter, in the unit the work is actually in: megapixel-steps. Zero when there is
    /// nothing to charge for — a failed job is not billed.
    /// </summary>
    public double Units { get; init; }

    public string UnitKind { get; init; } = UsageUnitKinds.MegapixelSteps;

    /// <summary>How many images came back, for the log line. Never what is in them.</summary>
    public int ImageCount { get; init; }

    public bool IsError => Status >= 400;
}

public static class ImageRenderer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public const string ContentType = "application/json";

    /// <summary>
    /// Turns an image tool result into the response the caller gets.
    /// </summary>
    /// <param name="createdUnixSeconds">
    /// Passed in rather than read from the clock here, so a parity suite can compare two hosts'
    /// bytes without excusing a timestamp, and so nothing in <c>InferHub.Shared</c> depends on
    /// ambient time.
    /// </param>
    public static ImageOutcome Generation(ToolResult result, ImageGenerationRequest request, long createdUnixSeconds)
    {
        if (Failure(result) is { } failure)
        {
            return failure;
        }

        var report = ImageWorkerReport.TryParse(result.Payload);
        var attachments = result.Attachments ?? [];

        if (attachments.Count == 0)
        {
            return new ImageOutcome
            {
                Status = 502,
                Error = "the image worker returned no image"
            };
        }

        var items = new List<object>(attachments.Count);
        var units = 0d;

        for (var i = 0; i < attachments.Count; i++)
        {
            var attachment = attachments[i];

            if (attachment.Bytes.Length == 0)
            {
                return new ImageOutcome
                {
                    Status = 502,
                    Error = $"the image worker returned an empty file for image {i}"
                };
            }

            // The worker's own numbers, not the request's. A recipe may clamp a size or a step
            // count, and metering the *asked* figures would bill for work that was not done — the
            // same reason a transcription is metered from the duration the worker measured rather
            // than from the upload's byte count (phase-42 D7).
            var described = report?.ImageAt(i);
            var size = described?.Size ?? request.Size;
            var steps = described?.Steps ?? report?.Steps ?? request.Steps ?? 0;

            if (size is { } known && steps > 0)
            {
                units += known.Megapixels * steps;
            }

            items.Add(new
            {
                b64_json = Convert.ToBase64String(attachment.Bytes),

                // Additive extras beside OpenAI's own field. A client that has never heard of them
                // is unaffected; one that wants to reproduce an image needs both, and asking it to
                // guess the seed a worker chose would make `seed` useless for the thing it is for.
                size = size?.ToString(),
                seed = described?.Seed,
                revised_prompt = (string?)null
            });
        }

        return new ImageOutcome
        {
            Status = 200,
            Json = JsonSerializer.Serialize(new { created = createdUnixSeconds, data = items }, Json),
            Units = units,
            UnitKind = UsageUnitKinds.MegapixelSteps,
            ImageCount = attachments.Count
        };
    }

    /// <summary>
    /// The failure shapes. Nothing here reads the error <em>text</em> to decide a status — the node
    /// states the kind and this renders it (phase-29 D6, phase-41's <c>RetryAfterSeconds</c>,
    /// phase-42's <c>ToolErrorCodes</c>).
    /// </summary>
    /// <remarks>
    /// <b>This is where "a size the recipe does not support" is answered</b>, and it is a deviation
    /// from the phase brief worth reading. The brief had the edge validate a size against the
    /// recipe's aspect buckets — which the edge cannot do, because a recipe is a file on the node
    /// and the hub has no catalogue until phase 48. The <em>worker</em> is the authority, it answers
    /// with <c>invalid_request</c> and a message naming the sizes it has, and this renders the 400.
    /// It costs one round trip to find out; the alternative is publishing a model catalogue over the
    /// mesh, which is a phase and is phase 48.
    /// </remarks>
    private static ImageOutcome? Failure(ToolResult result)
    {
        if (result.Success)
        {
            return null;
        }

        if (result.RetryAfterSeconds is { } retryAfter)
        {
            return new ImageOutcome
            {
                Status = 503,
                Error = result.Error ?? "the tool is busy",
                ErrorCode = "tool_busy",
                RetryAfterSeconds = retryAfter
            };
        }

        if (ToolErrorCodes.IsClientError(result.ErrorCode))
        {
            return new ImageOutcome
            {
                Status = 400,
                Error = NodeErrorText.Readable(result.Error),
                ErrorType = OpenAiErrorTypes.InvalidRequest,
                ErrorCode = result.ErrorCode
            };
        }

        return new ImageOutcome
        {
            Status = 502,
            Error = NodeErrorText.Readable(result.Error),
            ErrorCode = result.ErrorCode
        };
    }
}

/// <summary>
/// The worker's result payload: what it actually produced, as opposed to what was asked for.
/// </summary>
/// <remarks>
/// Every field is optional and a payload that parses to nothing is not an error — a worker that
/// returns bytes and no description still produces images, it just cannot be metered precisely, and
/// failing the request over a missing field would turn a bookkeeping gap into a user-visible outage.
/// </remarks>
public sealed record ImageWorkerReport(int? Steps, IReadOnlyList<GeneratedImage> Images)
{
    public GeneratedImage? ImageAt(int index) => index < Images.Count ? Images[index] : null;

    public static ImageWorkerReport? TryParse(string? payloadJson)
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
            var images = new List<GeneratedImage>();

            if (root.TryGetProperty("images", out var array) && array.ValueKind is JsonValueKind.Array)
            {
                foreach (var element in array.EnumerateArray())
                {
                    if (element.ValueKind is not JsonValueKind.Object)
                    {
                        continue;
                    }

                    var width = Int(element, "width") ?? Int(root, "width");
                    var height = Int(element, "height") ?? Int(root, "height");

                    images.Add(new GeneratedImage(
                        width is { } w && height is { } h ? new ImageSize(w, h) : null,
                        Int(element, "steps") ?? steps,
                        Long(element, "seed")));
                }
            }

            return new ImageWorkerReport(steps, images);
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
}

public sealed record GeneratedImage(ImageSize? Size, int? Steps, long? Seed);
