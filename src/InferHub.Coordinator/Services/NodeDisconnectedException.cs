namespace InferHub.Coordinator.Services;

public sealed class NodeDisconnectedException : Exception
{
    public NodeDisconnectedException(string connectionId, string? message = null, Exception? inner = null)
        : base(message ?? "node disconnected before the job could start", inner)
    {
        ConnectionId = connectionId;
    }

    public string ConnectionId { get; }
}
