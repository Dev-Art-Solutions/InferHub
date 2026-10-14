using System.Net;
using System.Text.Json;
using InferHub.Shared.Audio;
using InferHub.Shared.CloudMedia;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;

namespace InferHub.Tests;

/// <summary>
/// Phase 104 D1/D3–D6: the cloud client against <see cref="FakeMediaVendor"/>, read back through the
/// <em>same</em> renderers a local worker's answer goes through — so "a cloud picture renders like a
/// GPU picture" is asserted on the envelope a caller gets, not on the client's own idea of it.
/// </summary>
public sealed class CloudMediaClientTests : IDisposable
{
    private const string Key = "sk-test-cloud-media-0123456789";

    private readonly FakeMediaVendor vendor = new();
    private readonly HttpClient http;
    private readonly CloudMediaClient client;

    public CloudMediaClientTests()
    {
        http = new HttpClient(vendor.Borrowed());
        client = new CloudMediaClient(http);
    }

    public void Dispose()
    {
        http.Dispose();
        vendor.Dispose();
    }

    private static CloudMediaRoute OpenAi(string capability, string upstream) =>
        new("openai", CloudMediaDialects.OpenAi, "https://api.openai.test/v1", Key, capability, "local", upstream, 30);

    private static CloudMediaRoute Gemini(string capability, string upstream) =>
        new("google", CloudMediaDialects.Gemini, "https://gemini.test/v1beta", Key, capability, "local", upstream, 30);

    private static ToolJob Job(string capability, object payload, params ToolAttachment[] attachments) =>
        new(Guid.NewGuid(), capability, "local", JsonSerializer.Serialize(payload), attachments.Length == 0 ? null : attachments);

    private static ImageGenerationRequest Request(int n = 1, ImageSize? size = null) =>
        new("local", "a lighthouse", null, n, size, null, null, null);

    // ---- pictures --------------------------------------------------------------------------------

    [Fact]
    public async Task AnOpenAiPictureRendersAsTheImagesEnvelopeAndMetersOneStepPerMegapixel()
    {
        var job = Job(CapabilityKinds.Image, new { prompt = "a lighthouse", n = 2, width = 1024, height = 1024 });

        var result = await client.RunAsync(job, OpenAi(CapabilityKinds.Image, "gpt-image-1.5"), null, CancellationToken.None);
        var outcome = ImageRenderer.Render(result, Request(2), 1);

        Assert.Equal(200, outcome.Status);
        Assert.Equal(2, outcome.ImageCount);
        Assert.Equal(2 * 1.048576, outcome.Units, precision: 6);

        var sent = Assert.Single(vendor.Requests);
        Assert.Equal("/v1/images/generations", sent.Path);
        Assert.Equal("gpt-image-1.5", sent.Json.GetProperty("model").GetString());
        Assert.Equal("1024x1024", sent.Json.GetProperty("size").GetString());
        Assert.Equal(2, sent.Json.GetProperty("n").GetInt32());

        // gpt-image refuses response_format; only a dall-e model is asked for base64.
        Assert.False(sent.Json.TryGetProperty("response_format", out _));
        Assert.Equal($"Bearer {Key}", sent.Headers["Authorization"]);

        using var envelope = JsonDocument.Parse(outcome.Json!);
        Assert.Equal("1024x1024", envelope.RootElement.GetProperty("data")[0].GetProperty("size").GetString());
    }

