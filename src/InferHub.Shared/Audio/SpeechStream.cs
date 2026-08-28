using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using InferHub.Shared.OpenAi;

namespace InferHub.Shared.Audio;

/// <summary>
/// The two values OpenAI's <c>stream_format</c> takes on <c>POST /v1/audio/speech</c> (phase 70,
/// D1). Absent is not a third value — it is the buffered response phase 42 shipped, unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Read from the published OpenAPI spec on the day rather than remembered: <c>sse</c> frames the
/// audio as events, <c>audio</c> is the raw bytes on a chunked body.
/// </para>
/// <para>
/// <b>OpenAI's own Python SDK models neither event.</b> <c>src/openai/types/audio/</c> carries
/// <c>transcription_text_delta_event.py</c> and has no speech equivalent, so <c>sse</c> is a
/// documented-but-unmodelled surface and <c>audio</c> —
/// <c>with_streaming_response.create(...).iter_bytes()</c> — is what an SDK actually consumes. Both
/// are served; the docs lead with <c>audio</c> for that reason.
/// </para>
/// </remarks>
public static class SpeechStreamFormats
{
    public const string Sse = "sse";
    public const string Audio = "audio";

    public static readonly IReadOnlyList<string> All = [Sse, Audio];

    public static bool IsKnown(string? format) => format is Sse or Audio;

    public static string Refusal(string? got) =>
        $"stream_format '{got}' is not supported. Use one of: {string.Join(", ", All)}.";
}

/// <summary>
/// One piece of audio as a worker produced it, with the format it measured off its own first
/// samples rather than off a config file (D4).
/// </summary>
public sealed record SpeechStreamChunk(byte[] Audio, int SampleRate, int Channels, int SampleWidth)
{
    /// <summary>Whether two chunks describe the same stream. A worker that changes rate mid-answer is a failure, not a resample.</summary>
    public bool SameFormatAs(SpeechStreamChunk other) =>
        SampleRate == other.SampleRate && Channels == other.Channels && SampleWidth == other.SampleWidth;
}

/// <summary>
/// The wire format of a streamed synthesis, decided once for both hosts (phase 70).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AudioRenderer"/>'s argument, one route over: this says what the bytes and the frames
/// are, and the ten lines that push them at a response stay per host (37 D6, rule 2). Everything a
/// caller can observe about a streamed answer is decided here, which is what
/// <c>AudioStreamParityTests</c> checks by driving both hosts.
/// </para>
/// </remarks>
public static class SpeechStream
{
    public const string SseContentType = "text/event-stream";

    /// <summary>The event names. <c>delta</c> and <c>done</c> are OpenAI's; <c>error</c> is ours (D5).</summary>
    public const string DeltaEvent = "speech.audio.delta";

    public const string DoneEvent = "speech.audio.done";

    public const string ErrorEvent = "speech.audio.error";

    /// <summary>
    /// The measured sample rate, on the response. It is the only way a <c>pcm</c> caller can know
    /// it — the format is headerless by definition — and it is why the status is held until the
    /// first chunk arrives (D4).
    /// </summary>
    public const string SampleRateHeader = "X-InferHub-Audio-Sample-Rate";

    /// <summary>
    /// What was metered, in the unit it was metered in (D6). The token counts in
    /// <c>speech.audio.done</c> are zero and true; this is the number that reconciles with a bill.
    /// </summary>
    public const string CharactersHeader = "X-InferHub-Speech-Characters";

