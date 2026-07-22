using InferHub.Coordinator.Cluster;
using InferHub.Coordinator.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// The leadership state machine (phase 32), driven against a stub lease. The SQL is exercised by
/// <see cref="PostgresClusterLeaseTests"/>; what is pinned here is the part that must never be
/// wrong — when this coordinator believes it is leading.
/// </summary>
public class ClusterLeadershipTests
{
    [Fact]
    public void ClusterModeStartsStandbyAndNotActive()
    {
        // A coordinator that assumed leadership until told otherwise would give every cold start
        // a window with two primaries — the exact thing the lease exists to prevent.
        IClusterMembership membership = new ClusterMembership("hub-a");

        Assert.Equal(ClusterRole.Standby, membership.Role);
        Assert.False(membership.IsActive);
        Assert.True(membership.Enabled);
    }

    [Fact]
    public void SingleCoordinatorIsAlwaysActiveAndReportsNoCluster()
    {
        IClusterMembership membership = new SingleCoordinatorMembership();

        Assert.True(membership.IsActive);
        // Enabled=false is what keeps Cluster:Enabled=false byte-identical to v2.13: no role
        // header, no standby 503, no "cluster" key on /api/status, no cluster series on /metrics.
        Assert.False(membership.Enabled);
    }

    [Fact]
    public async Task AcquiringTheLeasePromotesToActive()
    {
        var harness = new Harness();
        harness.Lease.Held = true;

        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(ClusterRole.Active, harness.Membership.Role);
        Assert.Equal(1, harness.Membership.Fence);
        Assert.Equal(harness.Time.GetUtcNow(), harness.Membership.ActiveSinceUtc);
    }

    [Fact]
    public async Task RenewingKeepsTheOriginalActiveSince()
    {
        var harness = new Harness();
        harness.Lease.Held = true;
        await harness.Service.TickAsync(CancellationToken.None);
        var promotedAt = harness.Membership.ActiveSinceUtc;

        harness.Time.Advance(TimeSpan.FromSeconds(5));
        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(ClusterRole.Active, harness.Membership.Role);
        Assert.Equal(promotedAt, harness.Membership.ActiveSinceUtc);
    }

    [Fact]
    public async Task AnotherHolderKeepsThisInstanceStandby()
    {
        var harness = new Harness();
        harness.Lease.Held = false;
        harness.Lease.Holder = "hub-b";

        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(ClusterRole.Standby, harness.Membership.Role);
        Assert.Contains("hub-b", harness.Membership.Detail);
    }

    [Fact]
    public async Task LosingTheLeaseToAnotherHolderDemotesAndDropsTheFleet()
    {
        var harness = new Harness();
        harness.Lease.Held = true;
        await harness.Service.TickAsync(CancellationToken.None);
        Assert.Equal(ClusterRole.Active, harness.Membership.Role);

        harness.Lease.Held = false;
        harness.Lease.Holder = "hub-b";
        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(ClusterRole.Standby, harness.Membership.Role);
        // A node left attached to a hub that will refuse its work is a node the mesh has lost.
        Assert.Equal(1, harness.Connections.AbortAllCalls);
    }

    [Fact]
    public async Task ReleasingIsSkippedForAStandbyOnShutdown()
    {
        // Releasing a lease we do not hold would hand the mesh a leaderless window it then has to
        // wait a whole TTL to climb out of.
        var harness = new Harness();
        harness.Lease.Held = false;
        harness.Lease.Holder = "hub-b";
        await harness.Service.TickAsync(CancellationToken.None);

        await harness.Service.ReleaseIfActiveAsync();

        Assert.Equal(0, harness.Lease.Releases);
    }

    [Fact]
    public async Task AnActiveCoordinatorReleasesTheLeaseOnShutdown()
    {
        // So the standby promotes in milliseconds instead of waiting out the TTL.
        var harness = new Harness();
        harness.Lease.Held = true;
        await harness.Service.TickAsync(CancellationToken.None);

        await harness.Service.ReleaseIfActiveAsync();

        Assert.Equal(1, harness.Lease.Releases);
    }

