using System.Threading.Channels;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Tool dispatch (phase 41). A <em>capability</em> on the dispatcher, not four more methods on
/// <see cref="IDispatcher"/> — phase-34 D1's shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is exactly one implementation and it is <c>Dispatcher</c> itself.</b> Tool jobs go
/// through the same job registry, the same in-flight accounting, the same stream plumbing and the
/// same <c>FailForConnection</c> as inference jobs — the brief's "no second dispatcher" is met by
/// the class, and the split interface exists so that the several test doubles standing in for
/// inference dispatch are not made to fake a method they never call.
/// </para>
/// <para>
/// The node-facing side is <c>ExecuteToolJob</c> / <c>ExecuteStreamingToolJob</c> down the
/// connection the node opened, and <c>ToolResult</c> / <c>StreamToolChunks</c> back up it. The
/// coordinator still never dials a node (phase-26 D1).
/// </para>
/// </remarks>
public interface IToolDispatcher
{
    Task<ToolResult> DispatchToolAsync(RoutableNode node, ToolJob job, CancellationToken cancellationToken);

    /// <summary>
    /// The same dispatch, with somewhere for the node's <c>progress</c> frames to go (phase 47, D2).
    /// </summary>
    /// <remarks>
    /// A blocking tool job that also reports progress is not a contradiction: the answer is still
    /// one <see cref="ToolResult"/> on the result path, and the progress arrives out of band on the
    /// connection the node already opened. The alternative — driving image jobs through
    /// <see cref="DispatchToolStreamAsync"/> — cannot work, because a streaming tool response
    /// deliberately carries no attachments (phase-41's <c>StreamAsync</c> refuses them), and an
    /// image <em>is</em> an attachment.
    /// </remarks>
    Task<ToolResult> DispatchToolAsync(
        RoutableNode node,
        ToolJob job,
        IProgress<ToolChunk>? progress,
        CancellationToken cancellationToken);

    Task<ChannelReader<ToolChunk>> DispatchToolStreamAsync(
        RoutableNode node,
        ToolJob job,
        CancellationToken cancellationToken);

    bool CompleteTool(ToolResult result);

    bool WriteToolChunk(ToolChunk chunk);

    /// <summary>
    /// Hands the dispatcher a body that is still arriving, so the node can pull it (phase 53, D1).
    /// Returns a registration the edge disposes when the dispatch is over — the upload outlives no
    /// request, and a job id that is not registered is a stream the hub refuses.
    /// </summary>
    IDisposable RegisterUpload(Guid jobId, Endpoints.StreamedUpload upload);

    /// <summary>
    /// The node's side of it: the frames for a job it was told carries a streamed attachment. An
    /// unknown job id yields nothing rather than throwing — a node reconnecting into a hub that has
    /// forgotten the job must fail the job, not the connection.
    /// </summary>
    IAsyncEnumerable<AttachmentChunk> ReadUploadAsync(Guid jobId, CancellationToken cancellationToken);
}
