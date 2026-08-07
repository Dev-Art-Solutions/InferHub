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

    /// <summary>
    /// The images themselves, already paired with what the worker said about each one.
    /// </summary>
    /// <remarks>
    /// Collected here rather than a second time by each caller: a hub that ran <c>ImageResults</c>
    /// once for the envelope and again for the job store would be two readings of one worker's
    /// answer, and the day they disagreed the difference would be a size in the JSON that did not
    /// match the size on the job document.
    /// </remarks>
    public IReadOnlyList<ImageJobImage> Images { get; init; } = [];

    /// <summary>What is true of the request as a whole: the trigger, and any warnings (phase 49).</summary>
    public ImageJobSummary Summary { get; init; } = ImageJobSummary.None;

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

        var attachments = result.Attachments ?? [];

        if (attachments.Count == 0)
        {
            return new ImageOutcome
            {
                Status = 502,
                Error = "the image worker returned no image"
            };
        }

        for (var i = 0; i < attachments.Count; i++)
        {
            if (attachments[i].Bytes.Length == 0)
            {
                return new ImageOutcome
                {
                    Status = 502,
                    Error = $"the image worker returned an empty file for image {i}"
                };
            }
        }

        var images = ImageResults.Collect(result, request);
        var summary = ImageResults.Summarise(result);

        return new ImageOutcome
        {
            Status = 200,
            Json = Envelope(images, summary, createdUnixSeconds),
            Units = Units(images),
            UnitKind = UsageUnitKinds.MegapixelSteps,
            ImageCount = images.Count,
            Images = images,
            Summary = summary
        };
    }

    /// <summary>
    /// The OpenAI Images envelope, written in exactly one place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three surfaces produce it — the hub's synchronous <c>/v1</c> route, the node's, and phase 46's
    /// direct render — and until phase 49 two of them built it by hand from their own local copy of
    /// the field list. That was one field away from a parity bug on every release that adds one, and
    /// this release adds four.
    /// </para>
    /// <para>
    /// <c>projection</c>, <c>prompt_augmented</c>, <c>trigger</c>, <c>seam_delta</c> and
    /// <c>warnings</c> are additive beside OpenAI's own fields (phase-46 D1's shape).
    /// <b><c>prompt_augmented</c> is present whether or not anything was appended</b>, for a recipe
    /// that <em>has</em> a trigger (D2): a client that had to infer "nothing happened" from a
    /// missing key is a client guessing about its own prompt. A recipe with no trigger reports
    /// neither, because a permanent <c>false</c> on every SDXL response is a field that means
    /// nothing.
    /// </para>
    /// <para>
    /// <b>The prompt itself is never echoed back.</b> <c>revised_prompt</c> stays null and the
    /// response carries the <em>recipe's</em> trigger constant instead — which is a fact about the
    /// model, not the caller's words, and is therefore the one half of this that may also be logged
    /// (rule 7).
    /// </para>
    /// </remarks>
    public static string Envelope(
        IReadOnlyList<ImageJobImage> images,
        ImageJobSummary summary,
        long createdUnixSeconds)
    {
        // Dictionaries rather than anonymous types, because which keys are PRESENT is part of the
        // contract here and an anonymous type can only express that through a global null-ignoring
        // policy. That policy is how the hub came to emit `revised_prompt: null` while a solo node
        // omitted it — for three releases, with a parity suite running.
        var items = new List<Dictionary<string, object?>>(images.Count);

        foreach (var image in images)
        {
            var item = new Dictionary<string, object?>
            {
                ["b64_json"] = Convert.ToBase64String(image.Bytes),

                // Additive extras beside OpenAI's own fields. A client that has never heard of them
                // is unaffected; one that wants to reproduce an image needs both, and asking it to
                // guess the seed a worker chose would make `seed` useless for the thing it is for.
                ["size"] = image.Size?.ToString(),
                ["seed"] = image.Seed,
                ["projection"] = ImageProjections.Normalise(image.Projection)
            };

            // Only where there is a seam to have. A flat recipe measures nothing, and a permanent
            // zero would read as "perfectly seamless" rather than "not applicable".
            if (image.SeamDelta is { } seam)
            {
                item["seam_delta"] = seam;
            }

            // OpenAI's own field, always present and always null: nothing here revises a prompt, and
            // phase 49's augmentation is reported as `trigger` — a recipe constant — rather than by
            // echoing the caller's own words back at them.
            item["revised_prompt"] = null;
            items.Add(item);
        }

        var envelope = new Dictionary<string, object?>
        {
            ["created"] = createdUnixSeconds,
            ["data"] = items
        };

        if (summary.Trigger is { } trigger)
        {
            envelope["prompt_augmented"] = summary.PromptAugmented;
            envelope["trigger"] = trigger;
        }

        if (summary.Warnings.Count > 0)
        {
            envelope["warnings"] = summary.Warnings;
        }

        return JsonSerializer.Serialize(envelope, Json);
    }

    /// <summary>
    /// Megapixel-steps, from what the worker <em>produced</em> rather than from what was asked for.
    /// </summary>
    /// <remarks>
    /// A recipe may clamp a size or a step count, and metering the asked figures would bill for work
    /// that was not done — the same reason a transcription is metered from the duration the worker
    /// measured rather than from the upload's byte count (phase-42 D7).
    /// </remarks>
    public static double Units(IEnumerable<ImageJobImage> images) =>
        images.Sum(image => image.Size is { } size && image.Steps is > 0 ? size.Megapixels * image.Steps.Value : 0d);

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
public sealed record ImageWorkerReport(
    int? Steps,
    IReadOnlyList<GeneratedImage> Images,
    string? Projection = null,
    bool PromptAugmented = false,
    string? Trigger = null,
    IReadOnlyList<string>? Warnings = null)
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
            var projection = ImageProjections.Normalise(Text(root, "projection"));
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
                        Long(element, "seed"),

                        // Per image, falling back to the request-level declaration. A worker that
                        // says it once for a batch is saying the true thing once rather than four
                        // times, and every recipe produces one projection per request.
                        ImageProjections.Normalise(Text(element, "projection") ?? projection),
                        Number(element, "seamDelta")));
                }
            }

            return new ImageWorkerReport(
                steps,
                images,
                projection,
                root.TryGetProperty("promptAugmented", out var augmented) && augmented.ValueKind is JsonValueKind.True,
                Text(root, "trigger"),
                Strings(root, "warnings"));
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

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
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
/// One image as the worker described it: geometry, seed, projection, and — for an equirectangular
/// recipe — how far apart its left and right edges are (phase 49, D5).
/// </summary>
public sealed record GeneratedImage(
    ImageSize? Size,
    int? Steps,
    long? Seed,
    string? Projection = null,
    double? SeamDelta = null);
