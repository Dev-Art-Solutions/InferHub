using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Shared.Contracts;

namespace InferHub.Shared.Images;

/// <summary>
/// What an image request <em>does</em>. Three values, and the third is why there are two routes.
/// </summary>
public static class ImageOperations
{
    public const string Generate = "generate";

    /// <summary><c>/v1/images/edits</c>: a picture, a prompt, and optionally a mask.</summary>
    public const string Edit = "edit";

    /// <summary><c>/v1/images/variations</c>: a picture and nothing else.</summary>
    public const string Variation = "variation";

    public static bool IsEditing(string? operation) =>
        operation is Edit or Variation;
}

/// <summary>
/// A validated <c>POST /v1/images/edits</c> or <c>/v1/images/variations</c> request (phase 50).
/// </summary>
/// <remarks>
/// <para>
/// The client surface is OpenAI's, exactly — multipart, with <c>image</c>, <c>mask</c>, <c>prompt</c>,
/// <c>model</c>, <c>n</c>, <c>size</c> and <c>response_format</c> parts, and the two InferHub knobs
/// (<see cref="ImageExtensions.Strength"/>, <see cref="ImageExtensions.MaskConvention"/>) as headers
/// for phase-46 D1's reason.
/// </para>
/// <para>
/// <b>A variation takes no prompt, and that is a decision rather than an omission.</b> OpenAI's
/// variations API has no prompt field; adding one would be a second dialect for the sake of a
/// convenience that already exists, because <c>/v1/images/edits</c> <em>without</em> a mask is
/// exactly "img2img with a prompt". So a prompt on a variation is a <c>400</c> naming the other
/// route. "More of this picture" and "change this picture" are different requests, and only one of
/// them has words in it.
/// </para>
/// <para>
/// <b>A mask on a variation is a <c>400</c> too.</b> Accepting it would mean either ignoring it — a
/// silent substitution of the most surprising kind, since the caller can see their selection did
/// nothing only by looking at the result — or inpainting with an empty prompt, which is a different
/// operation than the one they named.
/// </para>
/// <para>
/// Validation lives here rather than in either host for phase-42 deviation 5's reason: both hosts
/// serve these routes and a client must not be able to tell which one answered. Reading a multipart
/// form is ASP.NET and stays per host; the sentences a caller reads, and the JSON the worker
/// receives, are decided once.
/// </para>
/// </remarks>
public sealed record ImageEditRequest(
    string Operation,
    string Model,
    string? Prompt,
    string? NegativePrompt,
    int Count,
    ImageSize? Size,
    int? Steps,
    double? Guidance,
    long? Seed,
    double? Strength,
    string MaskConvention,
    ToolAttachment Image,
    ToolAttachment? Mask) : IImageRequest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The role name the image part travels under. Never the caller's own filename.</summary>
    /// <remarks>
    /// <b>The filename is dropped on purpose</b> (phase-42 D5): what somebody called the file on
    /// their disk is metadata about their day, it is content in the sense rule 7 means, and it has
    /// no business crossing the mesh or landing in a scratch directory listing. What the worker
    /// needs is the <em>role</em>, and that is what it gets.
    /// </remarks>
    public const string ImagePart = "image";

    public const string MaskPart = "mask";

    public string Capability => CapabilityKinds.ImageEdit;

    /// <summary>
    /// The <c>413</c> sentence for a whole request, written here so both hosts refuse identically.
    /// </summary>
    public static string TooLargeRequest(long bytes, long maxBytes) =>
        $"this request sends {bytes} bytes in (image plus mask), over the {maxBytes}-byte limit " +
        "(Images:MaxRequestBytes)";

    public IReadOnlyList<ToolAttachment> Attachments =>
        Mask is null ? [Image] : [Image, Mask];

    /// <summary>The bytes travelling in, which is what the total-request budget is checked against.</summary>
    public long InputBytes => Image.Bytes.LongLength + (Mask?.Bytes.LongLength ?? 0);

    /// <summary>
    /// Parses and validates a multipart edit or variation.
    /// </summary>
    /// <param name="form">
    /// Reads one form field. A delegate for the same reason <see cref="ImageGenerationRequest"/>'s
    /// header reader is one: the hosts hold these in ASP.NET types this library must not see.
    /// </param>
    /// <param name="image">
    /// The uploaded picture, already read and already size-checked by the caller — a file over the
    /// attachment cap is a <c>413</c>, which is not this method's shape to return.
    /// </param>
    public static ImageEditRequest? TryParse(
        string operation,
        Func<string, string?> form,
        Func<string, string?> header,
        ToolAttachment? image,
        ToolAttachment? mask,
        ImageLimits limits,
        out string error,
        out string? errorParam)
    {
        error = string.Empty;
        errorParam = null;

        operation = string.IsNullOrWhiteSpace(operation)
            ? ImageOperations.Edit
            : operation.Trim().ToLowerInvariant();

        if (!ImageOperations.IsEditing(operation))
        {
            error = $"operation '{operation}' is not one this route serves. Use " +
                    $"'{ImageOperations.Edit}' or '{ImageOperations.Variation}'.";
            errorParam = "operation";
            return null;
        }

        var model = form("model");

        if (string.IsNullOrWhiteSpace(model))
        {
            error = "model is required";
            errorParam = "model";
            return null;
        }

        if (image is null)
        {
            error = $"an '{ImagePart}' part is required: this endpoint takes multipart/form-data " +
                    "with the picture to work from.";
            errorParam = ImagePart;
            return null;
        }

        var prompt = Blank(form("prompt"));

        if (operation == ImageOperations.Edit && prompt is null)
        {
            error = "prompt is required: an edit says what to change.";
            errorParam = "prompt";
            return null;
        }

        if (operation == ImageOperations.Variation && prompt is not null)
        {
            // Refused rather than ignored. A caller whose prompt vanished silently would conclude
            // the model ignores prompts, which is the wrong lesson about the right request on the
            // wrong route.
            error = "a variation takes no prompt — it is 'more of this picture'. Use " +
                    "POST /v1/images/edits with no mask for image-to-image with a prompt.";
            errorParam = "prompt";
            return null;
        }

        if (operation == ImageOperations.Variation && mask is not null)
        {
            error = "a variation takes no mask. Use POST /v1/images/edits to change part of a picture.";
            errorParam = MaskPart;
            return null;
        }

        var format = form("response_format");

        if (!string.IsNullOrWhiteSpace(format)
            && !string.Equals(format.Trim(), ImageResponseFormats.Base64, StringComparison.OrdinalIgnoreCase))
        {
            error = ImageResponseFormats.Refusal(format);
            errorParam = "response_format";
            return null;
        }

        var count = 1;

        if (Blank(form("n")) is { } rawCount)
        {
            if (!int.TryParse(rawCount, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out count)
                || count < 1)
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

        if (Blank(form("size")) is { } rawSize)
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

        if (!ImageExtensions.TryInt(header, ImageExtensions.Steps, 1, ImageExtensions.MaxSteps, out var steps, out error))
        {
            errorParam = ImageExtensions.Steps;
            return null;
        }

        if (!ImageExtensions.TryDouble(header, ImageExtensions.Guidance, 0, 50, out var guidance, out error))
        {
            errorParam = ImageExtensions.Guidance;
            return null;
        }

        if (!ImageExtensions.TryLong(header, ImageExtensions.Seed, out var seed, out error))
        {
            errorParam = ImageExtensions.Seed;
            return null;
        }

        // 0 keeps the input and 1 ignores it, so both ends are legal values rather than degenerate
        // ones — and neither is a default, because a recipe's own number is a better answer than
        // anything this edge could invent (phase-46 D6: the recipe is a file on the node).
        if (!ImageExtensions.TryDouble(header, ImageExtensions.Strength, 0, 1, out var strength, out error))
        {
            errorParam = ImageExtensions.Strength;
            return null;
        }

        var convention = MaskConventions.Normalise(header(ImageExtensions.MaskConvention));

        if (!MaskConventions.IsKnown(convention))
        {
            error = MaskConventions.Refusal(header(ImageExtensions.MaskConvention));
            errorParam = ImageExtensions.MaskConvention;
            return null;
        }

        return new ImageEditRequest(
            operation,
            model!.Trim(),
            prompt,
            Blank(form("negative_prompt")),
            count,
            size,
            steps,
            guidance,
            seed,
            strength,
            convention,
            image,
            mask);
    }

    /// <summary>
    /// What the worker receives.
    /// </summary>
    /// <remarks>
    /// <c>has_mask</c> is stated rather than left to be inferred from the attachment count, because
    /// "there are two files" and "the second one is a mask" are different facts and the worker
    /// should not have to guess which. Everything else is the generation payload plus two fields, so
    /// one worker entry point handles all three operations.
    /// </remarks>
    public string ToToolPayload() => JsonSerializer.Serialize(
        new
        {
            op = Operation,
            model = Model,
            prompt = Prompt,
            negative_prompt = NegativePrompt,
            width = Size?.Width,
            height = Size?.Height,
            steps = Steps,
            guidance = Guidance,
            seed = Seed,
            n = Count,
            strength = Strength,
            has_mask = Mask is not null,
            mask_convention = MaskConvention
        },
        Json);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
