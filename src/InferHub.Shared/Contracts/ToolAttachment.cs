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
/// <b>This is the buffered path, and since phase 53 it is one of two.</b> It is what a request at or
/// under <c>Tools:MaxAttachmentBytes</c> still takes, byte for byte; anything larger travels as
/// <see cref="AttachmentChunk"/> frames the node pulls while the client is still uploading, and
/// never becomes a <c>byte[]</c> on the hub at all. See phase-53 D1/D2.
/// </para>
/// <para>
/// The claim above needed one correction when phase 53 measured it: <c>ReadFormAsync</c> buffers a
/// file section over <c>FormOptions.MemoryBufferThreshold</c> (64 KB) into an
/// <c>ASPNETCORE_*.tmp</c> file under the process's temp directory, so from v3.9 to v3.20 every
/// real audio and image upload <em>did</em> touch the hub's disk — not through code written here,
/// but through the framework underneath it. The streamed path does not.
/// </para>
/// </remarks>
public sealed record ToolAttachment(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("bytes")] byte[] Bytes);

/// <summary>
/// One frame of a streamed attachment (phase 53, D1): the node pulls these off the hub while the
/// client's upload is still in flight, and appends them to the scratch file the worker will read by
/// path.
/// </summary>
/// <remarks>
/// <para>
/// The sequence is <c>start</c> → zero or more <c>data</c> → <c>end</c>, repeated per attachment
/// and terminated by the enumeration ending. <b>Deliberately not a list of attachment references on
/// the job:</b> how many parts a multipart body carries, and what they are called, is not knowable
/// until the body has been read — and reading it to find out is exactly the buffering this phase
/// removes. The frames describe the body in the order it actually arrives.
/// </para>
/// <para>
/// <see cref="Name"/> and <see cref="MediaType"/> ride on the <c>start</c> frame only; the node
/// names the file it opens from the index and the part name, never from the caller's filename
/// (phase-42 D5 — a filename is metadata about somebody's day).
/// </para>
/// </remarks>
public sealed record AttachmentChunk(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("mediaType")] string? MediaType = null,
    [property: JsonPropertyName("bytes")] byte[]? Bytes = null)
{
    public static AttachmentChunk Start(int index, string name, string mediaType) =>
        new(AttachmentChunkKinds.Start, index, name, mediaType);

    public static AttachmentChunk Data(int index, byte[] bytes) =>
        new(AttachmentChunkKinds.Data, index, Bytes: bytes);

    public static AttachmentChunk End(int index) => new(AttachmentChunkKinds.End, index);
}

/// <summary>The three frame kinds, as <c>const string</c> for the same reason every other wire
/// constant here is one: an enum would have to survive a mixed-version fleet.</summary>
public static class AttachmentChunkKinds
{
    public const string Start = "start";
    public const string Data = "data";
    public const string End = "end";
}

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
    /// How much of a streamed attachment travels in one frame (phase 53). 64 KB is small enough
    /// that the hub's window is a rounding error against a request and large enough that a 300 MB
    /// upload is not five thousand round trips.
    /// </summary>
    public const int DefaultStreamChunkBytes = 64 * 1024;

    /// <summary>The key the buffered path's refusal names.</summary>
    public const string MaxAttachmentBytesKey = "Tools:MaxAttachmentBytes";

    /// <summary>The key the streamed path's refusal names (phase 53). Zero means the path is off.</summary>
    public const string MaxStreamedBytesKey = "Tools:MaxStreamedBytes";

    /// <summary>
    /// The refusal sentence, naming the limit. Over the cap is a <c>413</c> at the edge; a caller
    /// who is told only "too large" has to guess by bisection. The key is a parameter since phase
    /// 53 because there are two ceilings and being told the wrong one to raise is worse than being
    /// told none.
    /// </summary>
    public static string TooLarge(string name, long bytes, long maxBytes, string key = MaxAttachmentBytesKey) =>
        $"attachment '{name}' is {bytes} bytes, over the {maxBytes}-byte limit ({key})";
}
