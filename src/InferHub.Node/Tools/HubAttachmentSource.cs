using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace InferHub.Node.Tools;

/// <summary>
/// The mesh half of <see cref="IStreamedAttachmentSource"/> (phase 53, D1): the node pulls the
/// bytes off the hub over the connection it already opened.
/// </summary>
/// <remarks>
/// <para>
/// <b>The node pulls; the hub does not push.</b> Phase-26 D1 is untouched — this is a
/// server-to-client stream established by <em>this</em> invocation, exactly as
/// <c>RequestNodeProfile</c> is, so no coordinator ever dials a GPU box. It also means the node
/// asks for the bytes when it is ready for them, rather than the hub buffering for a node that is
/// still queued behind another job.
/// </para>
/// <para>
/// A hub that has forgotten the job yields nothing, and <see cref="ToolExecutor"/> turns that into
/// a failed job rather than a running one with an empty file. Every other failure — the hub going
/// away mid-transfer, the client aborting — arrives here as the enumeration ending or throwing,
/// which is the same outcome by a different route.
/// </para>
/// </remarks>
public sealed class HubAttachmentSource(HubConnection connection) : IStreamedAttachmentSource
{
    public IAsyncEnumerable<AttachmentChunk> ReadAsync(Guid jobId, CancellationToken cancellationToken) =>
        connection.StreamAsync<AttachmentChunk>("StreamAttachments", jobId, cancellationToken);
}