    [Fact]
    public async Task StepsGuidanceAndSeedAreAnsweredWithoutAndSaidSoInTheEnvelope()
    {
        var job = Job(CapabilityKinds.Image, new { prompt = "p", steps = 30, guidance = 7.5, seed = 42 });

        var result = await client.RunAsync(job, OpenAi(CapabilityKinds.Image, "gpt-image-1.5"), null, CancellationToken.None);
        var outcome = ImageRenderer.Render(result, Request(), 1);

        Assert.Equal(200, outcome.Status);
        Assert.Equal(3, outcome.Summary.Warnings.Count);
        Assert.Contains(outcome.Summary.Warnings, w => w.StartsWith("seed is not used", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("negative_prompt", "blurry")]
    [InlineData("seam_repair", "inpaint")]
    [InlineData("reproject", "cubemap")]
    public async Task WhatWouldChangeThePictureIsRefusedBeforeTheVendorIsCalled(string field, string value)
    {
        var payload = new Dictionary<string, object> { ["prompt"] = "p", [field] = value };

        var result = await client.RunAsync(Job(CapabilityKinds.Image, payload), OpenAi(CapabilityKinds.Image, "gpt-image-1.5"), null, CancellationToken.None);

        Assert.Equal(400, ImageRenderer.Render(result, Request(), 1).Status);
        Assert.Empty(vendor.Requests);
    }

    [Fact]
    public async Task AnImagenPictureGoesToPredictWithTheAspectRatioAndTheKeyInGooglesHeader()
    {
        vendor.DrawSize = (1408, 768);
        var job = Job(CapabilityKinds.Image, new { prompt = "p", n = 2, width = 1600, height = 900 });

        var result = await client.RunAsync(job, Gemini(CapabilityKinds.Image, "imagen-4.0-generate-001"), null, CancellationToken.None);
        var outcome = ImageRenderer.Render(result, Request(2, new ImageSize(1600, 900)), 1);

        Assert.Equal(200, outcome.Status);
        Assert.Equal(2, outcome.ImageCount);

        var sent = Assert.Single(vendor.Requests);
        Assert.Equal("/v1beta/models/imagen-4.0-generate-001:predict", sent.Path);
        Assert.Equal("16:9", sent.Json.GetProperty("parameters").GetProperty("aspectRatio").GetString());
        Assert.Equal(Key, sent.Headers["x-goog-api-key"]);
        Assert.False(sent.Headers.ContainsKey("Authorization"));

        // The vendor's pixels, read off its PNG — and the envelope says the size was a ratio.
        Assert.Equal(new ImageSize(1408, 768), outcome.Images[0].Size);
        Assert.Contains(outcome.Summary.Warnings, w => w.Contains("aspect ratio 16:9", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASizeThatIsNoGeminiAspectRatioIsRefusedNamingTheRatios()
    {
        var job = Job(CapabilityKinds.Image, new { prompt = "p", width = 1000, height = 700 });

        var result = await client.RunAsync(job, Gemini(CapabilityKinds.Image, "gemini-2.5-flash-image"), null, CancellationToken.None);

        Assert.Equal(ToolErrorCodes.InvalidRequest, result.ErrorCode);
        Assert.Contains("10:7", result.Error);
        Assert.Contains("16:9", result.Error);
        Assert.Empty(vendor.Requests);
    }

    [Fact]
    public async Task AGeminiImageModelIsCalledOncePerPicture()
    {
        var job = Job(CapabilityKinds.Image, new { prompt = "p", n = 3 });

        var result = await client.RunAsync(job, Gemini(CapabilityKinds.Image, "gemini-2.5-flash-image"), null, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(3, result.Attachments!.Count);
        Assert.Equal(3, vendor.Requests.Count);
        Assert.All(vendor.Requests, r => Assert.Equal("/v1beta/models/gemini-2.5-flash-image:generateContent", r.Path));
    }

    [Fact]
    public async Task AnOpenAiEditSendsTheImageAndAnOpenAiMaskAsMultipart()
    {
        var png = FakeMediaVendor.Png(512, 512);
        var job = Job(
            CapabilityKinds.ImageEdit,
            new { op = "edit", prompt = "add a boat", has_mask = true, mask_convention = "openai" },
            new ToolAttachment(ImageEditRequest.ImagePart, "image/png", png),
            new ToolAttachment(ImageEditRequest.MaskPart, "image/png", png));

        var result = await client.RunAsync(job, OpenAi(CapabilityKinds.ImageEdit, "gpt-image-1.5"), null, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var sent = Assert.Single(vendor.Requests);
        Assert.Equal("/v1/images/edits", sent.Path);
        Assert.StartsWith("multipart/form-data", sent.ContentType);
        Assert.Contains("name=mask", sent.Text);
        Assert.Contains("add a boat", sent.Text);
    }

    [Fact]
    public async Task ALuminanceMaskToOpenAiAndAnyMaskToGeminiAreRefused()
    {
        var png = FakeMediaVendor.Png(64, 64);
        ToolJob Edit(string convention) => Job(
            CapabilityKinds.ImageEdit,
            new { op = "edit", prompt = "p", mask_convention = convention },
            new ToolAttachment(ImageEditRequest.ImagePart, "image/png", png),
            new ToolAttachment(ImageEditRequest.MaskPart, "image/png", png));

        var openAi = await client.RunAsync(Edit("luminance"), OpenAi(CapabilityKinds.ImageEdit, "gpt-image-1.5"), null, CancellationToken.None);
        var gemini = await client.RunAsync(Edit("openai"), Gemini(CapabilityKinds.ImageEdit, "gemini-2.5-flash-image"), null, CancellationToken.None);

        Assert.Equal(ToolErrorCodes.InvalidRequest, openAi.ErrorCode);
        Assert.Contains("transparent", openAi.Error);
        Assert.Equal(ToolErrorCodes.InvalidRequest, gemini.ErrorCode);
        Assert.Contains("no mask", gemini.Error);
        Assert.Empty(vendor.Requests);
    }

    // ---- speech --------------------------------------------------------------------------------

    [Fact]
    public async Task AnOpenAiVoiceIsTheVendorsBytesUnderTheFormatAskedFor()
    {
        var request = SpeechRequest.TryParse("""{"model":"local","input":"hello","response_format":"mp3"}""", out _)!;
        var job = new ToolJob(Guid.NewGuid(), CapabilityKinds.Speak, "local", request.ToToolPayload());

        var result = await client.RunAsync(job, OpenAi(CapabilityKinds.Speak, "gpt-4o-mini-tts"), null, CancellationToken.None);
        var outcome = AudioRenderer.Speech(result, request);

        Assert.Equal(200, outcome.Status);
        Assert.Equal("audio/mpeg", outcome.ContentType);
        Assert.Equal(FakeMediaVendor.Mp3, outcome.Bytes);
        Assert.Equal(5, outcome.Units);

        var sent = Assert.Single(vendor.Requests).Json;
        Assert.Equal("alloy", sent.GetProperty("voice").GetString());
        Assert.Equal("gpt-4o-mini-tts", sent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task AGeminiVoiceIsItsPcmWrappedInAWavHeader()
    {
        var request = SpeechRequest.TryParse("""{"model":"local","input":"hello","voice":"Puck"}""", out _)!;
        var job = new ToolJob(Guid.NewGuid(), CapabilityKinds.Speak, "local", request.ToToolPayload());

        var result = await client.RunAsync(job, Gemini(CapabilityKinds.Speak, "gemini-2.5-flash-preview-tts"), null, CancellationToken.None);
        var outcome = AudioRenderer.Speech(result, request);

        Assert.Equal(200, outcome.Status);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(outcome.Bytes!, 0, 4));
        Assert.Equal(44 + 4800, outcome.Bytes!.Length);

        // 24 kHz, the rate the vendor's mime type named.
        Assert.Equal(24000, BitConverter.ToInt32(outcome.Bytes, 24));

        var sent = Assert.Single(vendor.Requests).Json;
        Assert.Equal("Puck", sent.GetProperty("generationConfig").GetProperty("speechConfig")
            .GetProperty("voiceConfig").GetProperty("prebuiltVoiceConfig").GetProperty("voiceName").GetString());
    }

    [Fact]
    public async Task AGeminiVoiceInAFormatThatNeedsAnEncoderIsRefusedNamingWav()
    {
        var request = SpeechRequest.TryParse("""{"model":"local","input":"hello","response_format":"mp3"}""", out _)!;
        var job = new ToolJob(Guid.NewGuid(), CapabilityKinds.Speak, "local", request.ToToolPayload());

        var result = await client.RunAsync(job, Gemini(CapabilityKinds.Speak, "gemini-2.5-flash-preview-tts"), null, CancellationToken.None);

        Assert.Equal(400, AudioRenderer.Speech(result, request).Status);
        Assert.Equal(ToolErrorCodes.UnsupportedFormat, result.ErrorCode);
        Assert.Contains("response_format=wav", result.Error);
        Assert.Empty(vendor.Requests);
    }

    // ---- transcripts ---------------------------------------------------------------------------

    [Fact]
    public async Task WhisperIsAskedForVerboseJsonAndItsSegmentsBecomeSubtitles()
    {
        var request = TranscriptionRequest.TryCreate("local", "srt", null, null, null, hasFile: true, out _)!;
        var job = new ToolJob(Guid.NewGuid(), CapabilityKinds.Transcribe, "local", request.ToToolPayload(),
            [new ToolAttachment("audio", "application/octet-stream", FakeMediaVendor.Wav(1))]);

        var result = await client.RunAsync(job, OpenAi(CapabilityKinds.Transcribe, "whisper-1"), null, CancellationToken.None);
        var outcome = AudioRenderer.Transcription(result, request);

        Assert.Equal(200, outcome.Status);
        Assert.Contains("the quick brown fox", outcome.Text);
        Assert.Contains("-->", outcome.Text);
        Assert.Equal(2.5, outcome.Units);

        var sent = Assert.Single(vendor.Requests);
        Assert.Contains("verbose_json", sent.Text);

        // The file part is named for what it is, sniffed from its bytes — never the caller's filename.
        Assert.Contains("filename=audio.wav", sent.Text);
    }

    [Fact]
    public async Task AGeminiTranscriptMetersTheWavHeadersSecondsAndCannotBecomeSubtitles()
    {
        var json = TranscriptionRequest.TryCreate("local", "json", "bg", null, null, hasFile: true, out _)!;
        var vtt = TranscriptionRequest.TryCreate("local", "vtt", null, null, null, hasFile: true, out _)!;
        var audio = new ToolAttachment("audio", "application/octet-stream", FakeMediaVendor.Wav(3));

        var result = await client.RunAsync(
            new ToolJob(Guid.NewGuid(), CapabilityKinds.Transcribe, "local", json.ToToolPayload(), [audio]),
            Gemini(CapabilityKinds.Transcribe, "gemini-2.5-flash"),
            null,
            CancellationToken.None);

        var outcome = AudioRenderer.Transcription(result, json);
        Assert.Equal(200, outcome.Status);
        Assert.Equal(3, outcome.Units, precision: 6);
        Assert.Equal(502, AudioRenderer.Transcription(result, vtt).Status);

        var sent = Assert.Single(vendor.Requests).Json;
        var parts = sent.GetProperty("contents")[0].GetProperty("parts");
        Assert.Contains("'bg'", parts[0].GetProperty("text").GetString());
        Assert.Equal("audio/wav", parts[1].GetProperty("inlineData").GetProperty("mimeType").GetString());
    }

    // ---- the vendor's failures ------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    [InlineData(HttpStatusCode.TooManyRequests, 503)]
    [InlineData(HttpStatusCode.Unauthorized, 502)]
    [InlineData(HttpStatusCode.InternalServerError, 502)]
    public async Task AVendorStatusBecomesTheStatusTheEdgeAlreadyRenders(HttpStatusCode vendorStatus, int expected)
    {
        vendor.Fail = _ => (vendorStatus, """{"error":{"message":"the vendor's own sentence"}}""", vendorStatus == HttpStatusCode.TooManyRequests ? 7 : null);

        var result = await client.RunAsync(Job(CapabilityKinds.Image, new { prompt = "p" }), OpenAi(CapabilityKinds.Image, "gpt-image-1.5"), null, CancellationToken.None);
        var outcome = ImageRenderer.Render(result, Request(), 1);

        Assert.Equal(expected, outcome.Status);
        Assert.Contains("the vendor's own sentence", outcome.Error);
        Assert.DoesNotContain(Key, outcome.Error);

        if (expected == 503)
        {
            Assert.Equal(7, outcome.RetryAfterSeconds);
        }
    }

    [Fact]
    public async Task ACatalogueResolvesPerCapabilityAndTheValidatorRefusesASecondClaimant()
    {
        var providers = new Dictionary<string, CloudMediaProviderDefinition>
        {
            ["openai"] = new() { Type = "openai", ApiKey = "secret-key-xyz", Media = new() { ["image"] = new() { ["pic"] = "gpt-image-1.5" }, ["speak"] = new() { ["voice"] = "tts-1" } } },
            ["google"] = new() { Type = "gemini", ApiKey = "k", Media = new() { ["image"] = new() { ["pic"] = "imagen-4.0-generate-001" }, ["video"] = new() { ["clip"] = "veo-3" } } }
        };

        var errors = CloudMediaCatalog.Validate(providers, "CloudMedia:Providers", type => type);

        Assert.Contains(errors, e => e.Contains("'pic' for 'image' is claimed by both", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("cloud video is not in this release", StringComparison.Ordinal));

        var catalog = new CloudMediaCatalog(providers);
        Assert.True(catalog.TryResolve("speak", "VOICE", out var route));
        Assert.Equal("tts-1", route.UpstreamModel);
        Assert.Equal("https://api.openai.com/v1", route.BaseUrl);
        Assert.False(catalog.TryResolve("image", "voice", out _));
        Assert.DoesNotContain("secret-key-xyz", route.ToString());
    }
}
