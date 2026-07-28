using InferHub.Shared.Vector;

namespace InferHub.Node.Retrieval;

/// <summary>
/// Adapts the node's <see cref="ILogger"/> to the shared retrieval stack's <see cref="IVectorLog"/>
/// seam (phase-38 D3). The coordinator has its own three-line copy of this, for the same reason
/// phase-37 D6 duplicates the ten lines that write a frame to a response: what is shared is the
/// content, and a host's own logger plumbing is not content.
/// </summary>
public sealed class NodeVectorLog<T>(ILogger<T> logger) : IVectorLog
{
    public void Info(string message, params object?[] args) => logger.LogInformation(message, args);

    public void Warn(Exception? error, string message, params object?[] args) => logger.LogWarning(error, message, args);

    public void Error(Exception? error, string message, params object?[] args) => logger.LogError(error, message, args);

    public void Debug(string message, params object?[] args) => logger.LogDebug(message, args);
}
