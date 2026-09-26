namespace InferHub.Coordinator.Services;

public sealed record RoutableNode(
    string ConnectionId,
    string NodeId,
    string Name,
    /// Who holds the node's card (phase 86). Null = the node does not run on demand, which the
    /// router reads as always warm — the tiering collapses and routing is what it was before.
    InferHub.Shared.Contracts.OnDemandState? OnDemand = null);
