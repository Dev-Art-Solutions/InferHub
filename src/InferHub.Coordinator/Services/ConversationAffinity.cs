using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

public sealed class ConversationAffinity : IConversationAffinity
{
    private readonly ConcurrentDictionary<string, Entry> map = new();
    private readonly TimeSpan slidingExpiry;
    private readonly TimeProvider timeProvider;

    public ConversationAffinity(IOptions<RouterOptions> options)
        : this(options, TimeProvider.System)
    {
    }

    public ConversationAffinity(IOptions<RouterOptions> options, TimeProvider timeProvider)
    {
        var minutes = options.Value.AffinitySlidingMinutes <= 0 ? 10 : options.Value.AffinitySlidingMinutes;
        slidingExpiry = TimeSpan.FromMinutes(minutes);
        this.timeProvider = timeProvider;
    }

    public string? GetNodeFor(string conversationKey)
    {
        if (string.IsNullOrEmpty(conversationKey) || !map.TryGetValue(conversationKey, out var entry))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();

        if (now - entry.LastUsed > slidingExpiry)
        {
            map.TryRemove(new KeyValuePair<string, Entry>(conversationKey, entry));
            return null;
        }

        map[conversationKey] = entry with { LastUsed = now };
        return entry.ConnectionId;
    }

    public void Record(string conversationKey, string connectionId)
    {
        if (string.IsNullOrEmpty(conversationKey) || string.IsNullOrEmpty(connectionId))
        {
            return;
        }

        map[conversationKey] = new Entry(connectionId, timeProvider.GetUtcNow());
    }

    public void Forget(string conversationKey)
    {
        if (!string.IsNullOrEmpty(conversationKey))
        {
            map.TryRemove(conversationKey, out _);
        }
    }

    public int ForgetConnection(string connectionId)
    {
        if (string.IsNullOrEmpty(connectionId))
        {
            return 0;
        }

        var removed = 0;

        foreach (var pair in map)
        {
            if (pair.Value.ConnectionId == connectionId
                && map.TryRemove(new KeyValuePair<string, Entry>(pair.Key, pair.Value)))
            {
                removed++;
            }
        }

        return removed;
    }

    private sealed record Entry(string ConnectionId, DateTimeOffset LastUsed);
}
