using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

public sealed class ConversationAffinity : IConversationAffinity
{
    private readonly ConcurrentDictionary<string, Entry> map = new(StringComparer.Ordinal);
    private readonly TimeSpan slidingExpiry;
    private readonly TimeProvider timeProvider;
    private readonly IAffinityStore store;

    public ConversationAffinity(IOptions<RouterOptions> options)
        : this(options, new NoAffinityStore(), TimeProvider.System)
    {
    }

    public ConversationAffinity(IOptions<RouterOptions> options, TimeProvider timeProvider)
        : this(options, new NoAffinityStore(), timeProvider)
    {
    }

    public ConversationAffinity(IOptions<RouterOptions> options, IAffinityStore store, TimeProvider timeProvider)
    {
        var minutes = options.Value.AffinitySlidingMinutes <= 0 ? 10 : options.Value.AffinitySlidingMinutes;
        slidingExpiry = TimeSpan.FromMinutes(minutes);
        this.timeProvider = timeProvider;
        this.store = store;

        LoadPersisted();
    }

    public int Count => map.Count;

    public string? GetNodeFor(string conversationKey)
    {
        if (string.IsNullOrEmpty(conversationKey) || !map.TryGetValue(conversationKey, out var entry))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();

        if (now - entry.LastUsed > slidingExpiry)
        {
            if (map.TryRemove(new KeyValuePair<string, Entry>(conversationKey, entry)))
            {
                store.Forget(conversationKey);
            }

            return null;
        }

        map[conversationKey] = entry with { LastUsed = now };
        return entry.NodeId;
    }

    public void Record(string conversationKey, string nodeId)
    {
        if (string.IsNullOrEmpty(conversationKey) || string.IsNullOrEmpty(nodeId))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        map[conversationKey] = new Entry(nodeId, now);
        store.Record(conversationKey, nodeId, now);
    }

    public void Forget(string conversationKey)
    {
        if (!string.IsNullOrEmpty(conversationKey) && map.TryRemove(conversationKey, out _))
        {
            store.Forget(conversationKey);
        }
    }

    public int ForgetNode(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId))
        {
            return 0;
        }

        var removed = 0;

        foreach (var pair in map)
        {
            if (string.Equals(pair.Value.NodeId, nodeId, StringComparison.Ordinal)
                && map.TryRemove(new KeyValuePair<string, Entry>(pair.Key, pair.Value)))
            {
                store.Forget(pair.Key);
                removed++;
            }
        }

        return removed;
    }

    // Load persisted hints on startup, dropping any already past their sliding expiry — a hint that
    // was already stale when the coordinator went down is not worth resurrecting.
    private void LoadPersisted()
    {
        var now = timeProvider.GetUtcNow();

        foreach (var hint in store.Load())
        {
            if (string.IsNullOrEmpty(hint.ConversationKey) || string.IsNullOrEmpty(hint.NodeId))
            {
                continue;
            }

            if (now - hint.LastUsed > slidingExpiry)
            {
                store.Forget(hint.ConversationKey);
                continue;
            }

            map[hint.ConversationKey] = new Entry(hint.NodeId, hint.LastUsed);
        }
    }

    private sealed record Entry(string NodeId, DateTimeOffset LastUsed);
}
