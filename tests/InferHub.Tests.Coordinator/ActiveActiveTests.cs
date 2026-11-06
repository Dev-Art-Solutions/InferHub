using InferHub.Coordinator.Cluster;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 132: active-active members — every one serves, holds no lease, advertises what its nodes serve, and stops serving
/// when it cannot prove its own heartbeat.
/// </summary>
public class ActiveActiveTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AMemberServesWithoutTheLeaseAndAdvertisesWhatItsNodesServe()
    {
        var harness = new Harness("hub-a");
        harness.Registry.Upsert("c1", new NodeRegistration("n1", "node-1", "http://localhost:11434/", "3.92.0", Organisation: "lab-b"), Start);
        harness.Registry.ReportModels("c1", new NodeModels("n1", [new ModelInfo("qwen2.5:0.5b", "d", 1)], Start), Start);
        harness.Lease.Rows.Add(new ClusterMemberRow("hub-b", "http://hub-b:8080", TimeSpan.FromSeconds(2), """[{"model":{"name":"smollm:135m","digest":"e","size":2},"organisation":null}]"""));

        await harness.Service.TickAsync(CancellationToken.None);

        Assert.Equal(ClusterRole.Active, harness.Membership.Role);
        Assert.Equal(0, harness.Lease.Acquires);
        Assert.Equal("http://hub-a:8080/", harness.Lease.AdvertisedUrl);
        Assert.Contains("qwen2.5:0.5b", harness.Lease.AdvertisedModels);
        Assert.Contains("lab-b", harness.Lease.AdvertisedModels);
        Assert.Equal("serving alongside hub-b (active-active)", harness.Membership.Detail);
        var peer = Assert.Single(harness.Peers.Live);
        Assert.Equal(new Uri("http://hub-b:8080/"), peer.Url);
        Assert.Equal("smollm:135m", Assert.Single(peer.Models).Model.Name);
    }

    [Fact]
    public async Task AMemberThatCannotProveItsHeartbeatStopsServingAndLetsItsNodesGo()
    {
        var harness = new Harness("hub-a");
        await harness.Service.TickAsync(CancellationToken.None);
        Assert.Equal(ClusterRole.Active, harness.Membership.Role);

        harness.Lease.Fail = true;

        for (var i = 0; i < 3; i++)
        {
            harness.Time.Advance(TimeSpan.FromSeconds(5));
            await harness.Service.TickAsync(CancellationToken.None);
        }

        Assert.Equal(ClusterRole.Standby, harness.Membership.Role);
        Assert.Equal(1, harness.Connections.AbortAllCalls);
        Assert.Empty(harness.Peers.Live);
    }

    [Fact]
    public void ThePeerDirectoryKeepsOnlyOtherLiveMembersWithAUrl()
    {
        var peers = new ClusterPeers();

        peers.Replace(
        [
            new ClusterMemberRow("hub-a", "http://hub-a:8080/", TimeSpan.Zero, null),
            new ClusterMemberRow("hub-b", "http://hub-b:8080/base", TimeSpan.FromSeconds(3), "not json"),
            new ClusterMemberRow("hub-c", "http://hub-c:8080/", TimeSpan.FromSeconds(16), "[]"),
            new ClusterMemberRow("hub-d", null, TimeSpan.Zero, "[]"),
            new ClusterMemberRow("hub-e", "/", TimeSpan.Zero, "[]"),
            new ClusterMemberRow("hub-f", "file:///srv/hub", TimeSpan.Zero, "[]")
        ], "hub-a", TimeSpan.FromSeconds(15));

        var peer = Assert.Single(peers.Live);
        Assert.Equal("hub-b", peer.Instance);
        Assert.Equal("http://hub-b:8080/base/", peer.Url.ToString());
        Assert.Empty(peer.Models);
    }

    [Fact]
    public async Task APeerModelOfAnotherOrganisationIsNoReasonToPassARequestOnOrToListIt()
    {
        var peers = new ClusterPeers();
        peers.Replace(
        [
            new ClusterMemberRow("hub-b", "http://hub-b:8080/", TimeSpan.Zero,
                ClusterPeers.Describe([new PeerModel(new ModelInfo("granted", null, null), "lab-b"), new PeerModel(new ModelInfo("private", null, null), "lab-b")]))
        ], "hub-a", TimeSpan.FromSeconds(15));
        var policy = new OrganisationPolicy(Options.Create(new OrganisationOptions
        {
            Enabled = true,
            Grants = [new OrganisationGrant { Owner = "lab-b", To = "lab-a", Models = ["granted"] }]
        }));

        await Task.Run(() =>
        {
            OrganisationScope.Begin(policy, "lab-a");
            Assert.NotNull(peers.FindFor("granted"));
            Assert.Null(peers.FindFor("private"));
            Assert.Equal(["granted"], peers.ModelsForCaller().Select(model => model.Name));
        });

        Assert.NotNull(peers.FindFor("private"));
    }

    [Fact]
    public void TheListingsShowWhatOtherMembersServeAndThisMembersNodesWinAName()
    {
        var registry = new NodeRegistry();
        registry.Upsert("c1", new NodeRegistration("n1", "node-1", "http://localhost:11434/", "3.92.0"), Start);
        registry.ReportModels("c1", new NodeModels("n1", [new ModelInfo("shared", "local-digest", 1)], Start), Start);
        var peers = new ClusterPeers();
        peers.Replace(
        [
            new ClusterMemberRow("hub-b", "http://hub-b:8080/", TimeSpan.Zero,
                ClusterPeers.Describe([new PeerModel(new ModelInfo("shared", "peer-digest", 2), null), new PeerModel(new ModelInfo("elsewhere", "e", 3), null)]))
        ], "hub-a", TimeSpan.FromSeconds(15));

        var listed = ModelDiscovery.Merge(registry, null, peers);

        Assert.Equal(["elsewhere", "shared"], listed.Select(model => model.Name));
        Assert.Equal("local-digest", listed.Single(model => model.Name == "shared").Digest);
        Assert.Equal(["shared"], ModelDiscovery.Merge(registry, null, new ClusterPeers()).Select(model => model.Name));
    }

    [Fact]
    public void ANodeThatCannotTakeWorkIsNotAdvertised()
    {
        var registry = new NodeRegistry();

        foreach (var (connection, model) in new[] { ("c1", "open"), ("c2", "cordoned") })
        {
            registry.Upsert(connection, new NodeRegistration(connection, connection, "http://localhost:11434/", "3.92.0"), Start);
            registry.ReportModels(connection, new NodeModels(connection, [new ModelInfo(model, null, null)], Start), Start);
        }

        registry.Cordon("c2");

        Assert.Equal(["open"], registry.AdvertisedModels().Select(held => held.Model.Name));
    }

    [Theory]
    [InlineData("active-active", null, null, "Cluster:AdvertiseUrl")]
    [InlineData("active-active", "hub-a:8080", null, "Cluster:AdvertiseUrl")]
    [InlineData("active-active", "http://hub-a:8080/", "hub-a", "Cluster:PreferredInstance")]
    [InlineData("both", "http://hub-a:8080/", null, "Cluster:Mode")]
    public void AnActiveActiveMemberIsRefusedAtStartupWithoutWhatItNeeds(string mode, string? advertise, string? preferred, string expected)
    {
        var result = new ClusterOptionsValidator().Validate(null, new ClusterOptions
        {
            Enabled = true,
            ConnectionString = "Host=x",
            Mode = mode,
            AdvertiseUrl = advertise,
            PreferredInstance = preferred
        });

        Assert.Contains(expected, result.FailureMessage);
    }

    [Fact]
    public void ActiveActiveIsVisibleOnStatusAndMetricsAndAStandbyPairsOutputDoesNotChange()
    {
        var standby = new ClusterMembership("hub-a");
        standby.MarkActive(1, Start);
        var block = StatusEndpoint.BuildClusterBlock(standby, new ClusterPeers());
        Assert.Null(block!.Mode);
        Assert.Null(block.Peers);

        var member = new ClusterMembership("hub-a", activeActive: true);
        member.MarkActive(0, Start, "serving alongside hub-b (active-active)");
        var peers = new ClusterPeers();
        peers.Replace([new ClusterMemberRow("hub-b", "http://hub-b:8080/", TimeSpan.FromSeconds(1), "[]")], "hub-a", TimeSpan.FromSeconds(15));
        peers.Count("hub-b", "served");

        var aa = StatusEndpoint.BuildClusterBlock(member, peers)!;
        Assert.Equal("active-active", aa.Mode);
        Assert.Equal(1, Assert.Single(aa.Peers!).Forwarded["served"]);

        var text = PrometheusFormatter.Format(Scrape(new ClusterScrapeSample("hub-a", true, 0) { Peers = 1, Forwarded = [("hub-b", "served", 1)] }));
        Assert.Contains("inferhub_cluster_peers{instance=\"hub-a\"} 1", text);
        Assert.Contains("inferhub_cluster_forwarded_total{instance=\"hub-a\",peer=\"hub-b\",outcome=\"served\"} 1", text);
        Assert.DoesNotContain("inferhub_cluster_peers", PrometheusFormatter.Format(Scrape(new ClusterScrapeSample("hub-a", true, 1))));
    }

    [Fact]
    public void ANodeRevokedOnAnotherMemberIsDroppedByTheMemberItIsConnectedTo()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aa-" + Guid.NewGuid().ToString("N"));

        try
        {
            var authority = new InferHub.Coordinator.Auth.NodeCertificateAuthority(
                Options.Create(new InferHub.Coordinator.Auth.NodeIdentityOptions { Enabled = true, DataDirectory = directory }),
                TimeProvider.System,
                NullLogger<InferHub.Coordinator.Auth.NodeCertificateAuthority>.Instance);
            using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var (token, _) = authority.CreateToken("gpu-1", "admin:x");
            authority.Enroll(new NodeEnrollRequest(token, "certified", "gpu-1", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));

            var registry = new NodeRegistry();
            registry.Upsert("c-certified", new NodeRegistration("certified", "gpu-1", "http://localhost:11434/", "3.92.0"), Start);
            registry.Upsert("c-plain", new NodeRegistration("plain", "laptop", "http://localhost:11434/", "3.92.0"), Start);
            var connections = new RecordingConnections();
            var sweep = new ClusterRevocationSweep(authority, registry, connections,
                Options.Create(new ClusterOptions()), NullLogger<ClusterRevocationSweep>.Instance, TimeProvider.System);

            Assert.Equal(0, sweep.Sweep());

            authority.Revoke("certified", "admin:y", "moved");

            Assert.Equal(1, sweep.Sweep());
            Assert.Equal(["c-certified"], connections.Aborted);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class RecordingConnections : INodeConnectionTracker
    {
        public List<string> Aborted { get; } = [];

        public void Track(string connectionId, Microsoft.AspNetCore.SignalR.HubCallerContext context) { }

        public void Forget(string connectionId) { }

        public bool Abort(string connectionId)
        {
            Aborted.Add(connectionId);
            return true;
        }

        public int AbortAll() => 0;
    }

    private static PrometheusScrape Scrape(ClusterScrapeSample cluster) => new(
        "test", new Metrics().Snapshot(DateTimeOffset.UtcNow), [], [], new QueueSnapshot(0, 0, 0, 0, 0, null), [], 0, cluster);

    private sealed class Harness
    {
        public Harness(string instance)
        {
            var options = new ClusterOptions
            {
                Enabled = true,
                InstanceId = instance,
                ConnectionString = "Host=localhost",
                LeaseTtlSeconds = 15,
                RenewIntervalSeconds = 5,
                Mode = ClusterOptions.ActiveActiveMode,
                AdvertiseUrl = $"http://{instance}:8080/"
            };

            Membership = new ClusterMembership(instance, activeActive: true);
            Service = new ClusterLeaseService(Lease, Membership, Options.Create(options), Connections, NullLogger<ClusterLeaseService>.Instance, Time, Registry, Peers);
        }

        public AdvertisingLease Lease { get; } = new();

        public ClusterMembership Membership { get; }

        public NodeRegistry Registry { get; } = new();

        public ClusterPeers Peers { get; } = new();

        public ClusterLeadershipTests.CountingConnections Connections { get; } = new();

        public ClusterLeadershipTests.FakeTimeProvider Time { get; } = new(Start);

        public ClusterLeaseService Service { get; }
    }

    internal sealed class AdvertisingLease : IClusterLease
    {
        public List<ClusterMemberRow> Rows { get; } = [];

        public bool Fail { get; set; }

        public int Acquires { get; private set; }

        public string? AdvertisedUrl { get; private set; }

        public string AdvertisedModels { get; private set; } = "";

        public Task<LeaseResult> TryAcquireOrRenewAsync(CancellationToken cancellationToken)
        {
            Acquires++;
            return Task.FromResult(new LeaseResult(true, 1, "me", DateTimeOffset.UtcNow.AddSeconds(15)));
        }

        public Task ReleaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AdvertiseAsync(string url, string models, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("the database is unreachable");
            }

            AdvertisedUrl = url;
            AdvertisedModels = models;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ClusterMemberRow>> MembersAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ClusterMemberRow>>(Rows.Append(new ClusterMemberRow("hub-a", AdvertisedUrl, TimeSpan.Zero, AdvertisedModels)).ToArray());
    }
}
