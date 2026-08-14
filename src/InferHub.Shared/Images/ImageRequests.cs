using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Shared.Images;

/// <summary>
/// The output shapes a caller may ask for. <c>url</c> is deliberately not one of them.
/// </summary>
public static class ImageResponseFormats
{
    public const string Base64 = "b64_json";

    /// <summary>
    /// OpenAI's other value, and the one this API refuses (phase-46 D5).
    /// </summary>
    /// <remarks>
    /// Serving a URL means the hub keeps the bytes, and keeping the bytes means an image store, a
    /// retention window, a deletion endpoint and a question about whose pictures those are that
    /// nobody in this repository has agreed to answer. Design rule 4 (no persisted state) and rule 7
    /// (no content) point at the same refusal, so it is a <c>400</c> that names the alternative
    /// rather than a feature that quietly turns a fleet into an archive.
    /// </remarks>
    public const string Url = "url";

    public static bool IsKnown(string? format) => format is Base64 or Url;

    public static string Refusal(string? asked) =>
        string.Equals(asked?.Trim(), Url, StringComparison.OrdinalIgnoreCase)
            ? "response_format=url is not supported: this coordinator never stores a generated image, so there is " +
              $"no URL to serve it from. Use response_format={Base64}."
            : $"response_format '{asked}' is not supported. Use {Base64}.";
}

/// <summary>A width and a height, parsed from OpenAI's <c>"1024x1024"</c> spelling.</summary>
/// <remarks>
/// <para>
/// The edge validates the <em>syntax</em> and the arithmetic it can do without knowing anything
/// about a model. Whether a recipe actually supports a size is the <b>worker's</b> question and is
/// answered with an <c>invalid_request</c> code the edge renders as a 400 — see the deviation note
/// on <see cref="ImageRenderer"/>. SDXL in particular was trained on a fixed set of aspect buckets,
/// and a size outside them does not fail: it produces duplicated limbs and doubled horizons, which
/// reads as "this model is bad" rather than "you asked for 1000×1000".
/// </para>
/// <para>
/// A multiple of 8 is not a style preference. Every latent-diffusion pipeline downsamples by 8, and
/// a dimension that is not a multiple of it is either rejected by the library or silently rounded —
/// and a silently rounded size means the caller's declared size and the bytes they got disagree,
/// which is the class of thing this codebase refuses everywhere else.
/// </para>
/// </remarks>
public readonly record struct ImageSize(int Width, int Height)
{
    public const int MinDimension = 64;

    public const int MaxDimension = 4096;

    public long Pixels => (long)Width * Height;

    public double Megapixels => Pixels / 1_000_000d;

    /// <summary>Round-trips OpenAI's spelling, invariantly. A culture that formats 1024 with a group separator would produce "1 024x1 024".</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}");

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
            error = $"size '{value}' is not a 'WIDTHxHEIGHT' pair, for example '1024x1024'";
            return false;
        }

        if (width is < MinDimension or > MaxDimension || height is < MinDimension or > MaxDimension)
        {
            error = $"size '{value}' is outside {MinDimension}-{MaxDimension} pixels per side";
            return false;
        }

        if (width % 8 != 0 || height % 8 != 0)
        {
            error = $"size '{value}' must have both sides a multiple of 8 (every latent-diffusion pipeline downsamples by 8)";
            return false;
        }

        size = new ImageSize(width, height);
        return true;
    }
}