    /// <summary>
    /// Both length fields of a streaming RIFF header. The length is not knowable when the header
    /// goes out and this is the sentinel a piped wav has always used; a player accepts it, and
    /// <c>ffprobe</c> reports a nonsense duration, which the docs say rather than leaving it to be
    /// found.
    /// </summary>
    private const uint UnknownLength = 0xFFFFFFFFu;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The 44 bytes that make a stream of PCM samples a file, built from the first chunk's own
    /// measured format (D4).
    /// </summary>
    /// <remarks>
    /// <b>Nothing reads the rate from the voice's config.</b> <c>piper_worker</c> refuses to for a
    /// reason worth keeping: a hand-set rate that disagrees with the model produces audio at the
    /// wrong pitch and passes every byte-count assertion anybody writes. The model's own first
    /// chunk is the authority here too.
    /// </remarks>
    public static byte[] StreamingWavHeader(int sampleRate, int channels, int sampleWidth)
    {
        var bitsPerSample = sampleWidth * 8;
        var blockAlign = channels * sampleWidth;
        var byteRate = sampleRate * blockAlign;

        var header = new byte[44];
        var span = header.AsSpan();

        "RIFF"u8.CopyTo(span[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], UnknownLength);
        "WAVE"u8.CopyTo(span[8..12]);

        "fmt "u8.CopyTo(span[12..16]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..20], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..22], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..24], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..28], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..32], (uint)byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..34], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..36], (ushort)bitsPerSample);

        "data"u8.CopyTo(span[36..40]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..44], UnknownLength);

        return header;
    }

    /// <summary>
    /// Reads a worker's <c>chunk</c> payload. Returns null for anything that is not one — a
    /// <c>progress</c> frame, a worker's own bookkeeping — and sets <paramref name="error"/> only
    /// when the frame claimed to be audio and was not usable.
    /// </summary>
    /// <remarks>
    /// A frame that is simply not audio is <b>skipped, not failed</b>: phase 47 added
    /// <c>progress</c> to this same stream and a speech worker is entitled to send one.
    /// </remarks>
    public static SpeechStreamChunk? TryParseChunk(string? payloadJson, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            error = "the speech worker sent a chunk that is not JSON";
            return null;
        }

        if (root.ValueKind is not JsonValueKind.Object
            || !root.TryGetProperty("audio", out var audio)
            || audio.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        byte[] bytes;

        try
        {
            bytes = Convert.FromBase64String(audio.GetString() ?? string.Empty);
        }
        catch (FormatException)
        {
            error = "the speech worker sent a chunk whose audio is not base64";
            return null;
        }

        var sampleRate = Int(root, "sampleRate");
        var channels = Int(root, "channels") ?? 1;
        var sampleWidth = Int(root, "sampleWidth") ?? 2;

        if (sampleRate is null or <= 0)
        {
            // Without it there is no header to write and no rate to report, and guessing 22 050
            // would be the hand-set rate D4 refuses.
            error = "the speech worker sent audio without a sample rate";
            return null;
        }

        if (channels <= 0 || sampleWidth <= 0)
        {
            error = "the speech worker sent audio with an unusable channel count or sample width";
            return null;
        }

        return new SpeechStreamChunk(bytes, sampleRate.Value, channels, sampleWidth);
    }

    /// <summary>
    /// The message on a terminal chunk that failed, or null when the stream ended cleanly. The node
    /// writes <c>{error, done}</c> for every failure it can still describe (see
    /// <c>ToolExecutor.Terminal</c>), and this is the edge reading that field rather than the
    /// message text — phase-29 D6.
    /// </summary>
    public static string? TryReadFailure(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);

            return document.RootElement.ValueKind is JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind is JsonValueKind.String
                    ? error.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One SSE frame: the event name on its own line, then the JSON.</summary>
    public static string Frame(string eventName, string json) => $"event: {eventName}\ndata: {json}\n\n";

    public static string Delta(byte[] audio) =>
        Frame(DeltaEvent, JsonSerializer.Serialize(
            new { type = DeltaEvent, audio = Convert.ToBase64String(audio) },
            Json));

    /// <summary>
    /// The terminal event. <b>The three token counts are zero and that is true</b> (D6): Piper is a
    /// phoneme model and nothing here was ever tokenized. The schema requires the object, so it is
    /// emitted rather than omitted — an SDK that models it would throw on its absence — and the
    /// number that reconciles with a bill rides on <see cref="CharactersHeader"/>.
    /// </summary>
    public static string Done() =>
        Frame(DoneEvent, JsonSerializer.Serialize(
            new
            {
                type = DoneEvent,
                usage = new { input_tokens = 0, output_tokens = 0, total_tokens = 0 }
            },
            Json));

    /// <summary>
    /// The ending for a stream that died after the caller already holds a 200 (D5). Not in OpenAI's
    /// schema, which defines only the two above — it is an extension and the docs label it one,
    /// because the alternative is closing silently and leaving a client to decide whether the
    /// silence was the end of the sentence.
    /// </summary>
    public static string Error(string message, string type, string? code = null) =>
        Frame(ErrorEvent, JsonSerializer.Serialize(
            new { type = ErrorEvent, error = OpenAiErrorEnvelope.Create(message, type, code, null).Error },
            Json));

    /// <summary>UTF-8, because both hosts write bytes at a response body and neither buffers a string.</summary>
    public static byte[] Bytes(string frame) => Encoding.UTF8.GetBytes(frame);

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}

