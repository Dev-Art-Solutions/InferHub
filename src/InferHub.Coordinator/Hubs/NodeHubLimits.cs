namespace InferHub.Coordinator.Hubs;

/// <summary>
/// How large a message the node hub will accept, derived from the attachment cap the edge already
/// enforces (phase 42).
/// </summary>
/// <remarks>
/// <para>
/// <b>SignalR's default <c>MaximumReceiveMessageSize</c> is 32 KB, and exceeding it does not fail
/// the message — it kills the whole connection.</b> Found by running a real mesh: a 6-second
/// synthesised wav is ~300 KB, so <em>every</em> real <c>/v1/audio/speech</c> request through a
/// coordinator returned a 500, dropped the node's SignalR connection, and made it re-register.
/// Phase 41 shipped attachments and verified them across the wire with a 16-byte file, which is
/// under the cap by four orders of magnitude — the test proved the plumbing and said nothing about
/// the limit.
/// </para>
/// <para>
/// The size is <b>derived</b> from <c>Tools:MaxAttachmentBytes</c> rather than being its own key,
/// because two numbers that have to agree are two numbers that will not: an operator who raises the
/// attachment cap to take longer recordings must not then discover a second limit nobody mentioned.
/// One key, and the wire follows it.
/// </para>
/// <para>
/// The headroom is real arithmetic, not a guess. The default JSON hub protocol encodes a
/// <c>byte[]</c> as base64 — four characters per three bytes — and the frame around it carries a
/// job id, a media type and a file name.
/// </para>
/// </remarks>
public static class NodeHubLimits
{
    /// <summary>SignalR's own default. Never go below it.</summary>
    public const long SignalRDefault = 32 * 1024;

    /// <summary>Envelope, property names and the file name, generously.</summary>
    private const long Envelope = 64 * 1024;

    public static long ReceiveSizeFor(long maxAttachmentBytes)
    {
        if (maxAttachmentBytes <= 0)
        {
            return SignalRDefault;
        }

        // Base64 is 4 characters per 3 bytes, rounded up to a 4-character group.
        var encoded = ((maxAttachmentBytes + 2) / 3) * 4;

        return Math.Max(SignalRDefault, encoded + Envelope);
    }
}
