namespace InferHub.Shared.Contracts;

public sealed record Heartbeat(
    string NodeId,
    DateTimeOffset Timestamp,
    int InFlight);
