namespace InferHub.Shared.Audio;

/// <summary>
/// The <c>response_format</c> values OpenAI's audio API defines, and what each one is worth here
/// (phase 42, D1).
/// </summary>
/// <remarks>
/// <para>
/// The client surface is OpenAI's, exactly — phase 21's argument again. Every SDK in every language
/// already speaks it, and there is no <em>Ollama</em> dialect for audio to translate from, so unlike
/// chat and embeddings there is exactly one client shape here.
/// </para>
/// <para>
/// <b>An unknown value is a 400 naming the ones that exist, never a silent substitution.</b> A
/// caller who asked for <c>srt</c> and got JSON has a file their subtitle player rejects for a
/// reason nothing in the response explains.
/// </para>
/// </remarks>
public static class TranscriptionFormats
{
    public const string Json = "json";
    public const string Text = "text";
    public const string Srt = "srt";
    public const string Vtt = "vtt";
    public const string VerboseJson = "verbose_json";

    /// <summary>In the order they appear in OpenAI's own documentation, so the refusal reads familiarly.</summary>
    public static readonly IReadOnlyList<string> All = [Json, Text, Srt, Vtt, VerboseJson];

    public static bool IsKnown(string? format) =>
        format is Json or Text or Srt or Vtt or VerboseJson;

    /// <summary>
    /// The two that are rendered from segments rather than from the transcript text. They are the
    /// reason a worker always returns the verbose shape and the edge decides what to do with it —
    /// see <see cref="TranscriptFormatter"/>.
    /// </summary>
    public static bool NeedsSegments(string format) => format is Srt or Vtt;

    public static string ContentTypeOf(string format) => format switch
    {
        Text => "text/plain; charset=utf-8",
        Srt => "text/plain; charset=utf-8",
        Vtt => "text/vtt; charset=utf-8",
        _ => "application/json"
    };

    public static string Refusal(string? got) =>
        $"response_format '{got}' is not supported. Use one of: {string.Join(", ", All)}.";
}

/// <summary>
/// The audio containers <c>/v1/audio/speech</c> knows about.
/// </summary>
/// <remarks>
/// <b>A format the worker cannot produce is refused, never substituted</b> (D1). Only <c>wav</c> and
/// <c>pcm</c> are native to the shipped TTS worker; the rest need an encoder, and a worker whose
/// environment has no <c>ffmpeg</c> answers with <see cref="Shared.Contracts.ToolErrorCodes.UnsupportedFormat"/>
/// so the edge can render a 400 that names what it <em>can</em> do. A caller who asked for mp3 and
/// got a wav has a corrupted file with a confident content type, and finds out in a media player
/// three days later.
/// </remarks>
public static class SpeechFormats
{
    public const string Wav = "wav";
    public const string Mp3 = "mp3";
    public const string Opus = "opus";
    public const string Flac = "flac";
    public const string Pcm = "pcm";

    public static readonly IReadOnlyList<string> All = [Wav, Mp3, Opus, Flac, Pcm];

    public static bool IsKnown(string? format) =>
        format is Wav or Mp3 or Opus or Flac or Pcm;

    /// <summary>
    /// The media type for a produced file. <c>pcm</c> is headerless 16-bit little-endian at the
    /// voice's own sample rate — <c>audio/pcm</c> is not a registered type, and neither is anything
    /// else that would describe it, so the caller has to know. OpenAI's API has the same hole.
    /// </summary>
    public static string ContentTypeOf(string format) => format switch
    {
        Mp3 => "audio/mpeg",
        Opus => "audio/ogg",
        Flac => "audio/flac",
        Pcm => "audio/pcm",
        _ => "audio/wav"
    };

    public static string FileNameFor(string format) => "speech." + format;

    public static string Refusal(string? got) =>
        $"response_format '{got}' is not supported. Use one of: {string.Join(", ", All)}.";
}
