namespace InferHub.Coordinator.Services;

public sealed record RoutableNode(
    string ConnectionId,
    string NodeId,
    string Name);
