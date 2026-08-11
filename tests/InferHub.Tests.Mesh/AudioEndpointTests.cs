using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// <c>/v1/audio/transcriptions</c> and <c>/v1/audio/speech</c>, end to end: an HTTP client → a real
/// coordinator → a real SignalR wire → a real node → a real child process → and back (phase 42).
/// </summary>
public class AudioEndpointTests
{
    [Fact]
    public async Task ATranscriptionRoundTripsThroughTheMeshAndDefaultsToTheJsonShape()
    {
        await using var mesh = await AudioMesh.StartAsync();

        var response = await mesh.Client.PostAsync("/v1/audio/transcriptions", Upload());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("node", response.Headers.GetValues("X-InferHub-Served-By").Single());

        var body = await response.Content.ReadAsStringAsync();

        // The `json` format is one field, exactly as OpenAI's default is.
        using var document = JsonDocument.Parse(body);
        Assert.Equal(AudioFixture.KnownPhrase, document.RootElement.GetProperty("text").GetString());
        Assert.Single(document.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task EveryResponseFormatIsProducedInItsOwnShapeAndContentType()
    {
        await using var mesh = await AudioMesh.StartAsync();

        var text = await mesh.Client.PostAsync("/v1/audio/transcriptions", Upload(format: "text"));
        Assert.Equal("text/plain", text.Content.Headers.ContentType?.MediaType);
        Assert.Equal(AudioFixture.KnownPhrase, await text.Content.ReadAsStringAsync());

        var verbose = await mesh.Client.PostAsync("/v1/audio/transcriptions", Upload(format: "verbose_json"));
        using var document = JsonDocument.Parse(await verbose.Content.ReadAsStringAsync());
        Assert.Equal("transcribe", document.RootElement.GetProperty("task").GetString());
        Assert.Equal(3.25, document.RootElement.GetProperty("duration").GetDouble());
        Assert.Equal(2, document.RootElement.GetProperty("segments").GetArrayLength());

        var srt = await mesh.Client.PostAsync("/v1/audio/transcriptions", Upload(format: "srt"));
        Assert.Equal("text/plain", srt.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("1\n00:00:00,000 --> 00:00:01,500\n", await srt.Content.ReadAsStringAsync());

        var vtt = await mesh.Client.PostAsync("/v1/audio/transcriptions", Upload(format: "vtt"));
        Assert.Equal("text/vtt", vtt.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("WEBVTT\n\n00:00:00.000 --> 00:00:01.500\n", await vtt.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnUnknownResponseFormatIsA400NamingTheAlternativesAndNothingIsSubstituted()
    {
        await using var mesh = await AudioMesh.StartAsync();

        var response = await mesh.Client.PostAsync("/v1/audio/transcriptions", Upload(format: "mp3"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("json, text, srt, vtt, verbose_json", await Message(response));
    }

    /// <summary>
    /// A worker that returns text but no segments cannot produce a subtitle file, and the refusal is
    /// stated. An empty WebVTT is not an error anywhere in the toolchain that consumes it: it opens,
    /// it plays, and it shows nothing — so the caller concludes the audio was silent.
    /// </summary>
    [Fact]
    public async Task ASubtitleFormatWithoutSegmentsIsRefusedRatherThanEmitted()
    {
        await using var mesh = await AudioMesh.StartAsync(workerArguments: ["--audio-no-segments"]);

        var refused = await mesh.Client.PostAsync("/v1/audio/transcriptions", Upload(format: "srt"));
        Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
        Assert.Contains("no segments", await Message(refused));

        // …and the formats that do not need segments still work.
        var ok = await mesh.Client.PostAsync("/v1/audio/transcriptions", Upload(format: "text"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task SpeechReturnsARealWavWithTheFormatsOwnContentType()
    {
        await using var mesh = await AudioMesh.StartAsync();

        var response = await mesh.Client.PostAsync("/v1/audio/speech", Speech());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync();

        // The header, not the length. A byte count passes just as happily on 8000 zeros.
        Assert.True(bytes.Length > 44);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
    }

    /// <summary>
    /// The worker states which <em>kind</em> of failure it was and the edge renders it — the node
    /// never has to be interpreted from its message (phase-29 D6). A box with no <c>ffmpeg</c>
    /// behaves exactly like this.
    /// </summary>
    [Fact]
    public async Task AFormatTheWorkerCannotProduceIsA400NamingWhatItCan()
    {
        await using var mesh = await AudioMesh.StartAsync();

        var response = await mesh.Client.PostAsync("/v1/audio/speech", Speech(format: "mp3"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("It can produce: wav, pcm", await Message(response));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("unsupported_format", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AWorkerFailureWithNoCodeIsStillA502()
    {
        await using var mesh = await AudioMesh.StartAsync(workerArguments: ["--audio-fail", "boom"]);

        var response = await mesh.Client.PostAsync("/v1/audio/transcriptions", Upload());
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task AnUploadOverTheCapIsA413AtTheEdgeNamingTheLimit()
    {
        await using var mesh = await AudioMesh.StartAsync(maxAttachmentBytes: 64);

        var response = await mesh.Client.PostAsync("/v1/audio/transcriptions", Upload(bytes: new byte[256]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("over the 64-byte limit", await Message(response));
    }

    [Fact]
    public async Task ACapabilityNobodyProvidesIsA503WithRetryAfterAndAModelNobodyHoldsIsA404()
    {
        await using var mesh = await AudioMesh.StartAsync();

        // The model exists on the node — for `speak`, not for `transcribe`.
        var wrongCapability = await mesh.Client.PostAsync(
            "/v1/audio/transcriptions",
            Upload(model: AudioFixture.SpeakModel));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, wrongCapability.StatusCode);
        Assert.Equal("30", wrongCapability.Headers.GetValues("Retry-After").Single());
        Assert.Equal(
            $"no node currently provides 'transcribe' for model '{AudioFixture.SpeakModel}'",
            await Message(wrongCapability));

        var unknown = await mesh.Client.PostAsync("/v1/audio/transcriptions", Upload(model: "no-such-model"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("model 'no-such-model' not found", await Message(unknown));
    }

    [Fact]
    public async Task ABodyThatIsNotMultipartIsA400ThatSaysWhatTheEndpointTakes()
    {
        await using var mesh = await AudioMesh.StartAsync();

        var response = await mesh.Client.PostAsync(
            "/v1/audio/transcriptions",
            JsonContent.Create(new { model = AudioFixture.TranscribeModel }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("multipart/form-data", await Message(response));
    }

    /// <summary>
    /// Phase-21 D2, and it is the reason this test exists rather than a comment: a client-facing
    /// route added under a prefix the guard does not cover ships an unauthenticated inference API,
    /// and this one accepts a 25 MB upload and spends GPU time on it.
    /// </summary>
    [Theory]
    [InlineData("/v1/audio/transcriptions")]
    [InlineData("/v1/audio/speech")]
    public async Task TheAudioRoutesRejectAMissingKey(string path)
    {
        var called = false;
        var options = new StaticOptionsMonitor(new ApiKeyOptions { ApiKeys = ["secret"] });

        var middleware = new BearerApiKeyMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            options,
            new ClientRegistry(options),
            NullLogger<BearerApiKeyMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8");

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    /// <summary>
    /// Found by pulling the published <c>:tools</c> image and looking at it, which is the only way
    /// this class of thing is ever found. A tools-only box — no Ollama, a running Whisper — reported
    /// <c>capabilities: []</c> on its own status page while happily serving transcriptions, because
    /// solo status asked the backend's models and never the tool runtime. That page is the one an
    /// operator checks to find out why nothing is being routed to a node.
    /// </summary>
    [Fact]
    public async Task SoloStatusReportsTheToolRuntimesCapabilities()
    {
        var (solo, cleanup) = await AudioFixture.SoloAsync();

        try
        {
            var response = await solo.Client.GetAsync("/api/status");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            var capabilities = document.RootElement
                .GetProperty("capabilities")
                .EnumerateArray()
                .Select(kind => kind.GetString())
                .ToArray();

            Assert.Contains("transcribe", capabilities);
            Assert.Contains("speak", capabilities);
        }
        finally
        {
            await solo.DisposeAsync();
            cleanup.Dispose();
        }
    }

    // ---- metering (D7) --------------------------------------------------------------------------

    [Fact]
    public async Task ATranscriptionIsMeteredInAudioSecondsAndSpeechInCharacters()
    {
        await using var mesh = await AudioMesh.StartAsync();

        await mesh.Client.PostAsync("/v1/audio/transcriptions", Upload());
        await mesh.Client.PostAsync("/v1/audio/speech", Speech(input: "0123456789"));

        var rows = await mesh.Ledger.QueryAsync(new UsageQuery());

        var transcription = rows.Single(r => r.Model == AudioFixture.TranscribeModel);
        Assert.Equal(3.25, transcription.AudioSeconds);
        Assert.Equal(0, transcription.Characters);
        Assert.Equal(0, transcription.TotalTokens);

        var speech = rows.Single(r => r.Model == AudioFixture.SpeakModel);
        Assert.Equal(10, speech.Characters);
        Assert.Equal(0, speech.AudioSeconds);
    }

    [Fact]
    public async Task AFailedJobIsNotBilled()
    {
        await using var mesh = await AudioMesh.StartAsync(workerArguments: ["--audio-fail", "boom"]);

        await mesh.Client.PostAsync("/v1/audio/transcriptions", Upload());

        Assert.Empty(await mesh.Ledger.QueryAsync(new UsageQuery()));
    }

    /// <summary>
    /// A client whose only budget is tokens could otherwise transcribe a library for free: audio
    /// consumes no tokens, so a token budget cannot bound it.
    /// </summary>
    [Fact]
    public async Task AnAudioBudgetIsSeparateFromTheTokenBudgetAndRejectsWith402()
    {
        await using var mesh = await AudioMesh.StartAsync(
            limits: new ClientLimits { AudioSecondsPerDay = 3 });

        var first = await mesh.Client.PostAsync("/v1/audio/transcriptions", Upload());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // 3.25 seconds landed against a budget of 3, so the next one is refused.
        var second = await mesh.Client.PostAsync("/v1/audio/transcriptions", Upload());
        Assert.Equal(HttpStatusCode.PaymentRequired, second.StatusCode);
        Assert.Contains("daily audio-second budget of 3", await Message(second));
        Assert.True(second.Headers.Contains("Retry-After"));

        using var document = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal("insufficient_quota", document.RootElement.GetProperty("error").GetProperty("code").GetString());

        // …and speech, measured in a different unit, is untouched by it.
        Assert.Equal(HttpStatusCode.OK, (await mesh.Client.PostAsync("/v1/audio/speech", Speech())).StatusCode);
    }

    [Fact]
    public async Task AModelOutsideTheClientsAllowlistIsThe404ThatLooksLikeAMissingModel()
    {
        await using var mesh = await AudioMesh.StartAsync(
            limits: new ClientLimits { AllowedModels = ["something-else"] });

        var response = await mesh.Client.PostAsync("/v1/audio/transcriptions", Upload());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal($"model '{AudioFixture.TranscribeModel}' not found", await Message(response));
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static MultipartFormDataContent Upload(
        string? model = null,
        string? format = null,
        byte[]? bytes = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(model ?? AudioFixture.TranscribeModel), "model" },
            { new ByteArrayContent(bytes ?? "not really audio"u8.ToArray()), "file", "meeting.m4a" }
        };

        if (format is not null)
        {
            form.Add(new StringContent(format), "response_format");
        }

        return form;
    }

    private static JsonContent Speech(string? format = null, string? input = null)
        => JsonContent.Create(new
        {
            model = AudioFixture.SpeakModel,
            input = input ?? "InferHub can talk now.",
            response_format = format ?? "wav"
        });

    private static async Task<string?> Message(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetProperty("message").GetString();
    }

    private sealed class StaticOptionsMonitor(ApiKeyOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<ApiKeyOptions>
    {
        public ApiKeyOptions CurrentValue { get; } = value;

        public ApiKeyOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ApiKeyOptions, string?> listener) => null;
    }
}
