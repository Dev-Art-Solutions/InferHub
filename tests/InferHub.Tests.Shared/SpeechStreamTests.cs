using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using InferHub.Shared.Audio;

namespace InferHub.Tests;

/// <summary>
/// The pure half of phase 70: what a streamed synthesis puts on the wire, and what it refuses.
/// </summary>
/// <remarks>
/// Everything here runs without a host, a socket or a worker, because everything a caller can
/// observe about a streamed answer is decided in <c>InferHub.Shared</c> — which is what lets the
/// hub and a solo node agree by construction rather than by coincidence (37 D6).
/// </remarks>
public class SpeechStreamTests
{
    [Fact]
    public void ARequestWithoutStreamFormatIsNotAStreamingRequest()
    {
        var request = SpeechRequest.TryParse("""{"model":"amy","input":"hello"}""", out var error);

        Assert.NotNull(request);
        Assert.Equal(string.Empty, error);
        Assert.False(request!.IsStreaming);
        Assert.Null(request.StreamFormat);

        // The payload a worker receives still carries the key, as null, which System.Text.Json
        // drops — so a v3.36 worker sees byte-for-byte what it saw before.
        Assert.DoesNotContain("stream_format", request.ToToolPayload());
    }

    [Theory]
    [InlineData("sse")]
    [InlineData("audio")]
    [InlineData("SSE")]
    public void BothOfOpenAisValuesAreAccepted(string value)
    {
        var request = SpeechRequest.TryParse(
            $$"""{"model":"amy","input":"hello","response_format":"wav","stream_format":"{{value}}"}""",
            out _);

        Assert.NotNull(request);
        Assert.True(request!.IsStreaming);
        Assert.Equal(value.ToLowerInvariant(), request.StreamFormat);
        Assert.Contains("stream_format", request.ToToolPayload());
    }

    [Fact]
    public void AnUnknownStreamFormatIsRefusedNamingTheOnesThatExist()
    {
        var request = SpeechRequest.TryParse(
            """{"model":"amy","input":"hello","stream_format":"websocket"}""",
            out var error);

        Assert.Null(request);
        Assert.Contains("websocket", error);
        Assert.Contains("sse", error);
        Assert.Contains("audio", error);
    }

    /// <summary>D3, and the refusal has to name the alternative or it is a dead end.</summary>
    [Theory]
    [InlineData("mp3")]
    [InlineData("opus")]
    [InlineData("flac")]
    public void AFormatThatCannotBeCutInHalfIsRefusedBeforeAnythingIsDispatched(string format)
    {
        var request = SpeechRequest.TryParse(
            $$"""{"model":"amy","input":"hello","response_format":"{{format}}","stream_format":"audio"}""",
            out var error);

        Assert.Null(request);
        Assert.Contains(format, error);
        Assert.Contains("wav", error);
        Assert.Contains("pcm", error);

        // And the same format without stream_format is still perfectly legal — the refusal is
        // about streaming it, not about producing it.
        Assert.NotNull(SpeechRequest.TryParse(
            $$"""{"model":"amy","input":"hello","response_format":"{{format}}"}""",
            out _));
    }