    [Fact]
    public void RenewIntervalMustBeWellUnderTheTtl()
    {
        var validator = new ClusterOptionsValidator();

        var tooClose = validator.Validate(null, new ClusterOptions
        {
            Enabled = true,
            ConnectionString = "Host=localhost",
            LeaseTtlSeconds = 10,
            RenewIntervalSeconds = 5
        });

        Assert.True(tooClose.Failed);
        Assert.Contains(tooClose.Failures!, f => f.Contains("RenewIntervalSeconds"));

        var sane = validator.Validate(null, new ClusterOptions
        {
            Enabled = true,
            ConnectionString = "Host=localhost",
            LeaseTtlSeconds = 15,
            RenewIntervalSeconds = 5
        });

        Assert.True(sane.Succeeded);
    }

    [Fact]
    public void DisabledClusterNeedsNoConnectionString()
    {
        // Rule: Cluster:Enabled=false must not make a v2.13 config invalid.
        var result = new ClusterOptionsValidator().Validate(null, new ClusterOptions());

        Assert.True(result.Succeeded);
    }

    internal sealed class Harness
    {
        public Harness(int leaseTtlSeconds = 15, int renewIntervalSeconds = 5)
        {
            Options = new ClusterOptions
            {
                Enabled = true,
                InstanceId = "hub-a",
                ConnectionString = "Host=localhost",
                LeaseTtlSeconds = leaseTtlSeconds,
                RenewIntervalSeconds = renewIntervalSeconds
            };

            Membership = new ClusterMembership(Options.InstanceId);
            Service = new ClusterLeaseService(
                Lease,
                Membership,
                Microsoft.Extensions.Options.Options.Create(Options),
                Connections,
                NullLogger<ClusterLeaseService>.Instance,
                Time);
        }

        public ClusterOptions Options { get; }

        public StubLease Lease { get; } = new();

        public ClusterMembership Membership { get; }

        public CountingConnections Connections { get; } = new();

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero));

        public ClusterLeaseService Service { get; }
    }

    internal sealed class StubLease : IClusterLease
    {
        public bool Held { get; set; }

        public string? Holder { get; set; }

        /// <summary>Set to simulate a partition: the database cannot be reached at all.</summary>
        public Exception? Throws { get; set; }

        /// <summary>
        /// Simulates the worse partition: the round-trip neither succeeds nor fails, it just sits
        /// there burning the connect timeout. Waits on the caller's token, so a bounded attempt
        /// unblocks and an unbounded one hangs.
        /// </summary>
        public bool Hangs { get; set; }

        /// <summary>Attempts that actually reached the lease after <see cref="Hangs"/> was set.</summary>
        public int AttemptsWhileHanging { get; private set; }

        public long Fence { get; set; } = 1;

        public int Releases { get; private set; }

        public Task<LeaseResult> TryAcquireOrRenewAsync(CancellationToken cancellationToken)
        {
            if (Hangs)
            {
                AttemptsWhileHanging++;
                return Task.Delay(Timeout.Infinite, cancellationToken)
                    .ContinueWith(_ => default(LeaseResult), TaskContinuationOptions.ExecuteSynchronously);
            }

            if (Throws is not null)
            {
                return Task.FromException<LeaseResult>(Throws);
            }

            return Task.FromResult(Held
                ? new LeaseResult(true, Fence, "hub-a", DateTimeOffset.UtcNow.AddSeconds(15))
                : new LeaseResult(false, Fence, Holder, DateTimeOffset.UtcNow.AddSeconds(15)));
        }

        public Task ReleaseAsync(CancellationToken cancellationToken)
        {
            Releases++;
            return Task.CompletedTask;
        }
    }

    internal sealed class CountingConnections : INodeConnectionTracker
    {
        public int AbortAllCalls { get; private set; }

        public void Track(string connectionId, HubCallerContext context) { }

        public void Forget(string connectionId) { }

        public bool Abort(string connectionId) => false;

        public int AbortAll()
        {
            AbortAllCalls++;
            return 0;
        }
    }

    internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan delta) => now += delta;
    }
}
