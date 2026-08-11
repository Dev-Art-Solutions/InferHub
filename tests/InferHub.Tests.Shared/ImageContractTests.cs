using System.Globalization;
using System.Text.Json;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;

namespace InferHub.Tests;

/// <summary>
/// The pure pieces of the image edge: the request parser, the size grammar, the budget arithmetic
/// and the renderer (phase 46). No host, no wire — these are the parts whose off-by-one costs
/// somebody an OOM or a torn-down connection, and a pure function is what a test can pin exhaustively.
/// </summary>
public class ImageContractTests
{
    [Theory]
    [InlineData("1024x1024", 1024, 1024)]
    [InlineData(" 1216X832 ", 1216, 832)]
    public void ASizeParsesInEitherCaseAndRoundTrips(string input, int width, int height)
    {
        Assert.True(ImageSize.TryParse(input, out var size, out _));
        Assert.Equal(new ImageSize(width, height), size);
        Assert.Equal($"{width}x{height}", size.ToString());
    }

    /// <summary>
    /// The locale trap <c>PrometheusFormatter</c> and <c>AudioContractTests</c> already guard, in a
    /// third place. A size rendered with the ambient culture on a Bulgarian or German host writes a
    /// group separator — <c>"1 024x1 024"</c> — and every client that round-trips the field breaks
    /// on exactly the machines nobody runs CI on.
    /// </summary>
    [Fact]
    public void ASizeRendersInvariantlyUnderACultureWithGroupSeparators()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("bg-BG");
            Assert.Equal("1024x1024", new ImageSize(1024, 1024).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("1020x1020", "multiple of 8")]
    [InlineData("1024x1020", "multiple of 8")]
    [InlineData("1024", "WIDTHxHEIGHT")]
    [InlineData("axb", "WIDTHxHEIGHT")]
    [InlineData("-1024x1024", "WIDTHxHEIGHT")]
    [InlineData("8x8", "outside 64-4096")]
    [InlineData("8192x1024", "outside 64-4096")]
    public void AMalformedSizeIsRefusedWithASentenceThatSaysWhy(string input, string expected)
    {
        Assert.False(ImageSize.TryParse(input, out _, out var error));
        Assert.Contains(expected, error);
    }

    /// <summary>
    /// The estimate must be an <b>upper</b> bound: an optimistic one admits a batch that then
    /// exceeds the SignalR message cap, and exceeding that cap is what tore the connection down in
    /// v3.10.0. Uncompressed RGBA is above anything a three-channel PNG can weigh.
    /// </summary>
    [Fact]
    public void TheByteEstimateIsAnUpperBoundAndScalesWithTheBatch()
    {
        var size = new ImageSize(1024, 1024);

        Assert.Equal(1024L * 1024 * 4, ImageLimits.EstimateBytes(size, 1));
        Assert.Equal(1024L * 1024 * 4 * 3, ImageLimits.EstimateBytes(size, 3));
    }

    [Fact]
    public void TheLargestBatchIsBoundedByBothTheByteBudgetAndTheCount()
    {
        var size = new ImageSize(512, 512);   // exactly 1 MiB each, as an upper bound

        // The byte budget bites first…
        Assert.Equal(2, new ImageLimits(4, (long)(2.5 * 1024 * 1024)).LargestBatchOf(size));

        // …then the count cap does, even when the budget would take more…
        Assert.Equal(4, new ImageLimits(4, 64 * 1024 * 1024).LargestBatchOf(size));

        // …and zero is a real answer: at this size, not even one fits.
        Assert.Equal(0, new ImageLimits(4, 512 * 1024).LargestBatchOf(size));
    }

    /// <summary>
    /// The clamp exists so a misconfiguration cannot reproduce the phase-42 teardown: an operator who
    /// raises the response budget without raising <c>Tools:MaxAttachmentBytes</c> gets no change
    /// rather than a fleet that drops nodes under load.
    /// </summary>
    [Fact]
    public void TheResponseBudgetIsClampedByTheAttachmentCap()
    {
        var options = new ImageEdgeOptions { MaxResponseBytes = 512L * 1024 * 1024 };

        Assert.Equal(2 * 1024 * 1024, options.Resolve(2 * 1024 * 1024).MaxResponseBytes);
        Assert.Equal(ToolAttachmentLimits.DefaultMaxBytes, options.Resolve(ToolAttachmentLimits.DefaultMaxBytes).MaxResponseBytes);
    }

