using InferHub.Coordinator.Cluster;
using Microsoft.AspNetCore.Http;

namespace InferHub.Tests;

/// <summary>
/// The fence (phase 32). A partitioned active coordinator cannot be *told* it lost the lease —
/// by definition it cannot reach the database that knows. So the guard has to be local: demote
/// when this instance has not *proved* leadership within the TTL, measured on its own clock.
/// These tests are the reason that reasoning is safe to rely on.
/// </summary>
public class SplitBrainTests
{
    [Fact]
    public async Task ATransientBlipDoesNotDemoteTheActiveCoordinator()
    {
        // A dropped packet is not a lost election. Demoting on the first failed round-trip would
        // make a healthy pair flap leadership on ordinary network noise.
        var harness = new ClusterLeadershipTests.Harness(leaseTtlSeconds: 15, renewIntervalSeconds: 5);
        harness.Lease.Held = true;
        await harness.Service.TickAsync(CancellationToken.None);

        harness.Lease.Throws = new InvalidOperationException("connection refused");
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(ClusterRole.Active, harness.Membership.Role);
    }

    [Fact]
    public async Task APartitionedPrimaryFencesItselfWithinTheLeaseTtl()
    {
        var harness = new ClusterLeadershipTests.Harness(leaseTtlSeconds: 15, renewIntervalSeconds: 5);
        harness.Lease.Held = true;
        await harness.Service.TickAsync(CancellationToken.None);

        // The database is gone and stays gone. At the TTL boundary the row is free for the standby
        // to take, so this instance must have stopped believing it leads by exactly then.
        harness.Lease.Throws = new InvalidOperationException("connection refused");
        harness.Time.Advance(TimeSpan.FromSeconds(15));
        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(ClusterRole.Standby, harness.Membership.Role);
        Assert.Contains("could not be renewed", harness.Membership.Detail);
    }

    [Fact]
    public async Task TheFenceDoesNotWaitForTheRoundTripToComplete()
    {
        // Found live: against an unreachable database the attempt itself burns Npgsql's connect
        // timeout, so demotion landed ~8s after the lease row had already freed — a window in
        // which the standby holds the lease and the old primary still believes it leads. The
        // deadline is only a guarantee if nothing we do can overrun it, so it is checked before
        // any I/O and the attempt is bounded by what is left of it.
        var harness = new ClusterLeadershipTests.Harness(leaseTtlSeconds: 15, renewIntervalSeconds: 5);
        harness.Lease.Held = true;
        await harness.Service.TickAsync(CancellationToken.None);

        harness.Lease.Hangs = true;
        harness.Time.Advance(TimeSpan.FromSeconds(15));

        await harness.Service.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClusterRole.Standby, harness.Membership.Role);
        Assert.Equal(0, harness.Lease.AttemptsWhileHanging);
    }

    [Fact]
    public async Task AFencedPrimaryRefusesInference()
    {
        var harness = new ClusterLeadershipTests.Harness();
        harness.Lease.Held = true;
        await harness.Service.TickAsync(CancellationToken.None);

        var served = await ClusterRoleTests.InvokeAsync(harness.Membership, "/api/chat");
        Assert.Equal(StatusCodes.Status200OK, served.StatusCode);

        harness.Lease.Throws = new InvalidOperationException("connection refused");
        harness.Time.Advance(TimeSpan.FromSeconds(20));
        await harness.Service.TickAsync(CancellationToken.None);

        var refused = await ClusterRoleTests.InvokeAsync(harness.Membership, "/api/chat");
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, refused.StatusCode);
        Assert.Equal(ClusterRoleMiddleware.StandbyRole, refused.Headers[ClusterRoleMiddleware.RoleHeader]);
    }

    [Fact]
    public async Task AFencedPrimaryStillAnswersStatusAndMetrics()
    {
        // "Why is nothing being served?" has to be answerable from the instance that stopped
        // serving. A standby that goes dark is a standby nobody can diagnose.
        var harness = new ClusterLeadershipTests.Harness();
        harness.Membership.MarkStandby("fenced");

        foreach (var path in new[] { "/health", "/api/status", "/metrics", "/api/admin/nodes" })
        {
            var response = await ClusterRoleTests.InvokeAsync(harness.Membership, path);
            Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task LeadershipDoesNotBounceBackWithoutAFreshProof()
    {
        // Once fenced, the only way back is a successful acquire — not the mere passage of time
        // or the database becoming reachable again for something else.
        var harness = new ClusterLeadershipTests.Harness();
        harness.Lease.Held = true;
        await harness.Service.TickAsync(CancellationToken.None);

        harness.Lease.Throws = new InvalidOperationException("gone");
        harness.Time.Advance(TimeSpan.FromSeconds(20));
        await harness.Service.TickAsync(CancellationToken.None);
        Assert.Equal(ClusterRole.Standby, harness.Membership.Role);

        // The other hub took it while we were away, and it is still holding.
        harness.Lease.Throws = null;
        harness.Lease.Held = false;
        harness.Lease.Holder = "hub-b";
        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(ClusterRole.Standby, harness.Membership.Role);
    }
}
