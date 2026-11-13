using System.Net;
using System.Security.Cryptography;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Fit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 135 D1 on the hub: who may reach whom. A node is a peer while it is connected, its worker runs, its tunnel listens
/// and it holds an active certificate this hub issued — under that certificate's thumbprint, never one the node reported; a
/// revoked node drops out of every list; peers share an organisation; the endpoint is the node's own, else the address its
/// connection came from.
/// </summary>
public sealed class ShardPeersTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-shard-peers-" + Guid.NewGuid().ToString("N"));
    private readonly NodeRegistry registry = new();
    private readonly NodeShardRegistry shards = new();
    private readonly NodeCertificateAuthority authority;

    public ShardPeersTests()
    {
        authority = new NodeCertificateAuthority(
            Options.Create(new NodeIdentityOptions { Enabled = true, DataDirectory = directory }),
            TimeProvider.System,
            NullLogger<NodeCertificateAuthority>.Instance);
    }

    private ShardPeerPublisher Publisher() => new(registry, shards, null!, NullLogger<ShardPeerPublisher>.Instance, authority);

    private string Enroll(string nodeId, string? organisation = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (token, _) = authority.CreateToken(nodeId, "admin:test", organisation);
        var response = authority.Enroll(new NodeEnrollRequest(token, nodeId, nodeId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        return ShardTunnelWire.Thumbprint(Convert.FromBase64String(response.Certificate));
    }

    private void Connect(string nodeId, string state = NodeShardState.Running, string? advertise = null, string? off = null, string from = "10.0.0.1")
    {
        registry.Upsert("conn-" + nodeId, new NodeRegistration(nodeId, nodeId.ToUpperInvariant(), "http://x", "3.95.0"), DateTimeOffset.UtcNow);
        shards.Report(
            new NodeShardState(nodeId, state, 8192, 0, "127.0.0.1:50052", "8.0.1", [], null, DateTimeOffset.UtcNow,
                new ShardTunnelState(50053, advertise, "reported-but-not-trusted", off)),
            IPAddress.Parse(from));
    }

    [Fact]
    public void ConnectedCertifiedRunningNodesAreEachOthersPeersUnderTheHubsThumbprint()
    {
        var a = Enroll("a");
        var b = Enroll("b");
        Connect("a", advertise: "shard-a:50053");
        Connect("b", from: "10.0.0.2");

        var lists = Publisher().Compute();

        var forA = Assert.Single(lists["conn-a"].Peers);
        Assert.Equal(("b", "10.0.0.2:50053", b), (forA.NodeId, forA.Endpoint, forA.Thumbprint));
        var forB = Assert.Single(lists["conn-b"].Peers);
        Assert.Equal(("a", "shard-a:50053", a), (forB.NodeId, forB.Endpoint, forB.Thumbprint));
    }

    [Fact]
    public void NoCertificateNoWorkerOrNoTunnelIsNobodysPeer()
    {
        Enroll("a");
        Enroll("b");
        Enroll("c");
        Connect("a");
        Connect("b", state: NodeShardState.Failed);
        Connect("c", off: "no node certificate");
        Connect("d"); // never enrolled

        var lists = Publisher().Compute();

        Assert.Empty(lists["conn-a"].Peers);
        Assert.Empty(lists["conn-b"].Peers);
        Assert.Empty(lists["conn-d"].Peers);
    }

    [Fact]
    public void ARevokedNodeDropsOutOfEveryList()
    {
        Enroll("a");
        Enroll("b");
        Enroll("c");
        Connect("a");
        Connect("b");
        Connect("c");
        Assert.Equal(2, Publisher().Compute()["conn-a"].Peers.Count);

        authority.Revoke("b", "admin:test", "lost laptop");

        var lists = Publisher().Compute();
        Assert.Equal(["c"], lists["conn-a"].Peers.Select(p => p.NodeId));
        Assert.Empty(lists["conn-b"].Peers);
    }

    [Fact]
    public void PeersShareAnOrganisation()
    {
        Enroll("a", "org-1");
        Enroll("b", "org-1");
        Enroll("c", "org-2");
        Connect("a");
        Connect("b");
        Connect("c");

        var lists = Publisher().Compute();

        Assert.Equal(["b"], lists["conn-a"].Peers.Select(p => p.NodeId));
        Assert.Empty(lists["conn-c"].Peers);
    }

    [Fact]
    public void CommunityNodesArePeersAcrossOrganisationsAndOthersStayWithinTheirOwn()
    {
        // Phase 138 D2.
        Enroll("a", "alice");
        Enroll("b", "bob");
        Enroll("c", "carol");
        Connect("a");
        Connect("b");
        Connect("c");
        shards.Report(shards.Of("a")! with { Community = true }, IPAddress.Parse("10.0.0.1"));
        shards.Report(shards.Of("b")! with { Community = true }, IPAddress.Parse("10.0.0.1"));

        var lists = Publisher().Compute();

        Assert.Equal(["b"], lists["conn-a"].Peers.Select(p => p.NodeId));
        Assert.Equal(["a"], lists["conn-b"].Peers.Select(p => p.NodeId));
        Assert.Empty(lists["conn-c"].Peers);
    }

    private static ShardRelay Relay(bool enabled = true) =>
        new(new ShardRelayOptions { Enabled = enabled, Port = 50054, Host = "hub.example" }, NullLogger.Instance);

    private void OptIntoRelay(string nodeId) =>
        shards.Report(shards.Of(nodeId)! with { Tunnel = shards.Of(nodeId)!.Tunnel! with { Relay = true } }, IPAddress.Parse("10.0.0.1"));

    [Fact]
    public void ANodeThatOptedInIsListedAsWaitingAtTheRelayAndIsToldWhereItIsAndItsOwnKey()
    {
        // Phase 139 D1/D2.
        Enroll("a");
        Enroll("b");
        Enroll("c");
        Connect("a");
        Connect("b");
        Connect("c");
        OptIntoRelay("a");
        OptIntoRelay("b");
        var relay = Relay();

        var lists = new ShardPeerPublisher(registry, shards, null!, NullLogger<ShardPeerPublisher>.Instance, authority, null, relay).Compute();

        Assert.Equal([("b", true), ("c", false)], lists["conn-a"].Peers.Select(p => (p.NodeId, p.Relay)));
        var forA = lists["conn-a"].Relay!;
        Assert.Equal(("hub.example", 50054, "a", relay.KeyFor("a")), (forA.Host, forA.Port, forA.NodeId, forA.Key));
        Assert.NotEqual(forA.Key, lists["conn-b"].Relay!.Key);
        Assert.Null(lists["conn-c"].Relay);
    }

    [Fact]
    public void WithTheRelayOffNobodyIsToldOfOneWhateverTheNodesAsked()
    {
        Enroll("a");
        Enroll("b");
        Connect("a");
        Connect("b");
        OptIntoRelay("a");
        OptIntoRelay("b");

        foreach (var lists in new[]
                 {
                     Publisher().Compute(),
                     new ShardPeerPublisher(registry, shards, null!, NullLogger<ShardPeerPublisher>.Instance, authority, null, Relay(enabled: false)).Compute()
                 })
        {
            Assert.All(lists.Values, list => Assert.Null(list.Relay));
            Assert.All(lists.Values.SelectMany(list => list.Peers), peer => Assert.False(peer.Relay));
        }
    }

    [Fact]
    public async Task TheRelayMayJoinOnlyNodesTheLastListsMadePeersAndARevokedNodeLosesItsKey()
    {
        // Phase 139 D3.
        Enroll("a", "org-1");
        Enroll("b", "org-1");
        Enroll("c", "org-2");
        Connect("a");
        Connect("b");
        Connect("c");
        OptIntoRelay("a");
        OptIntoRelay("b");
        OptIntoRelay("c");
        var relay = Relay();
        var publisher = new ShardPeerPublisher(registry, shards, null!, NullLogger<ShardPeerPublisher>.Instance, authority, null, relay);

        await publisher.PublishAsync();

        Assert.True(relay.Authorize!("a", "b"));
        Assert.False(relay.Authorize!("a", "c"));
        Assert.False(relay.Authorize!("c", "a"));
        var before = relay.KeyFor("b");

        authority.Revoke("b", "admin:test", "lost laptop");
        await publisher.PublishAsync();

        Assert.False(relay.Authorize!("a", "b"));
        Assert.NotEqual(before, relay.KeyFor("b"));
    }

    [Theory]
    [InlineData("::ffff:10.1.2.3", "10.1.2.3:50053")]
    [InlineData("fd00::7", "[fd00::7]:50053")]
    public void WithoutAnAdvertisedAddressTheConnectionsOwnIsUsed(string from, string endpoint) =>
        Assert.Equal(endpoint, ShardPeerPublisher.Endpoint(new ShardTunnelState(50053, null, null, null), IPAddress.Parse(from)));

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