/// <summary>
/// The limits the edge can enforce before a single diffusion step runs, and the one place they are
/// decided so a hub and a solo node cannot disagree about what fits.
/// </summary>
/// <param name="MaxRequestBytes">
/// What an <em>edit</em> may send in — the picture and the mask together (phase 50, D4). Over it is
/// a <c>413</c> at the edge, before anything is buffered onward, naming the limit: a caller told
/// only "too large" finds the ceiling by bisection.
/// </param>
public sealed record ImageLimits(
    int MaxBatch,
    long MaxResponseBytes,
    long MaxRequestBytes = Contracts.ToolAttachmentLimits.DefaultMaxBytes)
{
    public const int DefaultMaxBatch = 4;

    /// <summary>
    /// An <b>upper bound</b> on what a PNG of a given size can weigh: uncompressed RGBA. PNG carries
    /// three channels and compresses, so the real file is always smaller — which is the direction
    /// this estimate has to err in. An optimistic estimate would admit a batch that then exceeds the
    /// SignalR message cap, and exceeding that cap <em>tears down the connection</em> rather than
    /// failing the message: the exact bug that shipped v3.10.0 dead on arrival with a 300 KB WAV.
    /// </summary>
    public const int UpperBoundBytesPerPixel = 4;

    public static ImageLimits Default { get; } =
        new(DefaultMaxBatch, Contracts.ToolAttachmentLimits.DefaultMaxBytes);

    public static long EstimateBytes(ImageSize size, int count) =>
        size.Pixels * UpperBoundBytesPerPixel * count;

    /// <summary>The largest <c>n</c> of this size that fits the budget. Zero when even one will not.</summary>
    public int LargestBatchOf(ImageSize size) =>
        (int)Math.Min(MaxBatch, MaxResponseBytes / Math.Max(1, size.Pixels * UpperBoundBytesPerPixel));

    /// <summary>
    /// The only budget check the edge can do, and it is done <b>before a step runs</b> rather than
    /// after a minute of GPU. The refusal names the largest <c>n</c> that fits, so the caller is not
    /// left bisecting.
    /// </summary>
    /// <remarks>
    /// Shared by all three routes since phase 50: a generation and an edit produce the same bytes
    /// through the same wire, and two copies of this arithmetic would be two answers to "what fits".
    /// </remarks>
    public bool Fits(ImageSize size, int count, out string error, out string errorParam)
    {
        error = string.Empty;
        errorParam = string.Empty;

        if (EstimateBytes(size, count) <= MaxResponseBytes)
        {
            return true;
        }

        var largest = LargestBatchOf(size);

        error = largest >= 1
            ? $"{count} image(s) at {size} may exceed the {MaxResponseBytes}-byte response budget " +
              $"(Images:MaxResponseBytes). At this size the largest n is {largest}."
            : $"one image at {size} may exceed the {MaxResponseBytes}-byte response budget " +
              "(Images:MaxResponseBytes). Ask for a smaller size, or raise the budget and " +
              "Tools:MaxAttachmentBytes with it.";

        errorParam = largest >= 1 ? "n" : "size";
        return false;
    }
}

/// <summary>
/// What every image request has in common, whichever of the three routes it arrived on (phase 50).
/// </summary>
/// <remarks>
/// <para>
/// It exists so the job registry, the job runner and the renderer take <em>an image request</em>
/// rather than a generation: phase 47's queue, its per-step progress, its cancel, its retention and
/// its metering are the same for an edit as for a generation, and duplicating them per operation
/// would be two queues with two ideas of fairness on one GPU — which is exactly what phase 47's own
/// D1 refused to build for the synchronous route.
/// </para>
/// <para>
/// <see cref="Capability"/> is what makes the split cheap: <c>image</c> or <c>image-edit</c> is
/// decided by the request that was parsed, and everything downstream routes on it without knowing
/// which route produced it.
/// </para>
/// </remarks>
public interface IImageRequest
{
    /// <summary>The recipe id, which is the routing key's second half.</summary>
    string Model { get; }

    /// <summary><c>image</c> or <c>image-edit</c> — the routing key's first half.</summary>
    string Capability { get; }

    int Count { get; }

    /// <summary>Null when the caller named none: the recipe's default, or the input's own size.</summary>
    ImageSize? Size { get; }

    int? Steps { get; }

    /// <summary>The worker's payload. What it does <em>not</em> contain is anything a hub logs.</summary>
    string ToToolPayload();

    /// <summary>Bytes travelling <em>in</em>: an image, and a mask when there is one.</summary>
    IReadOnlyList<Contracts.ToolAttachment> Attachments { get; }
}

