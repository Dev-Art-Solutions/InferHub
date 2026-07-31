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

    Task<ChannelReader<ToolChunk>> DispatchToolStreamAsync(
        RoutableNode node,
        ToolJob job,
        CancellationToken cancellationToken);

    bool CompleteTool(ToolResult result);

    bool WriteToolChunk(ToolChunk chunk);
}
