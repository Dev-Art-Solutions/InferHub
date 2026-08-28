using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;

namespace InferHub.Tests;

/// <summary>
/// A streamed synthesis end to end (phase 70): an HTTP client → a real coordinator → a real SignalR
/// wire → a real node → a real child process, and the bytes back as they are made.
/// </summary>
/// <remarks>
/// <para>
/// The echo worker's streaming mode sends the same quarter-second 440 Hz tone it has always written
/// to a file, split into <c>chunk</c> frames — so a stream and a buffered call can be compared
/// sample for sample, which is the assertion that catches an encoder losing a frame.
/// </para>
/// <para>
/// <b>The one to keep is <see cref="AnOversizedChunkFailsTheJobAndTheConnectionSurvivesIt"/></b>
/// (D2). Phase 42's own bug was not a failed request: it was a dropped SignalR connection and a
/// node that re-registered, and the only way to see the difference is to ask the same mesh for
/// something else afterwards.
/// </para>
/// </remarks>
public class AudioStreamTests
{
    [Fact]
    public async Task AStreamedWavIsOneHeaderAndThenSamples()
    {
        await using var mesh = await AudioMesh.StartAsync();

        var response = await mesh.Client.PostAsync("/v1/audio/speech", Speech("wav", "audio"));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("node", response.Headers.GetValues("X-InferHub-Served-By").Single());
        Assert.Equal("16000", response.Headers.GetValues("X-InferHub-Audio-Sample-Rate").Single());

        Assert.Equal("RIFF", Encoding.ASCII.GetString(body, 0, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(body, 36, 4));

        // D4: the two lengths are the streaming sentinel, because neither was knowable when the
        // header went out.
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4, 4)));
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(40, 4)));
        Assert.Equal(16000u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(24, 4)));

        // Exactly one header, however many chunks arrived.
        Assert.Equal(44 + 8000, body.Length);
    }

    [Fact]
    public async Task AStreamedPcmHasNoHeaderAndTheRateIsTheOnlyPlaceItCanBe()
    {
        await using var mesh = await AudioMesh.StartAsync();

        var response = await mesh.Client.PostAsync("/v1/audio/speech", Speech("pcm", "audio"));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/pcm", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("16000", response.Headers.GetValues("X-InferHub-Audio-Sample-Rate").Single());
        Assert.Equal(8000, body.Length);
        Assert.NotEqual("RIFF", Encoding.ASCII.GetString(body, 0, 4));
    }

    /// <summary>
    /// The assertion that catches a lost frame. Streaming changes *when* the samples arrive and
    /// nothing else, so the two bodies are the same bytes.
    /// </summary>
    [Fact]
    public async Task TheStreamedSamplesAreTheSameSamplesTheBufferedCallReturns()
    {
        await using var mesh = await AudioMesh.StartAsync();

        var buffered = await (await mesh.Client.PostAsync("/v1/audio/speech", Speech("pcm", null)))
            .Content.ReadAsByteArrayAsync();
        var streamed = await (await mesh.Client.PostAsync("/v1/audio/speech", Speech("pcm", "audio")))
            .Content.ReadAsByteArrayAsync();

        Assert.Equal(buffered, streamed);
    }

    [Fact]
    public async Task TheSseShapeIsDeltasAndThenADoneWithThreeZeroes()
    {
        await using var mesh = await AudioMesh.StartAsync();

        var response = await mesh.Client.PostAsync("/v1/audio/speech", Speech("pcm", "sse"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("20", response.Headers.GetValues("X-InferHub-Speech-Characters").Single());

        var events = Events(body);

        // The echo worker sends 4 KiB at a time and produces 8 000 bytes, so there is more than one
        // — which is what makes this a stream rather than a one-frame envelope.
        Assert.True(events.Count(e => e.Name == "speech.audio.delta") > 1, body);
        Assert.Equal("speech.audio.done", events[^1].Name);

        var reassembled = events
            .Where(e => e.Name == "speech.audio.delta")
            .SelectMany(e => Convert.FromBase64String(e.Json.GetProperty("audio").GetString()!))
            .ToArray();

        Assert.Equal(8000, reassembled.Length);

        var usage = events[^1].Json.GetProperty("usage");
        Assert.Equal(0, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(0, usage.GetProperty("output_tokens").GetInt32());
        Assert.Equal(0, usage.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task AFormatThatCannotBeCutInHalfIsRefusedAndNoNodeIsEverAsked()
    {
        await using var mesh = await AudioMesh.StartAsync();

        var response = await mesh.Client.PostAsync("/v1/audio/speech", Speech("mp3", "audio"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("wav, pcm", await Message(response));
        Assert.Empty(await mesh.Ledger.QueryAsync(new UsageQuery()));
    }

    [Fact]
    public async Task AnUnknownStreamFormatIsA400NamingTheTwoThatExist()
    {
        await using var mesh = await AudioMesh.StartAsync();

        var response = await mesh.Client.PostAsync("/v1/audio/speech", Speech("wav", "websocket"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("sse, audio", await Message(response));
    }

    /// <summary>
    /// D2, and the second half of the assertion is the point: phase 42's bug dropped the node's
    /// connection rather than the message, and a suite that only checked the failed request would
    /// have passed on it.
    /// </summary>
    [Fact]
    public async Task AnOversizedChunkFailsTheJobAndTheConnectionSurvivesIt()
    {
        // Three seconds at 16 kHz is 96 000 bytes; asked for in one 40 000-byte piece that is
        // ~53 KB of base64, comfortably over ToolProtocol.MaxChunkPayloadBytes.
        await using var mesh = await AudioMesh.StartAsync(
            workerArguments: ["--speech-seconds", "3", "--speech-chunk-bytes", "40000"]);

        var response = await mesh.Client.PostAsync("/v1/audio/speech", Speech("pcm", "audio"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var message = await Message(response);
        Assert.Contains("streaming chunk", message);
        Assert.Contains(ToolProtocol.MaxChunkPayloadBytes.ToString(), message);

        // The node is still there and still answering, which is the whole difference between a
        // failed job and a killed connection.
        var after = await mesh.Client.PostAsync("/v1/audio/speech", Speech("wav", null));
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    /// <summary>
    /// A rate that changes after the header is out. There is no status left to send, so the stream
    /// simply stops carrying the samples it cannot describe — and what already went out is intact.
    /// </summary>
    [Fact]
    public async Task ARateThatChangesAfterTheHeaderEndsTheStreamInsteadOfConcatenatingTwo()
    {
        await using var mesh = await AudioMesh.StartAsync(workerArguments: ["--speech-rate-shift"]);

        var response = await mesh.Client.PostAsync("/v1/audio/speech", Speech("pcm", "audio"));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4096, body.Length);
    }

    [Fact]
    public async Task AnSseStreamThatDiesEndsWithAnErrorEventRatherThanSilence()
    {
        await using var mesh = await AudioMesh.StartAsync(workerArguments: ["--speech-rate-shift"]);

        var events = Events(await (await mesh.Client.PostAsync("/v1/audio/speech", Speech("pcm", "sse")))
            .Content.ReadAsStringAsync());

        Assert.Equal("speech.audio.delta", events[0].Name);
        Assert.Equal("speech.audio.error", events[^1].Name);
        Assert.Contains("16000", events[^1].Json.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task AStreamWithNoAudioInItIsStillA502BecauseNothingHasBeenWritten()
    {
        await using var mesh = await AudioMesh.StartAsync(workerArguments: ["--speech-no-audio"]);

        var response = await mesh.Client.PostAsync("/v1/audio/speech", Speech("wav", "audio"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("no audio", await Message(response));
    }

    /// <summary>D6/D8: the unit is unchanged and the terminal frame is what bills.</summary>
    [Fact]
    public async Task AStreamIsMeteredInTheSameCharactersABufferedCallIs()
    {
        await using var mesh = await AudioMesh.StartAsync();

        await mesh.Client.PostAsync("/v1/audio/speech", Speech("pcm", "audio", "0123456789"));

        var row = Assert.Single(await mesh.Ledger.QueryAsync(new UsageQuery()));

        Assert.Equal(AudioFixture.SpeakModel, row.Model);
        Assert.Equal(10, row.Characters);
        Assert.Equal(0, row.TotalTokens);
        Assert.Equal(0, row.AudioSeconds);
    }

    [Fact]
    public async Task AFailedStreamIsNotBilled()
    {
        await using var mesh = await AudioMesh.StartAsync(workerArguments: ["--audio-fail", "boom"]);

        var response = await mesh.Client.PostAsync("/v1/audio/speech", Speech("wav", "audio"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await mesh.Ledger.QueryAsync(new UsageQuery()));
    }

    /// <summary>
    /// Phase-37 D6 for the routes this phase adds: a client that moves from a hub to a standalone
    /// box gets the same bytes, and the only header that differs is the one that exists to tell
    /// them apart.
    /// </summary>
    [Theory]
    [InlineData("wav", "audio")]
    [InlineData("pcm", "audio")]
    [InlineData("pcm", "sse")]
    public async Task AStreamIsIdenticalOnBothHosts(string format, string streamFormat)
    {
        await using var mesh = await AudioMesh.StartAsync();
        var (solo, cleanup) = await AudioFixture.SoloAsync();

        try
        {
            var hub = await mesh.Client.PostAsync("/v1/audio/speech", Speech(format, streamFormat));
            var node = await solo.Client.PostAsync("/v1/audio/speech", Speech(format, streamFormat));

            Assert.Equal(hub.StatusCode, node.StatusCode);
            Assert.Equal(hub.Content.Headers.ContentType?.MediaType, node.Content.Headers.ContentType?.MediaType);
            Assert.Equal(
                hub.Headers.GetValues("X-InferHub-Audio-Sample-Rate").Single(),
                node.Headers.GetValues("X-InferHub-Audio-Sample-Rate").Single());
            Assert.Equal(
                await hub.Content.ReadAsByteArrayAsync(),
                await node.Content.ReadAsByteArrayAsync());

            Assert.Equal("node", hub.Headers.GetValues("X-InferHub-Served-By").Single());
            Assert.Equal("node-solo", node.Headers.GetValues("X-InferHub-Served-By").Single());
        }
        finally
        {
            await solo.DisposeAsync();
            cleanup.Dispose();
        }
    }

    private static JsonContent Speech(string format, string? streamFormat, string? input = null)
        => JsonContent.Create(new
        {
            model = AudioFixture.SpeakModel,
            input = input ?? "InferHub can stream.",
            response_format = format,
            stream_format = streamFormat
        });

    private static (string Name, JsonElement Json)[] Events(string body) => body
        .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
        .Select(frame => frame.Split('\n'))
        .Select(lines => (
            Name: lines[0]["event: ".Length..],
            Json: JsonDocument.Parse(lines[1]["data: ".Length..]).RootElement.Clone()))
        .ToArray();

    private static async Task<string> Message(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetProperty("message").GetString() ?? string.Empty;
    }
}
