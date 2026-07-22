using InferHub.Coordinator.Cluster;
using InferHub.Tests.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace InferHub.Tests;

/// <summary>
/// The lease SQL itself, gated on <c>INFERHUB_TEST_POSTGRES</c> exactly like the vector-store and
/// usage-ledger suites. The property that matters is <b>single holder</b>: two coordinators
/// contending must never both come away believing they lead, and the only way to know that of a
/// conditional upsert is to run it against a real Postgres.
/// </summary>
[Collection("postgres")]
public class PostgresClusterLeaseTests : IAsyncLifetime
{
    private static readonly string? ConnString = Environment.GetEnvironmentVariable("INFERHUB_TEST_POSTGRES");

    private readonly string schema = "cluster_test_" + Guid.NewGuid().ToString("N")[..8];
    private readonly List<PostgresClusterLease> leases = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var lease in leases)
        {
            await lease.DisposeAsync();
        }

        if (ConnString is null) return;

        await using var dataSource = new NpgsqlDataSourceBuilder(ConnString).Build();
        await using var drop = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        await drop.ExecuteNonQueryAsync();
    }

    private PostgresClusterLease LeaseFor(string instanceId, int ttlSeconds = 15)
    {
        var lease = new PostgresClusterLease(
            Options.Create(new ClusterOptions
            {
                Enabled = true,
                InstanceId = instanceId,
                ConnectionString = ConnString!,
                Schema = schema,
                LeaseTtlSeconds = ttlSeconds
            }),
            NullLogger<PostgresClusterLease>.Instance);

        leases.Add(lease);
        return lease;
    }

    [PostgresFact]
    public async Task TheFirstContenderTakesTheLeaseAndTheSecondDoesNot()
    {
        var a = LeaseFor("hub-a");
        var b = LeaseFor("hub-b");

        var first = await a.TryAcquireOrRenewAsync(CancellationToken.None);
        var second = await b.TryAcquireOrRenewAsync(CancellationToken.None);

        Assert.True(first.Held);
        Assert.False(second.Held);
        Assert.Equal("hub-a", second.Holder);
    }

    [PostgresFact]
    public async Task RenewingKeepsTheFenceAndExtendsTheExpiry()
    {
        var a = LeaseFor("hub-a");

        var first = await a.TryAcquireOrRenewAsync(CancellationToken.None);
        var renewed = await a.TryAcquireOrRenewAsync(CancellationToken.None);

        Assert.True(renewed.Held);
        // A renewal is not an acquisition: a bumped fence would read as leadership moving when it
        // has not, and the fence is what tells an operator a failover actually happened.
        Assert.Equal(first.Fence, renewed.Fence);
        Assert.True(renewed.ExpiresAtUtc >= first.ExpiresAtUtc);
    }

    [PostgresFact]
    public async Task AnExpiredLeaseIsTakenOverAndTheFenceAdvances()
    {
        // A 1-second TTL stands in for a primary that died: nothing renews, the claim lapses, and
        // the standby's next probe finds a row it is allowed to take.
        var a = LeaseFor("hub-a", ttlSeconds: 1);
        var b = LeaseFor("hub-b");

        var first = await a.TryAcquireOrRenewAsync(CancellationToken.None);
        Assert.True(first.Held);

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        var taken = await b.TryAcquireOrRenewAsync(CancellationToken.None);

        Assert.True(taken.Held);
        Assert.Equal(first.Fence + 1, taken.Fence);
    }

    [PostgresFact]
    public async Task ReleasingHandsOverImmediatelyInsteadOfWaitingOutTheTtl()
    {
        var a = LeaseFor("hub-a");
        var b = LeaseFor("hub-b");

        await a.TryAcquireOrRenewAsync(CancellationToken.None);
        Assert.False((await b.TryAcquireOrRenewAsync(CancellationToken.None)).Held);

        await a.ReleaseAsync(CancellationToken.None);

        Assert.True((await b.TryAcquireOrRenewAsync(CancellationToken.None)).Held);
    }

    [PostgresFact]
    public async Task ConcurrentContendersProduceExactlyOneHolder()
    {
        // The property the whole phase rests on, run the only way it can be checked: many
        // instances racing the same statement at the same moment.
        var contenders = Enumerable.Range(0, 8).Select(i => LeaseFor("hub-" + i)).ToArray();

        var results = await Task.WhenAll(
            contenders.Select(lease => lease.TryAcquireOrRenewAsync(CancellationToken.None)));

        Assert.Single(results, r => r.Held);
    }

    [PostgresFact]
    public async Task TwoDeploymentsSharingADatabaseDoNotFightOverOneLease()
    {
        var mesh1 = new PostgresClusterLease(
            Options.Create(new ClusterOptions
            {
                Enabled = true, InstanceId = "hub-a", ConnectionString = ConnString!,
                Schema = schema, LeaseName = "mesh-1"
            }),
            NullLogger<PostgresClusterLease>.Instance);
        var mesh2 = new PostgresClusterLease(
            Options.Create(new ClusterOptions
            {
                Enabled = true, InstanceId = "hub-b", ConnectionString = ConnString!,
                Schema = schema, LeaseName = "mesh-2"
            }),
            NullLogger<PostgresClusterLease>.Instance);

        leases.Add(mesh1);
        leases.Add(mesh2);

        Assert.True((await mesh1.TryAcquireOrRenewAsync(CancellationToken.None)).Held);
        Assert.True((await mesh2.TryAcquireOrRenewAsync(CancellationToken.None)).Held);
    }
}
