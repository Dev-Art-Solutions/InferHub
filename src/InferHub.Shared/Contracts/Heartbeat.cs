namespace InferHub.Shared.Contracts;

public sealed record Heartbeat(
    string NodeId,
    DateTimeOffset Timestamp,
    int InFlight,
    /// <summary>
    /// What this node's inference backend is doing, phase 69. <b>Null is "no opinion" and is never
    /// read as unhealthy</b> — a node older than v3.36 has no such field, a node with
    /// <c>Ollama:Supervisor:Watch=false</c> sends none, and a vendor-typed node has nothing cheap
    /// to probe. All three must route exactly as they did (40 D1's mixed-fleet rule).
    /// </summary>
    BackendHealth? Backend = null,
    /// <summary>
    /// Whether this node's own <c>Node:ResourceLimits</c> cap is currently tripped, phase 82. <b>Null
    /// is "no opinion" and is never read as unhealthy</b> — a node older than v3.47, or one with no
    /// cap configured, sends none, and both must route exactly as they did before this field existed
    /// (the same mixed-fleet rule phase-40 D1 and phase-69 D5 both hold to).
    /// </summary>
    bool? ResourceThrottled = null);
