using InferHub.Coordinator.Cluster;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 131: automatic failback to a preferred coordinator, and a standby that refuses writes to the shared identity record.
/// </summary>
public class FailbackTests
{
    [Fact]
    public async Task AStandbyThatIsNotPreferredDoesNotContendWhileThePreferredIsAlive()
    {
        var harness = new Harness("hub-b", preferred: "hub-a");
        harness.Lease.Held = true;
        harness.Lease.Seen["hub-a"] = TimeSpan.FromSeconds(2);

        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(ClusterRole.Standby, harness.Membership.Role);
        Assert.Equal(0, harness.Lease.Attempts);
        Assert.Contains("'hub-a' is alive", harness.Membership.Detail);
        Assert.Equal(1, harness.Lease.Heartbeats);
    }

    [Fact]
    public async Task WhenThePreferredIsDeadAnotherInstanceTakesTheLeaseAsBefore()
    {
        var harness = new Harness("hub-b", preferred: "hub-a");
        harness.Lease.Held = true;
        harness.Lease.Seen["hub-a"] = TimeSpan.FromSeconds(16);

        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(ClusterRole.Active, harness.Membership.Role);
    }

    [Fact]
    public async Task AnActiveInstanceHandsTheLeaseBackOnlyAfterThePreferredHasBeenAliveLongEnough()
    {
        var harness = new Harness("hub-b", preferred: "hub-a", failbackAfterSeconds: 60);
        harness.Lease.Held = true;
        await harness.Service.TickAsync(CancellationToken.None);
        Assert.Equal(ClusterRole.Active, harness.Membership.Role);

        // hub-a comes back.
        harness.Lease.Seen["hub-a"] = TimeSpan.FromSeconds(1);
        for (var i = 0; i < 11; i++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
            harness.Time.Advance(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(ClusterRole.Active, harness.Membership.Role);
        Assert.Equal(0, harness.Lease.Releases);

        harness.Time.Advance(TimeSpan.FromSeconds(5));
        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(ClusterRole.Standby, harness.Membership.Role);
        Assert.Equal(1, harness.Lease.Releases);
        Assert.Equal(1, harness.Connections.AbortAllCalls);
        Assert.Contains("failing back", harness.Membership.Detail);

        // It stays aside from then on.
        await harness.Service.TickAsync(CancellationToken.None);
        Assert.Equal(ClusterRole.Standby, harness.Membership.Role);
    }

    [Fact]
    public async Task APreferredThatFlapsRestartsTheClock()
    {
        var harness = new Harness("hub-b", preferred: "hub-a", failbackAfterSeconds: 60);
        harness.Lease.Held = true;
        await harness.Service.TickAsync(CancellationToken.None);

        // Alive for 50s (ticking every 5s, so the lease is renewed and the fence never trips)...
        harness.Lease.Seen["hub-a"] = TimeSpan.FromSeconds(1);
        for (var i = 0; i < 10; i++)
        {
            await harness.Service.TickAsync(CancellationToken.None);
            harness.Time.Advance(TimeSpan.FromSeconds(5));
        }

        // ...then gone for a tick, then back: the 60s start again from here.
        harness.Lease.Seen["hub-a"] = TimeSpan.FromSeconds(30);
        await harness.Service.TickAsync(CancellationToken.None);
        harness.Lease.Seen["hub-a"] = TimeSpan.FromSeconds(1);
        for (var i = 0; i < 6; i++)
        {
            harness.Time.Advance(TimeSpan.FromSeconds(5));
            await harness.Service.TickAsync(CancellationToken.None);
        }

        Assert.Equal(ClusterRole.Active, harness.Membership.Role);
        Assert.Equal(0, harness.Lease.Releases);
    }

    [Fact]
    public async Task ThePreferredInstanceAndAClusterWithoutOneContendAsBefore()
    {
        foreach (var harness in new[] { new Harness("hub-a", preferred: "hub-a"), new Harness("hub-b", preferred: null) })
        {
            harness.Lease.Held = true;
            harness.Lease.Seen["hub-a"] = TimeSpan.FromSeconds(1);

            await harness.Service.TickAsync(CancellationToken.None);

            Assert.Equal(ClusterRole.Active, harness.Membership.Role);
        }
    }

    [Fact]
    public void AFailbackShorterThanTheLeaseIsRefusedAtStartup()
    {
        var result = new ClusterOptionsValidator().Validate(null, new ClusterOptions
        {
            Enabled = true,
            ConnectionString = "Host=x",
            PreferredInstance = "hub-a",
            FailbackAfterSeconds = 5,
            LeaseTtlSeconds = 15,
            RenewIntervalSeconds = 5
        });

        Assert.Contains("Cluster:FailbackAfterSeconds", result.FailureMessage);
    }

    [Theory]
    [InlineData("POST", "/api/admin/identity/tokens", 503)]
    [InlineData("POST", "/api/admin/identity/nodes/n1/revoke", 503)]
    [InlineData("POST", "/api/admin/site/nodes", 503)]
    [InlineData("POST", "/node/identity/enroll", 503)]
    [InlineData("GET", "/api/admin/identity", 200)]
    [InlineData("GET", "/api/admin/site", 200)]
    [InlineData("POST", "/api/admin/nodes/n1/cordon", 200)]
    public async Task AStandbyRefusesWritesToTheSharedIdentityRecordAndServesItsReads(string method, string path, int expected)
    {
        var membership = new ClusterMembership("hub-b");
        var middleware = new ClusterRoleMiddleware(context =>
        {
            context.Response.StatusCode = 200;
            return Task.CompletedTask;
        }, membership);
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(expected, context.Response.StatusCode);
    }

    private sealed class Harness
    {
        public Harness(string instance, string? preferred, int failbackAfterSeconds = 60)
        {
            var options = new ClusterOptions
            {
                Enabled = true,
                InstanceId = instance,
                ConnectionString = "Host=localhost",
                LeaseTtlSeconds = 15,
                RenewIntervalSeconds = 5,
                PreferredInstance = preferred,
                FailbackAfterSeconds = failbackAfterSeconds
            };

            Membership = new ClusterMembership(instance);
            Service = new ClusterLeaseService(Lease, Membership, Options.Create(options), Connections, NullLogger<ClusterLeaseService>.Instance, Time);
        }

        public SeeingLease Lease { get; } = new();

        public ClusterMembership Membership { get; }

        public ClusterLeadershipTests.CountingConnections Connections { get; } = new();

        public ClusterLeadershipTests.FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));

        public ClusterLeaseService Service { get; }
    }

    internal sealed class SeeingLease : IClusterLease
    {
        public bool Held { get; set; }

        public Dictionary<string, TimeSpan> Seen { get; } = new(StringComparer.Ordinal);

        public int Attempts { get; private set; }

        public int Releases { get; private set; }

        public int Heartbeats { get; private set; }

        public Task<LeaseResult> TryAcquireOrRenewAsync(CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(Held
                ? new LeaseResult(true, 1, "me", DateTimeOffset.UtcNow.AddSeconds(15))
                : new LeaseResult(false, 1, "other", DateTimeOffset.UtcNow.AddSeconds(15)));
        }

        public Task ReleaseAsync(CancellationToken cancellationToken)
        {
            Releases++;
            return Task.CompletedTask;
        }

        public Task HeartbeatAsync(CancellationToken cancellationToken)
        {
            Heartbeats++;
            return Task.CompletedTask;
        }

        public Task<TimeSpan?> SinceSeenAsync(string instance, CancellationToken cancellationToken)
            => Task.FromResult(Seen.TryGetValue(instance, out var since) ? since : (TimeSpan?)null);
    }
}
