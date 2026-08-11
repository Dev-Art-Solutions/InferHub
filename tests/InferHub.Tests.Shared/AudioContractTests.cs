using System.Globalization;
using InferHub.Shared.Audio;

namespace InferHub.Tests;

/// <summary>
/// The pure parts of phase 42: what a transcript formats to, and what a request validates to.
/// </summary>
/// <remarks>
/// They are worth their own suite because they are what the hub and a solo node <em>share</em>, so
/// every assertion here is one <c>AudioParityTests</c> does not have to make twice over a wire.
/// </remarks>
public class AudioContractTests
{
    private static readonly Transcript Sample = new(
        "Hello there. General Kenobi.",
        "en",
        4.5,
        [
            new TranscriptSegment(0, 0, 1.5, " Hello there."),
            new TranscriptSegment(1, 1.5, 4.5, " General Kenobi.")
        ]);

    [Fact]
    public void SrtUsesACommaBeforeTheMillisecondsAndAOneBasedCounter()
    {
        var srt = TranscriptFormatter.ToSrt(Sample.Segments);

        Assert.Equal(
            "1\n00:00:00,000 --> 00:00:01,500\nHello there.\n\n2\n00:00:01,500 --> 00:00:04,500\nGeneral Kenobi.\n\n",
            srt);
    }

    [Fact]
    public void VttUsesAPeriodAndTheWebvttHeaderAndNoCounter()
    {
        var vtt = TranscriptFormatter.ToVtt(Sample.Segments);

        Assert.Equal(
            "WEBVTT\n\n00:00:00.000 --> 00:00:01.500\nHello there.\n\n00:00:01.500 --> 00:00:04.500\nGeneral Kenobi.\n\n",
            vtt);
    }

    /// <summary>
    /// The locale trap <c>PrometheusMetricsTests</c> guards for the exposition format. A cue written
    /// on a Bulgarian or German host with the ambient culture reads <c>00:00:01,500</c> in a WebVTT
    /// file, which is silently invalid on exactly the machines nobody runs CI on.
    /// </summary>
    [Fact]
    public void TimestampsAreInvariantEvenUnderACommaDecimalCulture()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("bg-BG");

            Assert.Contains("00:00:01.500 -->", TranscriptFormatter.ToVtt(Sample.Segments));
            Assert.Contains("00:00:01,500 -->", TranscriptFormatter.ToSrt(Sample.Segments));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void TheJsonFormatCarriesOnlyTheTextAndVerboseCarriesTheSegments()
    {
        Assert.Equal("""{"text":"Hello there. General Kenobi."}""", Sample.ToCompactJson());

        var verbose = Sample.ToVerboseJson();
        Assert.Contains("\"task\":\"transcribe\"", verbose);
        Assert.Contains("\"duration\":4.5", verbose);
        Assert.Contains("\"segments\":[", verbose);
    }

