using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Shared.Audio;

/// <summary>
/// What a transcription worker returned, in the one shape the edge formats every
/// <c>response_format</c> out of (phase 42, D1).
/// </summary>
/// <remarks>
/// <para>
/// A worker always answers with the verbose shape — text, segments, duration — and the edge decides
/// what the caller sees. The alternative, telling the worker which format to produce, would put
/// <c>srt</c> escaping and timestamp arithmetic inside every worker anybody ever writes, in whatever
/// language they wrote it in, and the day two workers disagreed about a comma the bug would look
/// like a model problem.
/// </para>
/// <para>
/// Segments come free from Whisper, which is why <c>verbose_json</c> carries them. Speaker labels
/// it does not produce are not invented here — see the phase's non-goals.
/// </para>
/// </remarks>
public sealed record Transcript(
    string Text,
    string? Language,
    double? Duration,
    IReadOnlyList<TranscriptSegment> Segments)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads the worker's payload. Everything is optional except the text: a worker that answers
    /// with only <c>{"text": "..."}</c> is a valid transcription worker, it simply cannot serve
    /// <c>srt</c> or <c>vtt</c> — and that refusal is stated where it happens rather than papered
    /// over with an empty subtitle file.
    /// </summary>
    public static Transcript? TryParse(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("text", out var text) || text.ValueKind is not JsonValueKind.String)
            {
                return null;
            }

            var language = root.TryGetProperty("language", out var l) && l.ValueKind is JsonValueKind.String
                ? l.GetString()
                : null;

            var duration = root.TryGetProperty("duration", out var d) && d.ValueKind is JsonValueKind.Number
                ? d.GetDouble()
                : (double?)null;

            var segments = new List<TranscriptSegment>();

            if (root.TryGetProperty("segments", out var array) && array.ValueKind is JsonValueKind.Array)
            {
                var index = 0;

                foreach (var element in array.EnumerateArray())
                {
                    if (element.ValueKind is not JsonValueKind.Object)
                    {
                        continue;
                    }

                    segments.Add(new TranscriptSegment(
                        element.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number
                            ? id.GetInt32()
                            : index,
                        Number(element, "start"),
                        Number(element, "end"),
                        element.TryGetProperty("text", out var st) && st.ValueKind is JsonValueKind.String
                            ? st.GetString() ?? string.Empty
                            : string.Empty));

                    index++;
                }
            }

            return new Transcript(text.GetString() ?? string.Empty, language, duration, segments);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number
            ? value.GetDouble()
            : 0d;

    /// <summary>The <c>json</c> body: the one field OpenAI's default format carries.</summary>
    public string ToCompactJson() => JsonSerializer.Serialize(new { text = Text }, Json);

    /// <summary>The <c>verbose_json</c> body.</summary>
    public string ToVerboseJson() => JsonSerializer.Serialize(
        new VerboseTranscription("transcribe", Language, Duration, Text, Segments),
        Json);
}

public sealed record TranscriptSegment(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("text")] string Text);

/// <summary>The <c>verbose_json</c> envelope, in OpenAI's field order and names.</summary>
public sealed record VerboseTranscription(
    [property: JsonPropertyName("task")] string Task,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("duration")] double? Duration,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("segments")] IReadOnlyList<TranscriptSegment> Segments);

/// <summary>
/// Turns a <see cref="Transcript"/> into whatever <c>response_format</c> asked for.
/// </summary>
/// <remarks>
/// <para>
/// It is string formatting and nothing else — no dependency, phase-28 D1's reasoning about the
/// Prometheus exposition format applied to two subtitle formats that are, between them, about
/// forty lines of code.
/// </para>
/// <para>
/// It lives in <c>InferHub.Shared</c> because the hub and a solo node must produce the same bytes
/// for the same transcript: phase-37 D6's line is that the frame <em>bodies</em> are shared and only
/// the ten lines that write them to a response are per host. A subtitle that differs by a comma
/// between two InferHub deployments is a bug somebody debugs in a video player.
/// </para>
/// </remarks>
public static class TranscriptFormatter
{
    public static string Format(Transcript transcript, string responseFormat) => responseFormat switch
    {
        TranscriptionFormats.Text => transcript.Text,
        TranscriptionFormats.Srt => ToSrt(transcript.Segments),
        TranscriptionFormats.Vtt => ToVtt(transcript.Segments),
        TranscriptionFormats.VerboseJson => transcript.ToVerboseJson(),
        _ => transcript.ToCompactJson()
    };

    /// <summary>
    /// SubRip: a 1-based counter, <c>HH:MM:SS,mmm --&gt; HH:MM:SS,mmm</c>, the line, a blank line.
    /// The separator is a <b>comma</b> here and a <b>period</b> in WebVTT, which is the single most
    /// common way a hand-written subtitle writer produces a file one player accepts and another does
    /// not.
    /// </summary>
    public static string ToSrt(IReadOnlyList<TranscriptSegment> segments)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            builder.Append(i + 1).Append('\n');
            builder.Append(Timestamp(segment.Start, ',')).Append(" --> ").Append(Timestamp(segment.End, ',')).Append('\n');
            builder.Append(segment.Text.Trim()).Append('\n').Append('\n');
        }

        return builder.ToString();
    }

    public static string ToVtt(IReadOnlyList<TranscriptSegment> segments)
    {
        var builder = new StringBuilder("WEBVTT\n\n");

        foreach (var segment in segments)
        {
            builder.Append(Timestamp(segment.Start, '.')).Append(" --> ").Append(Timestamp(segment.End, '.')).Append('\n');
            builder.Append(segment.Text.Trim()).Append('\n').Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// <c>HH:MM:SS</c> + a separator + three digits of milliseconds.
    /// </summary>
    /// <remarks>
    /// <b><see cref="CultureInfo.InvariantCulture"/> is load-bearing.</b> The same trap
    /// <c>PrometheusFormatter</c> has: on a Bulgarian or German host a default-culture format writes
    /// a decimal comma, and a VTT cue that reads <c>00:00:01.234</c> everywhere else reads
    /// <c>00:00:01,234</c> there — a file that is silently invalid on exactly the machines nobody
    /// runs CI on.
    /// </remarks>
    private static string Timestamp(double seconds, char separator)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        var span = TimeSpan.FromSeconds(seconds);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}{separator}{span.Milliseconds:D3}");
    }
}