    [Fact]
    public void AParsedRequestCarriesTheExtensionsAndOmitsAnAbsentSize()
    {
        var request = ImageGenerationRequest.TryParse(
            """{"model":"sdxl","prompt":"a cat","negative_prompt":"blurry","n":2}""",
            name => name switch
            {
                ImageGenerationRequest.StepsHeader => "12",
                ImageGenerationRequest.GuidanceHeader => "7.5",
                ImageGenerationRequest.SeedHeader => "99",
                _ => null
            },
            ImageLimits.Default,
            out var error,
            out _);

        Assert.NotNull(request);
        Assert.Equal(string.Empty, error);
        Assert.Equal(12, request!.Steps);
        Assert.Equal(7.5, request.Guidance);
        Assert.Equal(99, request.Seed);
        Assert.Null(request.Size);

        using var payload = JsonDocument.Parse(request.ToToolPayload());

        // Absent rather than guessed: "use the recipe's default" is the only honest thing to say when
        // nobody named a size, and 1024 is right for SDXL and ruinous for SD 1.5.
        Assert.False(payload.RootElement.TryGetProperty("width", out _));
        Assert.Equal("blurry", payload.RootElement.GetProperty("negative_prompt").GetString());
        Assert.Equal(2, payload.RootElement.GetProperty("n").GetInt32());
    }

    /// <summary>
    /// The body wins over the header for <c>seed</c>, because the body field is the one an SDK user
    /// finds first and a header quietly overriding it would be the silent substitution this codebase
    /// refuses everywhere else.
    /// </summary>
    [Fact]
    public void ASeedInTheBodyWinsOverTheHeader()
    {
        var request = ImageGenerationRequest.TryParse(
            """{"model":"sdxl","prompt":"a cat","seed":7}""",
            name => name == ImageGenerationRequest.SeedHeader ? "99" : null,
            ImageLimits.Default,
            out _,
            out _);

        Assert.Equal(7, request!.Seed);
    }

    [Theory]
    [InlineData("""{"prompt":"a cat"}""", "model is required", "model")]
    [InlineData("""{"model":"sdxl"}""", "prompt is required", "prompt")]
    [InlineData("""{"model":"sdxl","prompt":" "}""", "prompt is required", "prompt")]
    [InlineData("""{"model":"sdxl","prompt":"a","n":0}""", "positive integer", "n")]
    [InlineData("""{"model":"sdxl","prompt":"a","n":9}""", "Images:MaxBatch", "n")]
    [InlineData("""[]""", "must be a JSON object", null)]
    [InlineData("""not json""", "invalid JSON", null)]
    public void AnInvalidRequestNamesTheFieldItIsAbout(string body, string expected, string? param)
    {
        var request = ImageGenerationRequest.TryParse(body, _ => null, ImageLimits.Default, out var error, out var errorParam);

        Assert.Null(request);
        Assert.Contains(expected, error);
        Assert.Equal(param, errorParam);
    }

    [Fact]
    public void TheUrlResponseFormatRefusalExplainsItselfRatherThanJustListingValues()
    {
        ImageGenerationRequest.TryParse(
            """{"model":"sdxl","prompt":"a cat","response_format":"url"}""",
            _ => null,
            ImageLimits.Default,
            out var error,
            out var param);

        Assert.Contains("never stores a generated image", error);
        Assert.Contains("b64_json", error);
        Assert.Equal("response_format", param);
    }

    // ---- the renderer ---------------------------------------------------------------------------

