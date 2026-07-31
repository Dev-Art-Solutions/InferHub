using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// Bytes travelling with a <see cref="ToolJob"/> or a <see cref="ToolResult"/> — an audio file in,
/// audio bytes out (phase 40, D4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded, in-memory, and never on the hub's disk.</b> The coordinator holds the bytes for the
/// duration of the dispatch and nothing else: no temp file, no cache, and no log line containing
/// them. Design rule 7 is at its most literal here — an audio attachment <em>is</em> content, in
/// the sense that it is a recording of somebody's voice.
/// </para>
/// <para>
/// A streaming upload path that never materialises the whole body would be better and is deferred
/// rather than forgotten: it needs chunked client-to-server streaming through the dispatcher, which
/// is a phase and not a footnote. Until then <c>Tools:MaxAttachmentBytes</c> is the ceiling and it
/// is enforced at the edge, before anything is buffered onward.
/// </para>
/// </remarks>
public sealed record ToolAttachment(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("bytes")] byte[] Bytes);

/// <summary>
/// The one place the attachment cap is decided, so the hub's edge and the node's own edge cannot
/// disagree about what fits.
/// </summary>
public static class ToolAttachmentLimits
{
    /// <summary>
    /// 25 MB — what the OpenAI audio API accepts, so the limit is one somebody has already met and
    /// every client library already handles.
    /// </summary>
    public const long DefaultMaxBytes = 25L * 1024 * 1024;

    /// <summary>
    /// The refusal sentence, naming the limit. Over the cap is a <c>413</c> at the edge; a caller
    /// who is told only "too large" has to guess by bisection.
    /// </summary>
    public static string TooLarge(string name, long bytes, long maxBytes) =>
        $"attachment '{name}' is {bytes} bytes, over the {maxBytes}-byte limit (Tools:MaxAttachmentBytes)";
}
