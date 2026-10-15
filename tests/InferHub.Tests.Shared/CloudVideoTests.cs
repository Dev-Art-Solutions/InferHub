using System.Text.Json;
using InferHub.Shared.CloudMedia;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;

namespace InferHub.Tests;

/// <summary>
/// Phase 105: a clip from Google through the cloud client, read back through the <em>same</em>
/// <see cref="VideoRenderer"/> a GPU clip goes through — Veo's operation polled to the end and its file
/// fetched with the key held to Google's host, Gemini Omni answering in one call, and every duration or
/// size the vendor does not render refused before anything is sent.
/// </summary>
public sealed class CloudVideoTests : IDisposable
{
    private const string Key = "AIza-test-cloud-video-0123456789";

    private readonly FakeMediaVendor vendor = new();
    private readonly HttpClient http;
    private readonly CloudMediaClient client;

    public CloudVideoTests()
    {
        // As both hosts build it: no automatic redirects (105 D5).
        http = new HttpClient(new NoRedirects(vendor.Borrowed()));
        client = new CloudMediaClient(http, pollInterval: TimeSpan.FromMilliseconds(5));
    }

    public void Dispose()
    {
        http.Dispose();
        vendor.Dispose();
    }

    private static CloudMediaRoute Google(string upstream) =>
        new("google", CloudMediaDialects.Gemini, "https://generativelanguage.googleapis.com/v1beta", Key, CapabilityKinds.Video, "veo", upstream, 30);

    private static ToolJob Job(VideoGenerationRequest request) =>
        new(Guid.NewGuid(), CapabilityKinds.Video, request.Model, request.ToToolPayload());

    private static VideoGenerationRequest Request(ImageSize? size = null, double? seconds = null, string? negative = null, int? steps = null) =>
        new("veo", "a lighthouse at dusk", negative, size, seconds, steps, null, null);

    [Fact]
    public async Task AVeoClipIsPolledFetchedAndRenderedLikeAGpuClip()
    {
        vendor.VeoPendingPolls = 2;
        var progress = new List<ToolChunk>();
        var request = Request(new ImageSize(1280, 720), seconds: 6, steps: 30);

        var outcome = await client.RunReportedAsync(Job(request), Google("veo-3.1-generate-preview"), null, CancellationToken.None, new Recording(progress));
        vendor.ClipSeconds = 6;
        var rendered = VideoRenderer.Render(outcome.Result, request);

        Assert.True(outcome.Result.Success, outcome.Result.Error);
        Assert.Equal(200, rendered.Status);
        Assert.Equal("video/mp4", rendered.Images[0].MediaType);
        Assert.Equal(new ImageSize(1280, 720), rendered.Images[0].Size);

        // The MP4's own header said eight (the fake renders 8 s regardless): the clip's real length wins over the label.
        Assert.Equal(8, rendered.SecondaryUnits);
        Assert.Equal(UsageUnitKinds.VideoSeconds, rendered.SecondaryUnitKind);
        Assert.Equal(1280 * 720 / 1e6 * 8, rendered.Units, precision: 4);
        Assert.Contains("steps is not used", Assert.Single(rendered.Summary.Warnings!));

        var submit = vendor.Requests.First();
        Assert.EndsWith("/models/veo-3.1-generate-preview:predictLongRunning", submit.Path);
        var parameters = submit.Json.GetProperty("parameters");
        Assert.Equal("16:9", parameters.GetProperty("aspectRatio").GetString());
        Assert.Equal("720p", parameters.GetProperty("resolution").GetString());
        Assert.Equal("6", parameters.GetProperty("durationSeconds").GetString());
        Assert.Equal(3, vendor.Requests.Count(r => r.Path.Contains("/operations/")));
        Assert.Equal(1, vendor.Requests.Count(r => r.Path.EndsWith(":predictLongRunning")));
        Assert.True(progress.Count >= 3);
        Assert.All(progress, chunk => Assert.Contains("\"totalSteps\":600", chunk.Payload));
    }

