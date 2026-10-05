using InferHub.Shared.Contracts;

namespace InferHub.Node.Backends;

/// <summary>
/// A backend that can score a closed set of answers rather than generate one (phase 94). Today that
/// is colibri's Brio and nothing else; registered only on a colibri node, so a node that cannot
/// score has no scorer at all rather than one that throws (67 D4's "declared, not discovered").
/// </summary>
/// <remarks>
/// It answers a <see cref="ToolResult"/> because a <c>score</c> job <em>is</em> a
/// <see cref="ToolJob"/> (94 D1): the node states which kind of failure it was and the edge renders
/// it, exactly as for a tool worker.
/// </remarks>
public interface IClosedSetScorer
{
    Task<ToolResult> ScoreAsync(ToolJob job, CancellationToken cancellationToken);
}
