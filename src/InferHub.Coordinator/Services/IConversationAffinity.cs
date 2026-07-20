namespace InferHub.Coordinator.Services;

public interface IConversationAffinity
{
    /// <summary>
    /// The stable <c>nodeId</c> a conversation is pinned to, or null. Re-keyed off the SignalR
    /// <c>connectionId</c> in phase 30: a connection id is not stable across a node's own reconnect,
    /// so keying on it dropped every warm conversation the moment a node bounced. The router resolves
    /// the returned node id to the current connection at dispatch time.
    /// </summary>
    string? GetNodeFor(string conversationKey);

    void Record(string conversationKey, string nodeId);

    void Forget(string conversationKey);

    /// <summary>
    /// Drop every conversation pinned to a node. Called only on an *explicit* deregister — a node
    /// the operator says is gone for good. A mere disconnect no longer forgets: the node may
    /// reconnect with a new connection id and its warm conversations must survive (that is the whole
    /// point of the re-key). A hint for a node that is simply absent resolves to a clean miss anyway.
    /// </summary>
    int ForgetNode(string nodeId);

    /// <summary>Live affinity entries — an operational gauge for <c>/api/status</c> and <c>/metrics</c>.</summary>
    int Count { get; }
}