/// <summary>
/// The state a streamed synthesis carries between chunks, and every byte that goes on the wire
/// because of one (phase 70).
/// </summary>
/// <remarks>
/// <para>
/// It exists so that the two hosts share more than a constant. Phase-37 D6 draws the line at "the
/// ten lines that write a response are per host", and a streamed answer has more than ten lines of
/// *decision* in it — when the header goes out, what the first frame carries, what a format change
/// mid-stream means. Those live here, pure, and what stays per host is genuinely a content type,
/// three headers and a flush.
/// </para>
/// <para>
/// Not thread-safe and not meant to be: one request, one encoder, one reader loop.
/// </para>
/// </remarks>
public sealed class SpeechStreamEncoder(SpeechRequest request)
{
    private SpeechStreamChunk? first;

    /// <summary>Whether any audio has gone out. Past this point there is no status left to send (D5).</summary>
    public bool Started => first is not null;

    /// <summary>The rate the worker measured, once one chunk has arrived. Null before that.</summary>
    public int? SampleRate => first?.SampleRate;

    /// <summary>
    /// What the response is. For <c>audio</c> it is the container the caller asked for; for
    /// <c>sse</c> it is an event stream whatever the container is, because the container is inside
    /// the events.
    /// </summary>
    public string ContentType => request.IsSse
        ? SpeechStream.SseContentType
        : SpeechFormats.ContentTypeOf(request.ResponseFormat);

    /// <summary>
    /// The bytes this chunk puts on the wire, or null with <paramref name="error"/> set.
    /// </summary>
    /// <remarks>
    /// <b>A rate that changes mid-answer is a failure, not something to resample.</b> Two rates
    /// concatenated play as one file at the wrong speed for half of it, and there is no dependency
    /// in this project that could resample them — nor should there be one for a case that means the
    /// worker is confused.
    /// </remarks>
    public byte[]? Encode(SpeechStreamChunk chunk, out string? error)
    {
        error = null;

        var opening = first;

        if (opening is null)
        {
            first = chunk;

            // D4: the header is the first thing on the wire and it is built from the audio's own
            // measured format, because that is the only authority for it.
            byte[] payload = request.ResponseFormat == SpeechFormats.Wav
                ? [.. SpeechStream.StreamingWavHeader(chunk.SampleRate, chunk.Channels, chunk.SampleWidth), .. chunk.Audio]
                : chunk.Audio;

            return Wrap(payload);
        }

        if (!chunk.SameFormatAs(opening))
        {
            error =
                $"the speech worker changed format mid-stream (was {opening.SampleRate} Hz / {opening.Channels} ch / " +
                $"{opening.SampleWidth * 8}-bit, now {chunk.SampleRate} Hz / {chunk.Channels} ch / {chunk.SampleWidth * 8}-bit)";

            return null;
        }

        return Wrap(chunk.Audio);
    }

    /// <summary>The clean ending, or null when the shape has no ending to write (raw bytes just stop).</summary>
    public byte[]? Complete() => request.IsSse ? SpeechStream.Bytes(SpeechStream.Done()) : null;

    /// <summary>
    /// The ending for a stream that died past the head (D5). Null for <c>audio</c>, where there is
    /// nowhere in a byte stream to put a sentence and closing is all that is left.
    /// </summary>
    public byte[]? Fail(string message, string type, string? code) =>
        request.IsSse ? SpeechStream.Bytes(SpeechStream.Error(message, type, code)) : null;

    private byte[] Wrap(byte[] audio) =>
        request.IsSse ? SpeechStream.Bytes(SpeechStream.Delta(audio)) : audio;
}
