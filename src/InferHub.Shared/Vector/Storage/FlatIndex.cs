using System.Text.Json;

namespace InferHub.Shared.Vector.Storage;

public sealed class FlatIndex
{
    private readonly int _dimension;
    private readonly DistanceMetric _metric;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public FlatIndex(int dimension, DistanceMetric metric)
    {
        if (dimension < 1) throw new ArgumentOutOfRangeException(nameof(dimension), "dimension must be >= 1");
        _dimension = dimension;
        _metric = metric;
    }

    public int Count => _entries.Count;

    public int Dimension => _dimension;

    public DistanceMetric Metric => _metric;

    public void Upsert(VectorRecord record)
    {
        if (record.Vector.Length != _dimension)
        {
            throw new ArgumentException($"vector length {record.Vector.Length} does not match collection dimension {_dimension}", nameof(record));
        }

        var original = (float[])record.Vector.Clone();
        var indexed = _metric == DistanceMetric.Cosine ? Normalise(original) : original;

        _entries[record.Id] = new Entry(record.Id, original, indexed, record.Payload, record.Metadata, record.SeqNo, record.TimestampUtc);
    }

    public bool Delete(string id) => _entries.Remove(id);

    public VectorRecord? Get(string id)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            return null;
        }

        return new VectorRecord(entry.Id, entry.Original, entry.Payload, entry.Metadata, entry.SeqNo, entry.TimestampUtc);
    }

    public IEnumerable<VectorRecord> EnumerateLive()
    {
        foreach (var entry in _entries.Values)
        {
            yield return new VectorRecord(entry.Id, entry.Original, entry.Payload, entry.Metadata, entry.SeqNo, entry.TimestampUtc);
        }
    }

    public IReadOnlyList<VectorMatch> Query(float[] vector, int k, IReadOnlyDictionary<string, string>? filter)
    {
        if (vector.Length != _dimension)
        {
            throw new ArgumentException($"query vector length {vector.Length} does not match collection dimension {_dimension}", nameof(vector));
        }

        if (k < 1)
        {
            return Array.Empty<VectorMatch>();
        }

        var queryVector = _metric == DistanceMetric.Cosine ? Normalise(vector) : vector;
        var smallerIsBetter = _metric == DistanceMetric.L2;

        var matches = new List<VectorMatch>(_entries.Count);
        foreach (var entry in _entries.Values)
        {
            if (filter is { Count: > 0 } && !MatchesFilter(entry.Metadata, filter))
            {
                continue;
            }

            double score = _metric switch
            {
                DistanceMetric.Cosine => Dot(queryVector, entry.Indexed),
                DistanceMetric.Dot => Dot(queryVector, entry.Original),
                DistanceMetric.L2 => EuclideanDistance(queryVector, entry.Original),
                _ => 0
            };

            matches.Add(new VectorMatch(entry.Id, score, entry.Payload, entry.Metadata));
        }

        matches.Sort((a, b) => smallerIsBetter
            ? a.Score.CompareTo(b.Score)
            : b.Score.CompareTo(a.Score));

        if (matches.Count > k)
        {
            matches.RemoveRange(k, matches.Count - k);
        }

        return matches;
    }

    private static bool MatchesFilter(IReadOnlyDictionary<string, string>? metadata, IReadOnlyDictionary<string, string> filter)
    {
        if (metadata is null) return false;
        foreach (var pair in filter)
        {
            if (!metadata.TryGetValue(pair.Key, out var value) || !string.Equals(value, pair.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += (double)a[i] * b[i];
        }
        return sum;
    }

    private static double EuclideanDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = (double)a[i] - b[i];
            sum += d * d;
        }
        return Math.Sqrt(sum);
    }

    private static float[] Normalise(float[] vector)
    {
        double sumSq = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            sumSq += (double)vector[i] * vector[i];
        }

        var magnitude = Math.Sqrt(sumSq);
        if (magnitude <= double.Epsilon)
        {
            return (float[])vector.Clone();
        }

        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            result[i] = (float)(vector[i] / magnitude);
        }
        return result;
    }

    private sealed record Entry(
        string Id,
        float[] Original,
        float[] Indexed,
        JsonElement? Payload,
        IReadOnlyDictionary<string, string>? Metadata,
        long SeqNo,
        DateTimeOffset TimestampUtc);
}
