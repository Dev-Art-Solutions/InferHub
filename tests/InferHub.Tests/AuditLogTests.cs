using InferHub.Coordinator.Services;

namespace InferHub.Tests;

public class AuditLogTests
{
    [Fact]
    public void RecordStoresLatestEntryPerNode()
    {
        var log = new AuditLog();
        var first = DateTimeOffset.UtcNow;
        var second = first.AddSeconds(5);

        log.Record("node-1", "cordon", "10.0.0.1", first);
        log.Record("node-1", "uncordon", "10.0.0.2", second);

        var entry = log.Get("node-1");
        Assert.NotNull(entry);
        Assert.Equal("uncordon", entry!.Action);
        Assert.Equal("10.0.0.2", entry.By);
        Assert.Equal(second, entry.AtUtc);
    }

    [Fact]
    public void GetReturnsNullForUnknownNode()
    {
        var log = new AuditLog();
        Assert.Null(log.Get("missing"));
    }

    [Fact]
    public void GetIsCaseInsensitive()
    {
        var log = new AuditLog();
        log.Record("Node-1", "cordon", "admin", DateTimeOffset.UtcNow);

        Assert.NotNull(log.Get("node-1"));
        Assert.NotNull(log.Get("NODE-1"));
    }

    [Fact]
    public void RecordIgnoresEmptyNodeId()
    {
        var log = new AuditLog();
        log.Record("", "cordon", "admin", DateTimeOffset.UtcNow);
        Assert.Null(log.Get(""));
    }

    [Fact]
    public void RecordFallsBackToAdminWhenByIsBlank()
    {
        var log = new AuditLog();
        log.Record("node-1", "cordon", "", DateTimeOffset.UtcNow);

        var entry = log.Get("node-1");
        Assert.NotNull(entry);
        Assert.Equal("admin", entry!.By);
    }

    [Fact]
    public void ForgetRemovesEntry()
    {
        var log = new AuditLog();
        log.Record("node-1", "cordon", "admin", DateTimeOffset.UtcNow);

        log.Forget("node-1");

        Assert.Null(log.Get("node-1"));
    }
}
