namespace InferHub.Coordinator.Services;

/// <summary>One persisted affinity hint: a conversation pinned to a stable node id, last seen at.</summary>
public readonly record struct PersistedAffinity(string ConversationKey, string NodeId, DateTimeOffset LastUsed);

/// <summary>
/// The durability seam for conversation affinity (phase 30). The default implementation
/// (<see cref="NoAffinityStore"/>) is a no-op, so <c>Persistence=none</c> keeps affinity purely
/// in-memory. <see cref="FileAffinityStore"/> writes a derived cache of routing hints to disk.
/// It is never a source of truth — a missing or stale entry costs one cold model load, never a
/// wrong answer — so it does not become a third authority alongside the vector store and ledger.
/// </summary>
public interface IAffinityStore
{
    /// <summary>Read the persisted hints back on startup. Empty for the no-op store.</summary>
    IReadOnlyCollection<PersistedAffinity> Load();

    /// <summary>Persist (or refresh) a conversation → node hint.</summary>
    void Record(string conversationKey, string nodeId, DateTimeOffset lastUsed);

    /// <summary>Persist the removal of a conversation's hint.</summary>
    void Forget(string conversationKey);
}

/// <summary>The default: affinity stays in-memory, so a restart resets it like every other counter.</summary>
public sealed class NoAffinityStore : IAffinityStore
{
    public IReadOnlyCollection<PersistedAffinity> Load() => [];

    public void Record(string conversationKey, string nodeId, DateTimeOffset lastUsed)
    {
    }

    public void Forget(string conversationKey)
    {
    }
}
