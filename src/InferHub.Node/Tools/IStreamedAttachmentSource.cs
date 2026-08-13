using InferHub.Shared.Contracts;

namespace InferHub.Node.Tools;

/// <summary>
/// Where the bytes of a streamed attachment come from (phase 53, D1).
/// </summary>
/// <remarks>
/// <para>
/// Two implementations and neither knows about the other, which is phase-41 D8's framing for the
/// fourth time: in a mesh it is the hub's <c>StreamAttachments</c> stream pulled down the connection
/// the node already opened; in solo mode it is the request body itself, with no hop at all.
/// <see cref="ToolExecutor"/> takes either and cannot tell which it was handed — the scratch file it
/// writes, and the <c>ToolFile</c> path the worker reads, are identical.
/// </para>
/// <para>
/// It is an <c>IAsyncEnumerable</c> rather than a <c>Stream</c> because the frames carry the
/// boundaries: a multipart body may hold more than one part, and where one ends is information the
/// bytes themselves do not have.
/// </para>
/// </remarks>
public interface IStreamedAttachmentSource
{
    IAsyncEnumerable<AttachmentChunk> ReadAsync(Guid jobId, CancellationToken cancellationToken);
}
