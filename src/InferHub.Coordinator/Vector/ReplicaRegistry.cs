using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace InferHub.Coordinator.Vector;

/// <summary>
/// In-memory map of collection → connection IDs of nodes currently holding a live replica.
/// Source-of-truth for what the hub believes is on disk on each node. The raw store on the
/// hub stays authoritative for the data itself; this registry is just placement state.
/// </summary>
public sealed class ReplicaRegistry
{
    private readonly ConcurrentDictionary<string, ImmutableHashSet<string>> _placement =
        new(StringComparer.OrdinalIgnoreCase);

    public void Add(string collection, string connectionId)
    {
        _placement.AddOrUpdate(
            collection,
            _ => ImmutableHashSet.Create(StringComparer.Ordinal, connectionId),
            (_, current) => current.Add(connectionId));
    }

    public bool Remove(string collection, string connectionId)
    {
        if (!_placement.TryGetValue(collection, out var current)) return false;
        var next = current.Remove(connectionId);
        if (next.Count == current.Count) return false;
        if (next.IsEmpty)
        {
            _placement.TryRemove(collection, out _);
        }
        else
        {
            _placement[collection] = next;
        }
        return true;
    }

    public IReadOnlyCollection<string> Holders(string collection)
    {
        return _placement.TryGetValue(collection, out var holders)
            ? holders
            : (IReadOnlyCollection<string>)Array.Empty<string>();
    }

    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> Snapshot()
    {
        return _placement.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyCollection<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Forget every placement that referenced <paramref name="connectionId"/> (a node went away).
    /// Returns the collections affected so callers can heal them.
    /// </summary>
    public IReadOnlyCollection<string> ForgetConnection(string connectionId)
    {
        var affected = new List<string>();
        foreach (var pair in _placement)
        {
            if (pair.Value.Contains(connectionId) && Remove(pair.Key, connectionId))
            {
                affected.Add(pair.Key);
            }
        }
        return affected;
    }

    public void Clear(string collection)
    {
        _placement.TryRemove(collection, out _);
    }
}
