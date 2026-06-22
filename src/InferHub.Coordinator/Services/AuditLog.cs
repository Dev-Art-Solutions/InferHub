using System.Collections.Concurrent;

namespace InferHub.Coordinator.Services;

public interface IAuditLog
{
    void Record(string nodeId, string action, string by, DateTimeOffset atUtc);

    AuditEntry? Get(string nodeId);

    void Forget(string nodeId);
}

public sealed record AuditEntry(string Action, DateTimeOffset AtUtc, string By);

public sealed class AuditLog : IAuditLog
{
    private readonly ConcurrentDictionary<string, AuditEntry> entries =
        new(StringComparer.OrdinalIgnoreCase);

    public void Record(string nodeId, string action, string by, DateTimeOffset atUtc)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        entries[nodeId.Trim()] = new AuditEntry(
            action.Trim(),
            atUtc,
            string.IsNullOrWhiteSpace(by) ? "admin" : by.Trim());
    }

    public AuditEntry? Get(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return null;
        }

        return entries.TryGetValue(nodeId.Trim(), out var entry) ? entry : null;
    }

    public void Forget(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return;
        }

        entries.TryRemove(nodeId.Trim(), out _);
    }
}