    [Fact]
    public void AWorkerPayloadWithOnlyTextIsAValidTranscript()
    {
        var transcript = Transcript.TryParse("""{"text":"just the words"}""");

        Assert.NotNull(transcript);
        Assert.Equal("just the words", transcript!.Text);
        Assert.Null(transcript.Duration);
        Assert.Empty(transcript.Segments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void APayloadWithoutTextIsNotATranscript(string payload)
        => Assert.Null(Transcript.TryParse(payload));

    [Fact]
    public void AnUnknownResponseFormatIsRefusedWithTheOnesThatExist()
    {
        var request = TranscriptionRequest.TryCreate(
            "whisper-small", "mp3", null, null, null, hasFile: true, out var error);

        Assert.Null(request);
        Assert.Contains("json, text, srt, vtt, verbose_json", error);
    }

    [Fact]
    public void ResponseFormatDefaultsToJsonAndIsCaseInsensitive()
    {
        var request = TranscriptionRequest.TryCreate(
            "whisper-small", "  VERBOSE_JSON ", null, null, null, hasFile: true, out _);

        Assert.Equal(TranscriptionFormats.VerboseJson, request!.ResponseFormat);

        var defaulted = TranscriptionRequest.TryCreate(
            "whisper-small", null, null, null, null, hasFile: true, out _);

        Assert.Equal(TranscriptionFormats.Json, defaulted!.ResponseFormat);
    }

    [Fact]
    public void AMissingFileIsNamedRatherThanReportedAsAMissingModel()
    {
        Assert.Null(TranscriptionRequest.TryCreate(
            "whisper-small", null, null, null, null, hasFile: false, out var error));

        Assert.Equal("a 'file' part is required", error);
    }

    /// <summary>
    /// The worker is never told which format to produce — it always answers with the verbose shape
    /// and the edge formats. Otherwise every worker anybody writes, in whatever language, carries
    /// its own SRT timestamp arithmetic.
    /// </summary>
    [Fact]
    public void TheToolPayloadDoesNotCarryTheResponseFormat()
    {
        var payload = TranscriptionRequest
            .TryCreate("whisper-small", "srt", "en", null, "0.2", hasFile: true, out _)!
            .ToToolPayload();

        Assert.DoesNotContain("srt", payload);
        Assert.Contains("\"language\":\"en\"", payload);
        Assert.Contains("\"temperature\":0.2", payload);

        // Absent, not null: a worker reading `prompt` should see it missing rather than have to
        // distinguish "no prompt" from "the prompt is null".
        Assert.DoesNotContain("prompt", payload);
    }

    [Fact]
    public void TemperatureIsParsedInvariantlySoADecimalPointAlwaysMeansAPoint()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("bg-BG");

            var request = TranscriptionRequest.TryCreate(
                "whisper-small", null, null, null, "0.5", hasFile: true, out var error);

            Assert.NotNull(request);
            Assert.Equal(string.Empty, error);
            Assert.Equal(0.5, request!.Temperature);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void SpeechDefaultsToWavAndRefusesAnUnknownFormat()
    {
        var wav = SpeechRequest.TryParse("""{"model":"piper-en","input":"hi"}""", out _);
        Assert.Equal(SpeechFormats.Wav, wav!.ResponseFormat);

        var bad = SpeechRequest.TryParse("""{"model":"piper-en","input":"hi","response_format":"aiff"}""", out var error);
        Assert.Null(bad);
        Assert.Contains("wav, mp3, opus, flac, pcm", error);
    }

    [Fact]
    public void SpeechCountsCharactersAndRejectsAnEmptyInputButNotAWhitespaceOne()
    {
        var request = SpeechRequest.TryParse("""{"model":"piper-en","input":"twelve chars"}""", out _);
        Assert.Equal(12, request!.Characters);

        Assert.Null(SpeechRequest.TryParse("""{"model":"piper-en","input":""}""", out var error));
        Assert.Equal("input is required", error);

        // A space is a legitimate thing to synthesise — a pause — and refusing it would be us
        // deciding what a caller meant.
        Assert.NotNull(SpeechRequest.TryParse("""{"model":"piper-en","input":" "}""", out _));
    }

    [Fact]
    public void SpeedIsBoundedTheWayTheOpenAiApiBoundsIt()
    {
        Assert.Null(SpeechRequest.TryParse("""{"model":"m","input":"x","speed":9}""", out var error));
        Assert.Equal("speed must be between 0.25 and 4.0", error);

        Assert.NotNull(SpeechRequest.TryParse("""{"model":"m","input":"x","speed":1.5}""", out _));
    }

    [Fact]
    public void EveryKnownSpeechFormatHasItsOwnContentTypeAndNoneDefaultsSilently()
    {
        var byType = SpeechFormats.All.ToDictionary(f => f, SpeechFormats.ContentTypeOf);

        Assert.Equal("audio/wav", byType[SpeechFormats.Wav]);
        Assert.Equal("audio/mpeg", byType[SpeechFormats.Mp3]);
        Assert.Equal("audio/ogg", byType[SpeechFormats.Opus]);
        Assert.Equal("audio/flac", byType[SpeechFormats.Flac]);
        Assert.Equal("audio/pcm", byType[SpeechFormats.Pcm]);
        Assert.Equal(SpeechFormats.All.Count, byType.Values.Distinct().Count());
    }
}
