using InferHub.Shared.Contracts;

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
    int LocalInFlight,
    int ModelCount,
    IReadOnlyDictionary<string, string> Labels,
    int? MaxConcurrency,
    bool Cordoned,
    bool SupportsModelManagement = false,
    /// Already resolved (phase 40): a node that declared nothing carries chat + embed over
    /// everything it holds, so a reader never has to know whether the node is an old one. The
    /// registry always fills this; it is nullable only so a hand-built snapshot in a test need
    /// not care.
    IReadOnlyList<NodeCapability>? Capabilities = null,
    /// What the node last said about its inference backend (phase 69). Null is no opinion: an
    /// older node, one watching nothing, or a vendor-typed one.
    BackendHealth? BackendHealth = null);