    [Fact]
    public void TheRendererMetersFromTheWorkersNumbersRatherThanTheRequests()
    {
        var request = Request(size: "1024x1024", steps: 50);

        // The worker clamped both. Metering the *asked* figures would bill for work not done.
        var result = ToolResult.Succeeded(
            Guid.NewGuid(),
            """{"steps":20,"images":[{"width":512,"height":512,"steps":20,"seed":5}]}""",
            [new ToolAttachment("image-0.png", "image/png", [1, 2, 3])]);

        var outcome = ImageRenderer.Render(result, request, 1_700_000_000);

        Assert.Equal(200, outcome.Status);
        Assert.Equal(UsageUnitKinds.MegapixelSteps, outcome.UnitKind);
        Assert.Equal(512 * 512 * 20 / 1_000_000d, outcome.Units, 6);
        Assert.Equal(1, outcome.ImageCount);

        using var body = JsonDocument.Parse(outcome.Json!);
        Assert.Equal(1_700_000_000, body.RootElement.GetProperty("created").GetInt64());

        var item = Assert.Single(body.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal("512x512", item.GetProperty("size").GetString());
        Assert.Equal(5, item.GetProperty("seed").GetInt64());
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), item.GetProperty("b64_json").GetString());
    }

    /// <summary>
    /// A worker that returns bytes and no description still produced images. Failing the request over
    /// a missing bookkeeping field would turn an accounting gap into a user-visible outage.
    /// </summary>
    [Fact]
    public void AResultWithNoDescriptionStillRendersAndFallsBackToTheRequest()
    {
        var outcome = ImageRenderer.Render(
            ToolResult.Succeeded(Guid.NewGuid(), null, [new ToolAttachment("i.png", "image/png", [1])]),
            Request(size: "512x512", steps: 30),
            1);

        Assert.Equal(200, outcome.Status);
        Assert.Equal(512 * 512 * 30 / 1_000_000d, outcome.Units, 6);
    }

    [Fact]
    public void AResultWithNoImagesIsA502RatherThanAnEmptySuccess()
    {
        var outcome = ImageRenderer.Render(
            ToolResult.Succeeded(Guid.NewGuid(), """{"images":[]}"""),
            Request(),
            1);

        Assert.Equal(502, outcome.Status);
        Assert.Contains("returned no image", outcome.Error);
        Assert.Equal(0, outcome.Units);
    }

    [Fact]
    public void AnEmptyFileIsA502NamingWhichImageItWas()
    {
        var outcome = ImageRenderer.Render(
            ToolResult.Succeeded(
                Guid.NewGuid(),
                null,
                [new ToolAttachment("a.png", "image/png", [1]), new ToolAttachment("b.png", "image/png", [])]),
            Request(),
            1);

        Assert.Equal(502, outcome.Status);
        Assert.Contains("image 1", outcome.Error);
    }

    /// <summary>
    /// Nothing here reads the error <em>text</em> to pick a status: the node states which kind of
    /// failure it was and this renders it (phase-29 D6).
    /// </summary>
    [Theory]
    [InlineData(ToolErrorCodes.InvalidRequest, 400)]
    [InlineData(ToolErrorCodes.UnsupportedFormat, 400)]
    [InlineData(ToolErrorCodes.ModelUnavailable, 502)]
    [InlineData(null, 502)]
    public void AWorkerFailureRendersFromItsCodeAndNeverFromItsMessage(string? code, int expected)
    {
        var outcome = ImageRenderer.Render(
            ToolResult.Refused(Guid.NewGuid(), "something went wrong", code),
            Request(),
            1);

        Assert.Equal(expected, outcome.Status);
        Assert.Equal(0, outcome.Units);
    }

    [Fact]
    public void ABusyToolRendersAs503WithTheNodesOwnRetryAfter()
    {
        var outcome = ImageRenderer.Render(
            ToolResult.Retry(Guid.NewGuid(), "every worker is busy", 12),
            Request(),
            1);

        Assert.Equal(503, outcome.Status);
        Assert.Equal(12, outcome.RetryAfterSeconds);
        Assert.Equal("tool_busy", outcome.ErrorCode);
    }

    // ---- phase 50: the edit contract -----------------------------------------------------------

    /// <summary>
    /// The worker payload an edit becomes. <c>has_mask</c> is <em>stated</em> rather than inferred
    /// from the attachment count, because "there are two files" and "the second one is a mask" are
    /// different facts and a worker should not have to guess which.
    /// </summary>
    [Fact]
    public void AnEditPayloadStatesItsOperationItsStrengthAndWhetherThereIsAMask()
    {
        var request = Edit(mask: true, headers: (ImageExtensions.Strength, "0.4"));

        using var payload = JsonDocument.Parse(request.ToToolPayload());

        Assert.Equal("edit", payload.RootElement.GetProperty("op").GetString());
        Assert.Equal(0.4, payload.RootElement.GetProperty("strength").GetDouble(), 5);
        Assert.True(payload.RootElement.GetProperty("has_mask").GetBoolean());
        Assert.Equal("openai", payload.RootElement.GetProperty("mask_convention").GetString());

        // Two attachments, in order, named for their role rather than for whatever the caller
        // called the file on their disk (phase-42 D5).
        Assert.Equal(["image", "mask"], request.Attachments.Select(a => a.Name));
        Assert.Equal(CapabilityKinds.ImageEdit, request.Capability);
    }

    /// <summary>
    /// Absent, <c>strength</c> is simply not in the payload — the <b>recipe's</b> default applies,
    /// and the edge does not have one to invent. A number chosen here would be the edge deciding how
    /// far an edit moves away from somebody's photograph.
    /// </summary>
    [Fact]
    public void AnAbsentStrengthIsAbsentFromThePayloadRatherThanDefaulted()
    {
        using var payload = JsonDocument.Parse(Edit().ToToolPayload());

        Assert.False(payload.RootElement.TryGetProperty("strength", out _));
    }

    [Fact]
    public void AVariationCarriesNoPromptAtAll()
    {
        using var payload = JsonDocument.Parse(Edit(operation: "variation", prompt: null).ToToolPayload());

        Assert.Equal("variation", payload.RootElement.GetProperty("op").GetString());
        Assert.False(payload.RootElement.TryGetProperty("prompt", out _));
    }

    [Theory]
    [InlineData("openai", true)]
    [InlineData("LUMINANCE", true)]
    [InlineData(" openai ", true)]
    [InlineData("inverted", false)]
    [InlineData("white", false)]
    public void OnlyTheTwoConventionsAreKnown(string value, bool known)
        => Assert.Equal(known, MaskConventions.IsKnown(value));

    /// <summary>
    /// Absent means OpenAI's, because that is the API this endpoint claims to speak. Defaulting to
    /// the other one would silently invert every mask written against the documented behaviour.
    /// </summary>
    [Fact]
    public void AnAbsentMaskConventionIsOpenAis()
        => Assert.Equal(MaskConventions.OpenAi, MaskConventions.Normalise(null));

    private static ImageEditRequest Edit(
        string operation = "edit",
        string? prompt = "a tall window",
        bool mask = false,
        params (string Name, string Value)[] headers)
    {
        var fields = new Dictionary<string, string> { ["model"] = "sdxl" };

        if (prompt is not null)
        {
            fields["prompt"] = prompt;
        }

        return ImageEditRequest.TryParse(
            operation,
            name => fields.GetValueOrDefault(name),
            name => headers.FirstOrDefault(h => h.Name == name).Value,
            new ToolAttachment("image", "image/png", [1, 2, 3]),
            mask ? new ToolAttachment("mask", "image/png", [4, 5, 6]) : null,
            ImageLimits.Default,
            out var error,
            out _) ?? throw new InvalidOperationException(error);
    }

    private static ImageGenerationRequest Request(string? size = null, int? steps = null)
        => ImageGenerationRequest.TryParse(
            $$"""{"model":"sdxl","prompt":"a cat"{{(size is null ? "" : $",\"size\":\"{size}\"")}}}""",
            name => name == ImageGenerationRequest.StepsHeader ? steps?.ToString() : null,
            ImageLimits.Default,
            out _,
            out _)!;
}
