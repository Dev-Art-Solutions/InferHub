namespace InferHub.Coordinator.Services;

public sealed record NodeSnapshot(
    string ConnectionId,
    string NodeId,
    string Name,
    string OllamaEndpoint,
    string Version,
    DateTimeOffset LastSeenUtc,
    double AgeSeconds,
    int InFlight,
    int ModelCount);