/// <summary>
/// A validated <c>POST /v1/images/generations</c> request, and the tool payload it becomes.
/// </summary>
/// <remarks>
/// <para>
/// Validation lives here rather than in either host because both hosts serve this route and a client
/// must not be able to tell which one answered it (phase-37 D6, phase-42 deviation 5). Reading a body
/// and a header is ASP.NET and stays per host — design rule 2 keeps ASP.NET out of this library — but
/// the sentences a caller reads, and the JSON the worker receives, are decided once.
/// </para>
/// <para>
/// <b>The InferHub extensions travel as headers, with one exception</b> (D1). <c>steps</c>,
/// <c>guidance</c> and <c>seed</c> are headers because a body field would collide with whatever
/// OpenAI adds next and would make a typed SDK's request object wrong, while a header is additive by
/// construction — phase-24 D5's shape, including "an unknown value is a 400, never a silent
/// fallback". <c>seed</c> is <em>also</em> accepted in the body because it is the first thing anyone
/// reaches for and a header-only seed would be the most-asked question in the release thread.
/// </para>
/// <para>
/// <b><c>negative_prompt</c> is a body field and never a header</b>, and that is rule 7 rather than
/// taste: a negative prompt is the caller's own words, and a header is the one part of a request
/// that every proxy, gateway and access log in the path writes down by default.
/// </para>
/// </remarks>
public sealed record ImageGenerationRequest(
    string Model,
    string Prompt,
    string? NegativePrompt,
    int Count,
    ImageSize? Size,
    int? Steps,
    double? Guidance,
    long? Seed,
    string? SeamRepair = null) : IImageRequest
{
    public string Capability => Contracts.CapabilityKinds.Image;

    /// <summary>Nothing travels in with a generation. The prompt is the whole input.</summary>
    public IReadOnlyList<Contracts.ToolAttachment> Attachments => [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public const string StepsHeader = ImageExtensions.Steps;

    public const string GuidanceHeader = ImageExtensions.Guidance;

    public const string SeedHeader = ImageExtensions.Seed;

    /// <summary>The ceiling on a header-supplied step count. A recipe may cap it lower.</summary>
    public const int MaxSteps = ImageExtensions.MaxSteps;

    /// <summary>
    /// Parses and validates the body and the three extension headers. Returns null and sets
    /// <paramref name="error"/> on the first problem, in the order a caller would hit them.
    /// </summary>
    /// <param name="header">
    /// Reads one request header. A delegate rather than a dictionary because the two hosts hold
    /// headers in ASP.NET types this library must not see.
    /// </param>
    public static ImageGenerationRequest? TryParse(
        string rawJson,
        Func<string, string?> header,
        ImageLimits limits,
        out string error,
        out string? errorParam)
    {
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

        var format = Text(root, "response_format");

        if (!string.IsNullOrWhiteSpace(format))
        {
            format = format.Trim().ToLowerInvariant();

            if (format != ImageResponseFormats.Base64)
            {
                error = ImageResponseFormats.Refusal(Text(root, "response_format"));
                errorParam = "response_format";
                return null;
            }
        }

        var count = 1;

        if (root.TryGetProperty("n", out var nElement) && nElement.ValueKind is not JsonValueKind.Null)
        {
            if (nElement.ValueKind is not JsonValueKind.Number || !nElement.TryGetInt32(out count) || count < 1)
            {
                error = "n must be a positive integer";
                errorParam = "n";
                return null;
            }

            if (count > limits.MaxBatch)
            {
                error = $"n must be at most {limits.MaxBatch} (Images:MaxBatch)";
                errorParam = "n";
                return null;
            }
        }

        ImageSize? size = null;

        if (Text(root, "size") is { } rawSize && !string.IsNullOrWhiteSpace(rawSize))
        {
            if (!ImageSize.TryParse(rawSize, out var parsed, out var sizeError))
            {
                error = sizeError;
                errorParam = "size";
                return null;
            }

            if (!limits.Fits(parsed, count, out error, out var budgetParam))
            {
                errorParam = budgetParam;
                return null;
            }

            size = parsed;
        }

        if (!TryHeaderInt(header, StepsHeader, 1, MaxSteps, out var steps, out error))
        {
            errorParam = StepsHeader;
            return null;
        }

        if (!TryHeaderDouble(header, GuidanceHeader, 0, 50, out var guidance, out error))
        {
            errorParam = GuidanceHeader;
            return null;
        }

        var seed = Integer(root, "seed");

        if (seed is null)
        {
            if (!TryHeaderLong(header, SeedHeader, out seed, out error))
            {
                errorParam = SeedHeader;
                return null;
            }
        }

        if (!ImageExtensions.TrySeamRepair(header, out var seamRepair, out error))
        {
            errorParam = ImageExtensions.SeamRepair;
            return null;
        }

        return new ImageGenerationRequest(
            model!.Trim(),
            prompt!,
            Blank(Text(root, "negative_prompt")),
            count,
            size,
            steps,
            guidance,
            seed,
            seamRepair);
    }

    /// <summary>
    /// What the worker receives. <c>width</c>/<c>height</c> are absent when the caller named no
    /// size, which is how "use the recipe's default" is said — a number invented at the edge would
    /// be the edge deciding a model's native resolution, and 1024 is right for SDXL and ruinous for
    /// SD 1.5.
    /// </summary>
    public string ToToolPayload() => JsonSerializer.Serialize(
        new
        {
            model = Model,
            prompt = Prompt,
            negative_prompt = NegativePrompt,
            width = Size?.Width,
            height = Size?.Height,
            steps = Steps,
            guidance = Guidance,
            seed = Seed,
            n = Count,

            // Absent unless somebody asked, so a worker that never heard of phase 55 receives the
            // payload it received in v3.22 — and one that has heard of it can tell "no repair" from
            // "this hub does not know about repair" the only way that matters: neither spends a step.
            seam_repair = SeamRepair
        },
        Json);

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

    private static bool TryHeaderInt(
        Func<string, string?> header,
        string name,
        int min,
        int max,
        out int? value,
        out string error) => ImageExtensions.TryInt(header, name, min, max, out value, out error);

    private static bool TryHeaderDouble(
        Func<string, string?> header,
        string name,
        double min,
        double max,
        out double? value,
        out string error) => ImageExtensions.TryDouble(header, name, min, max, out value, out error);

    private static bool TryHeaderLong(Func<string, string?> header, string name, out long? value, out string error)
        => ImageExtensions.TryLong(header, name, out value, out error);
}

/// <summary>
/// The <c>X-InferHub-Image-*</c> extension headers, and the one place they are parsed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The InferHub extensions travel as headers</b> (phase-46 D1, phase-24 D5's shape): a body field
/// would collide with whatever OpenAI adds to this API next and would make a typed SDK's request
/// object wrong, while a header is additive by construction. <b>An unknown value is a 400, never a
/// silent fallback</b>, and the message names what is acceptable.
/// </para>
/// <para>
/// Culture is invariant everywhere, always. A German or Bulgarian client sending <c>0,75</c> gets a
/// clean refusal rather than a number this host happens to parse and another host does not — the
/// same reasoning that keeps a decimal comma out of the Prometheus exposition (phase-28) and out of
/// a WebVTT timestamp (phase-42).
/// </para>
/// </remarks>
public static class ImageExtensions
{
    public const string Steps = "X-InferHub-Image-Steps";

    public const string Guidance = "X-InferHub-Image-Guidance";

    public const string Seed = "X-InferHub-Image-Seed";

    /// <summary>
    /// How far an edit moves away from the picture it was given, 0–1 (phase 50, D3).
    /// </summary>
    /// <remarks>
    /// OpenAI's edits API has no <c>strength</c>, and image-to-image without one is meaningless —
    /// it is the whole knob. Absent, the recipe's <c>defaults.strength</c> applies; a recipe with
    /// neither is a <c>400</c> from the worker naming this header, because the edge cannot know
    /// what a recipe defaults to (phase-46 D6: a recipe is a file on the node).
    /// </remarks>
    public const string Strength = "X-InferHub-Image-Strength";

    /// <summary>Which way round the caller's mask reads (phase 50, D2).</summary>
    public const string MaskConvention = "X-InferHub-Mask-Convention";

    /// <summary>
    /// Close this panorama's join, and by which mechanism (phase 55, D1).
    /// </summary>
    /// <remarks>
    /// Absent means today's behaviour byte for byte, including the warning. The values are
    /// <see cref="SeamRepairModes"/>' two mechanisms, and an unknown one is a <c>400</c> — the same
    /// "never a silent fallback" every other header here obeys, and it matters more than usual: a
    /// caller whose repair silently did not happen sees a picture with a line in it and concludes
    /// the feature does not work.
    /// </remarks>
    public const string SeamRepair = "X-InferHub-Image-Seam-Repair";

    /// <summary>
    /// Parses <see cref="SeamRepair"/>. Null when absent or explicitly <c>off</c> — the two are the
    /// same request, and a payload field that said "off" would give the worker a third thing to
    /// mean nothing.
    /// </summary>
    internal static bool TrySeamRepair(Func<string, string?> header, out string? value, out string error)
    {
        value = null;
        error = string.Empty;

        var raw = header(SeamRepair);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var normalised = SeamRepairModes.Normalise(raw);

        if (normalised == SeamRepairModes.Off)
        {
            return true;
        }

        if (!SeamRepairModes.IsMechanism(normalised))
        {
            error = SeamRepairModes.Refusal(raw);
            return false;
        }

        value = normalised;
        return true;
    }

    /// <summary>The ceiling on a header-supplied step count. A recipe may cap it lower.</summary>
    public const int MaxSteps = 150;

    internal static bool TryInt(
        Func<string, string?> header,
        string name,
        int min,
        int max,
        out int? value,
        out string error)
    {
        value = null;
        error = string.Empty;

        var raw = header(name);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < min
            || parsed > max)
        {
            error = $"{name}: '{raw}' is not an integer between {min} and {max}";
            return false;
        }

        value = parsed;
        return true;
    }

    internal static bool TryDouble(
        Func<string, string?> header,
        string name,
        double min,
        double max,
        out double? value,
        out string error)
    {
        value = null;
        error = string.Empty;

        var raw = header(name);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed)
            || parsed < min
            || parsed > max)
        {
            error = $"{name}: '{raw}' is not a number between {min} and {max} (use a decimal point)";
            return false;
        }

        value = parsed;
        return true;
    }

    internal static bool TryLong(Func<string, string?> header, string name, out long? value, out string error)
    {
        value = null;
        error = string.Empty;

        var raw = header(name);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            error = $"{name}: '{raw}' is not a non-negative integer";
            return false;
        }

        value = parsed;
        return true;
    }
}
