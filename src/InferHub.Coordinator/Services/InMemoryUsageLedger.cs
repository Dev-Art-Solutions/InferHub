namespace InferHub.Coordinator.Services;

/// <summary>
/// The default ledger: in-memory, reset by a restart, exactly like every other counter on the
/// coordinator (rule 4). Honest, and useless for billing — that is what <c>Usage:Persistence=postgres</c>
/// is for. Bounded so that a long-lived deployment cannot turn metering into a slow memory leak:
/// past the cap the oldest records are dropped, oldest-first, and a query that reaches back past
/// the cap simply sees less history.
/// </summary>
public sealed class InMemoryUsageLedger : IUsageLedger
{
    internal const int MaxRecords = 200_000;

    private readonly object gate = new();
    private readonly List<UsageRecord> records = new();

    public ValueTask RecordAsync(UsageRecord record, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            records.Add(record);

            if (records.Count > MaxRecords)
            {
                records.RemoveRange(0, records.Count - MaxRecords);
            }
        }

        return ValueTask.CompletedTask;
    }

    public Task<IReadOnlyList<UsageAggregate>> QueryAsync(UsageQuery query, CancellationToken cancellationToken = default)
    {
        UsageRecord[] snapshot;
        lock (gate)
        {
            snapshot = records.ToArray();
        }

        var rows = snapshot
            .Where(r => Matches(r, query))
            .GroupBy(r => (r.ClientId, r.Model))
            .Select(g => new UsageAggregate(
                g.Key.ClientId,
                g.Key.Model,
                g.LongCount(),
                g.Sum(r => r.PromptTokens),
                g.Sum(r => r.CompletionTokens),
                g.LongCount(r => r.Fallback)))
            .OrderBy(a => a.ClientId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult<IReadOnlyList<UsageAggregate>>(rows);
    }

    private static bool Matches(UsageRecord record, UsageQuery query)
    {
        if (query.FromUtc is { } from && record.AtUtc < from)
        {
            return false;
        }

        if (query.ToUtc is { } to && record.AtUtc >= to)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(query.ClientId)
            && !string.Equals(record.ClientId, query.ClientId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(query.Model)
            && !string.Equals(record.Model, query.Model, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
