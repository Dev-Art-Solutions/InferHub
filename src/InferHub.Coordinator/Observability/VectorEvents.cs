using System.Collections.Concurrent;

namespace InferHub.Coordinator.Observability;

/// <summary>
/// Fan-out bus for vector-related lifecycle events (collection created/dropped,
/// replica assigned/lost, heal started/completed). The admin SSE stream subscribes to
/// this so an operator sees replication events land live in the console feed.
/// Subscribers are best-effort: a slow subscriber's channel drops rather than blocking
/// the producer, keeping the write path clean.
/// </summary>
public sealed class VectorEvents
{
    private readonly ConcurrentDictionary<int, Action<VectorEvent>> _subscribers = new();
    private int _nextId;
    private long _sequence;

    public IDisposable Subscribe(Action<VectorEvent> handler)
    {
        var id = Interlocked.Increment(ref _nextId);
        _subscribers[id] = handler;
        return new Subscription(this, id);
    }

    public void Publish(string kind, string? collection, IDictionary<string, object?>? data = null)
    {
        var seq = Interlocked.Increment(ref _sequence);
        var ev = new VectorEvent(seq, kind, collection, DateTimeOffset.UtcNow, data ?? new Dictionary<string, object?>());
        foreach (var handler in _subscribers.Values)
        {
            try { handler(ev); }
            catch { /* observer failures never break producers */ }
        }
    }

    private sealed class Subscription(VectorEvents parent, int id) : IDisposable
    {
        public void Dispose() => parent._subscribers.TryRemove(id, out _);
    }
}

public sealed record VectorEvent(
    long Sequence,
    string Kind,
    string? Collection,
    DateTimeOffset AtUtc,
    IDictionary<string, object?> Data);