    [Fact]
    public async Task TheKeyGoesToGooglesHostAndNotToTheHostItRedirectsTo()
    {
        var outcome = await client.RunReportedAsync(Job(Request()), Google("veo-3.1-generate-preview"), null, CancellationToken.None);

        Assert.True(outcome.Result.Success, outcome.Result.Error);
        var download = Assert.Single(vendor.Requests, r => r.Path.EndsWith("/files/clip-1:download"));
        Assert.Equal(Key, download.Headers["x-goog-api-key"]);

        var storage = Assert.Single(vendor.Requests, r => r.Host == "storage.example");
        Assert.False(storage.Headers.ContainsKey("x-goog-api-key"));
        Assert.False(storage.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task AVideoUriOnAForeignHostIsRefusedAndNothingIsSentThere()
    {
        vendor.VeoVideoUri = "https://attacker.example/clip.mp4";

        var outcome = await client.RunReportedAsync(Job(Request()), Google("veo-3.1-generate-preview"), null, CancellationToken.None);

        Assert.False(outcome.Result.Success);
        Assert.Contains("attacker.example", outcome.Result.Error);
        Assert.DoesNotContain(vendor.Requests, r => r.Host == "attacker.example");
    }

    [Theory]
    [InlineData(1280, 720, 5d, "renders 4, 6, 8 seconds")]
    [InlineData(1920, 1080, 4d, "1080p only at 8 seconds")]
    [InlineData(832, 480, null, "renders 1280x720, 720x1280, 1920x1080, 1080x1920")]
    public async Task WhatVeoDoesNotRenderIsRefusedBeforeAnyCall(int width, int height, double? seconds, string says)
    {
        var request = Request(new ImageSize(width, height), seconds);

        var outcome = await client.RunReportedAsync(Job(request), Google("veo-3.1-generate-preview"), null, CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.Equal(ToolErrorCodes.InvalidRequest, outcome.Result.ErrorCode);
        Assert.Contains(says, outcome.Result.Error);
        Assert.Empty(vendor.Requests);
        Assert.Equal(400, VideoRenderer.Render(outcome.Result, request).Status);
    }

    [Fact]
    public async Task A1080pClipWithNoSecondsIsAskedForAtEight()
    {
        var outcome = await client.RunReportedAsync(Job(Request(new ImageSize(1080, 1920))), Google("veo-3.1-fast-generate-preview"), null, CancellationToken.None);

        Assert.True(outcome.Result.Success, outcome.Result.Error);
        var parameters = vendor.Requests.First().Json.GetProperty("parameters");
        Assert.Equal("9:16", parameters.GetProperty("aspectRatio").GetString());
        Assert.Equal("1080p", parameters.GetProperty("resolution").GetString());
        Assert.Equal("8", parameters.GetProperty("durationSeconds").GetString());
    }

    [Fact]
    public async Task ANegativePromptIsRefusedRatherThanDropped()
    {
        var outcome = await client.RunReportedAsync(Job(Request(negative: "people")), Google("veo-3.1-generate-preview"), null, CancellationToken.None);

        Assert.False(outcome.Sent);
        Assert.Contains("negative prompt", outcome.Result.Error);
    }

    [Fact]
    public async Task AnOmniClipArrivesInOneCallAndItsLengthIsReadFromTheMp4()
    {
        vendor.ClipSeconds = 5.5;
        var request = Request(new ImageSize(720, 1280), seconds: 4);

        var outcome = await client.RunReportedAsync(Job(request), Google("gemini-omni-1.1-flash"), null, CancellationToken.None);
        var rendered = VideoRenderer.Render(outcome.Result, request);

        Assert.Equal(200, rendered.Status);
        Assert.Equal(5.5, rendered.SecondaryUnits);
        Assert.Contains("chooses the clip's length", Assert.Single(rendered.Summary.Warnings!));

        var sent = Assert.Single(vendor.Requests);
        Assert.EndsWith("/v1beta/interactions", sent.Path);
        Assert.Equal("gemini-omni-1.1-flash", sent.Json.GetProperty("model").GetString());
        var format = sent.Json.GetProperty("response_format");
        Assert.Equal("video", format.GetProperty("type").GetString());
        Assert.Equal("9:16", format.GetProperty("aspect_ratio").GetString());
        Assert.Equal("720p", format.GetProperty("resolution").GetString());
        Assert.Equal(Key, sent.Headers["x-goog-api-key"]);
    }

    [Fact]
    public async Task AVendorFailureWhilePollingFailsTheJobAndNothingIsResubmitted()
    {
        vendor.Fail = seen => seen.Path.Contains("/operations/") ? (System.Net.HttpStatusCode.InternalServerError, """{"error":{"message":"backend"}}""", null) : null;

        var outcome = await client.RunReportedAsync(Job(Request()), Google("veo-3.1-generate-preview"), null, CancellationToken.None);

        Assert.False(outcome.Result.Success);
        Assert.Contains("not submitted again", outcome.Result.Error);
        Assert.Equal(1, vendor.Requests.Count(r => r.Path.EndsWith(":predictLongRunning")));
    }

    [Fact]
    public async Task ACancelStopsPollingAndSaysSo()
    {
        vendor.VeoPendingPolls = 1000;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var outcome = await client.RunReportedAsync(Job(Request()), Google("veo-3.1-generate-preview"), null, cancel.Token);

        Assert.Equal(ToolErrorCodes.Cancelled, outcome.Result.ErrorCode);
    }

    [Fact]
    public void VideoOnAnOpenAiProviderFailsStartupNamingTheShutdown()
    {
        var provider = new CloudMediaProviderDefinition { Type = "openai", ApiKey = "k" };
        provider.Media["video"] = new Dictionary<string, string> { ["sora"] = "sora-2" };

        var error = Assert.Single(CloudMediaCatalog.Validate([new("openai", provider)], "CloudMedia:Providers", type => type));

        Assert.Contains("Sora 2", error);
        Assert.Contains("gemini", error);
    }

    [Fact]
    public void VideoOnAGeminiProviderIsAValidMap()
    {
        var provider = new CloudMediaProviderDefinition { Type = "gemini", ApiKey = "k" };
        provider.Media["video"] = new Dictionary<string, string> { ["veo"] = "veo-3.1-generate-preview" };

        Assert.Empty(CloudMediaCatalog.Validate([new("google", provider)], "CloudMedia:Providers", type => type));
        Assert.Contains(new CloudMediaCatalog(new Dictionary<string, CloudMediaProviderDefinition> { ["google"] = provider }).Capabilities(),
            capability => capability.Kind == CapabilityKinds.Video && capability.Models.Contains("veo"));
    }

    [Theory]
    [InlineData("1920x1080", true)]
    [InlineData("1080x1920", true)]
    [InlineData("1920x1072", true)]
    [InlineData("1900x1080", false)]
    public void TheVendorsOwn1080pIsAdmittedOffTheGridAndNothingElseIs(string size, bool admitted)
        => Assert.Equal(admitted, VideoSizes.TryParse(size, out _, out _));

    private sealed class Recording(List<ToolChunk> chunks) : IProgress<ToolChunk>
    {
        public void Report(ToolChunk value)
        {
            lock (chunks)
            {
                chunks.Add(value);
            }
        }
    }

    /// <summary>The pooled clients are built with redirects off; a plain handler chain has no redirect stage, which is the same thing.</summary>
    private sealed class NoRedirects(HttpMessageHandler inner) : DelegatingHandler(inner);
}