    [Fact]
    public void TheWavHeaderIsARealHeaderWithTheStreamingLengthInIt()
    {
        var header = SpeechStream.StreamingWavHeader(22050, 1, 2);

        Assert.Equal(44, header.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(header, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(header, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(header, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(header, 36, 4));

        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20, 2)));   // PCM
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22, 2)));   // mono
        Assert.Equal(22050u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24, 4)));
        Assert.Equal(44100u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32, 2)));   // block align
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34, 2)));  // bits

        // D4: both lengths are the sentinel, because neither is knowable yet.
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4)));
        Assert.Equal(0xFFFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40, 4)));
    }

    [Fact]
    public void AStereoHeaderCarriesTheRateItWasGivenRatherThanAConstant()
    {
        var header = SpeechStream.StreamingWavHeader(48000, 2, 2);

        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22, 2)));
        Assert.Equal(48000u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24, 4)));
        Assert.Equal(192000u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28, 4)));
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32, 2)));
    }

    [Fact]
    public void AChunkIsReadAsTheWorkerWroteIt()
    {
        var chunk = SpeechStream.TryParseChunk(Payload([1, 2, 3, 4], 22050), out var error);

        Assert.Null(error);
        Assert.NotNull(chunk);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, chunk!.Audio);
        Assert.Equal(22050, chunk.SampleRate);
        Assert.Equal(1, chunk.Channels);
        Assert.Equal(2, chunk.SampleWidth);
    }

    /// <summary>Phase 47 put <c>progress</c> on this same stream and a speech worker may send one.</summary>
    [Fact]
    public void AFrameThatIsNotAudioIsSkippedRatherThanFailed()
    {
        var chunk = SpeechStream.TryParseChunk("""{"type":"progress","step":1,"totalSteps":4}""", out var error);

        Assert.Null(chunk);
        Assert.Null(error);
    }

    [Fact]
    public void AudioWithNoRateOnItIsAFailureRatherThanAGuess()
    {
        var chunk = SpeechStream.TryParseChunk("""{"audio":"AAECAw=="}""", out var error);

        Assert.Null(chunk);
        Assert.Contains("sample rate", error);
    }

    [Fact]
    public void AudioThatIsNotBase64IsAFailureThatSaysSo()
    {
        var chunk = SpeechStream.TryParseChunk("""{"audio":"not base64 at all","sampleRate":22050}""", out var error);

        Assert.Null(chunk);
        Assert.Contains("base64", error);
    }

    [Fact]
    public void TheNodesTerminalFailureIsReadFromTheFieldAndNotFromTheText()
    {
        Assert.Equal(
            "tool 'piper' ended without answering",
            SpeechStream.TryReadFailure("""{"error":"tool 'piper' ended without answering","done":true}"""));

        Assert.Null(SpeechStream.TryReadFailure("""{"format":"pcm","characters":5,"stream":true}"""));
        Assert.Null(SpeechStream.TryReadFailure("not json at all"));
    }

    [Fact]
    public void TheFirstAudioFrameCarriesTheHeaderAndTheRestDoNot()
    {
        var encoder = new SpeechStreamEncoder(Request("wav", "audio"));

        Assert.False(encoder.Started);
        Assert.Null(encoder.SampleRate);

        var first = encoder.Encode(new SpeechStreamChunk([9, 9], 22050, 1, 2), out var error);

        Assert.Null(error);
        Assert.True(encoder.Started);
        Assert.Equal(22050, encoder.SampleRate);
        Assert.Equal(46, first!.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(first, 0, 4));

        var second = encoder.Encode(new SpeechStreamChunk([8, 8], 22050, 1, 2), out error);

        Assert.Null(error);
        Assert.Equal(new byte[] { 8, 8 }, second);
    }

    [Fact]
    public void PcmGetsNoHeaderAtAllAndTheRateIsOnTheResponseInstead()
    {
        var encoder = new SpeechStreamEncoder(Request("pcm", "audio"));
        var first = encoder.Encode(new SpeechStreamChunk([7, 7, 7], 22050, 1, 2), out var error);

        Assert.Null(error);
        Assert.Equal(new byte[] { 7, 7, 7 }, first);
        Assert.Equal(22050, encoder.SampleRate);
        Assert.Equal("audio/pcm", encoder.ContentType);
    }

    /// <summary>
    /// Two rates concatenated play as one file at the wrong speed for half of it, and there is no
    /// dependency here that could resample them — nor should there be one for a case that means the
    /// worker is confused.
    /// </summary>
    [Fact]
    public void ARateThatChangesMidStreamIsRefusedWithBothNumbersInIt()
    {
        var encoder = new SpeechStreamEncoder(Request("wav", "audio"));

        encoder.Encode(new SpeechStreamChunk([1], 22050, 1, 2), out _);
        var second = encoder.Encode(new SpeechStreamChunk([1], 24000, 1, 2), out var error);

        Assert.Null(second);
        Assert.Contains("22050", error);
        Assert.Contains("24000", error);
    }

    [Fact]
    public void TheSseShapeIsEventThenDataAndTheContentTypeSaysSo()
    {
        var encoder = new SpeechStreamEncoder(Request("pcm", "sse"));

        Assert.Equal("text/event-stream", encoder.ContentType);

        var frame = Encoding.UTF8.GetString(encoder.Encode(new SpeechStreamChunk([1, 2, 3], 22050, 1, 2), out _)!);

        Assert.StartsWith("event: speech.audio.delta\ndata: ", frame);
        Assert.EndsWith("\n\n", frame);

        using var document = JsonDocument.Parse(Data(frame));
        Assert.Equal("speech.audio.delta", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), document.RootElement.GetProperty("audio").GetString());
    }

    /// <summary>
    /// D6. The schema requires all three, so all three are written; they are zero because Piper is
    /// a phoneme model and nothing was ever tokenized. The number that reconciles with a bill is
    /// the character count, and it is on a header.
    /// </summary>
    [Fact]
    public void TheDoneEventCarriesAUsageObjectOfHonestZeroes()
    {
        var encoder = new SpeechStreamEncoder(Request("pcm", "sse"));
        var frame = Encoding.UTF8.GetString(encoder.Complete()!);

        Assert.StartsWith("event: speech.audio.done\n", frame);

        using var document = JsonDocument.Parse(Data(frame));
        var usage = document.RootElement.GetProperty("usage");

        Assert.Equal("speech.audio.done", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(0, usage.GetProperty("output_tokens").GetInt32());
        Assert.Equal(0, usage.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public void RawBytesHaveNoEndingToWriteAndSayThatByReturningNothing()
    {
        Assert.Null(new SpeechStreamEncoder(Request("wav", "audio")).Complete());
        Assert.Null(new SpeechStreamEncoder(Request("wav", "audio")).Fail("boom", "api_error", null));
    }

    [Fact]
    public void ASseStreamThatDiesEndsWithAnErrorEventCarryingTheOrdinaryEnvelope()
    {
        var frame = Encoding.UTF8.GetString(
            new SpeechStreamEncoder(Request("pcm", "sse")).Fail("the node disconnected", "api_error", "node_lost")!);

        Assert.StartsWith("event: speech.audio.error\n", frame);

        using var document = JsonDocument.Parse(Data(frame));
        var error = document.RootElement.GetProperty("error");

        Assert.Equal("the node disconnected", error.GetProperty("message").GetString());
        Assert.Equal("api_error", error.GetProperty("type").GetString());
        Assert.Equal("node_lost", error.GetProperty("code").GetString());
    }

    private static SpeechRequest Request(string format, string streamFormat) =>
        new("amy", "hello", null, format, null, streamFormat);

    private static string Payload(byte[] audio, int rate) => JsonSerializer.Serialize(new
    {
        audio = Convert.ToBase64String(audio),
        sampleRate = rate,
        channels = 1,
        sampleWidth = 2
    });

    private static string Data(string frame) => frame.Split("data: ")[1].TrimEnd('\n');
}
