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
/// Phase 136 D1 on the hub: a group the admin defined is sent to its head only when every member is ready — connected, worker
/// running, tunnel open, one of the head's peers, one RPC protocol — and each worker's share is its offer. Otherwise nothing is
/// sent, and the reason is the first thing missing.
/// </summary>
public sealed class ShardGroupsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-shard-groups-" + Guid.NewGuid().ToString("N"));
    private readonly NodeRegistry registry = new();
    private readonly NodeShardRegistry shards = new();
    private readonly NodeCertificateAuthority authority;
    private readonly ShardGroupsOptions options = new();

    public ShardGroupsTests()
    {
        authority = new NodeCertificateAuthority(
            Options.Create(new NodeIdentityOptions { Enabled = true, DataDirectory = directory }),
            TimeProvider.System,
            NullLogger<NodeCertificateAuthority>.Instance);
        options.Groups["llama-8b-split"] = new ShardGroupDefinition { Head = "shard-a", Members = ["shard-b", "shard-c"], Model = "llama31-8b.gguf", Context = 4096 };
    }

    private ShardPeerPublisher Publisher() =>
        new(registry, shards, null!, NullLogger<ShardPeerPublisher>.Instance, authority, Options.Create(options));

    private (IReadOnlyDictionary<string, ShardGroupList> Lists, IReadOnlyList<ShardGroupStatus> Status) Compute()
    {
        var publisher = Publisher();
        return publisher.ComputeGroups(publisher.Compute());
    }

    private void Node(string id, long ramMiB = 3072, string protocol = "8.0.1", string? organisation = null, bool enroll = true, string state = NodeShardState.Running)
    {
        if (enroll)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var (token, _) = authority.CreateToken(id, "admin:test", organisation);
            authority.Enroll(new NodeEnrollRequest(token, id, id, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        }

        registry.Upsert("conn-" + id, new NodeRegistration(id, "shard-" + id, "http://x", "3.96.0"), DateTimeOffset.UtcNow);
        shards.Report(new NodeShardState(id, state, ramMiB, 0, "127.0.0.1:50052", protocol, [], null, DateTimeOffset.UtcNow,
            new ShardTunnelState(50053, $"shard-{id}:50053", null, null)), IPAddress.Loopback);
    }

    [Fact]
    public void AReadyGroupIsSentToItsHeadWithEachWorkersShareByItsOffer()
    {
        Node("a", 3072);
        Node("b", 3072);
        Node("c", 6144);

        var (lists, status) = Compute();

        var spec = Assert.Single(lists["conn-a"].Groups);
        Assert.Equal(("llama-8b-split", "llama31-8b.gguf", 4096, 3072L), (spec.Name, spec.Model, spec.Context, spec.HeadWeight));
        Assert.Equal([("b", 3072L), ("c", 6144L)], spec.Members.Select(m => (m.NodeId, m.Weight)));
        Assert.Empty(lists["conn-b"].Groups);
        Assert.True(Assert.Single(status).Ready);
    }

    [Fact]
    public void AMemberThatIsNotConnectedHoldsTheGroupBack()
    {
        Node("a");
        Node("b");

        var (lists, status) = Compute();

        Assert.Empty(lists["conn-a"].Groups);
        Assert.Contains("member 'shard-c' is not connected", Assert.Single(status).Why);
    }

    [Fact]
    public void AMemberWhoseWorkerIsNotRunningOrThatIsNotAPeerHoldsTheGroupBack()
    {
        Node("a");
        Node("b", state: NodeShardState.Failed);
        Node("c");
        Assert.Contains("shard-b's worker is not running", Assert.Single(Compute().Status).Why);

        // In another organisation, so not one of the head's peers (135).
        Node("b", organisation: "other");
        Node("a", organisation: "mine", enroll: true);
        Assert.Contains("is not one of shard-a's peers", Assert.Single(Compute().Status).Why);
    }

    [Fact]
    public void MembersOnDifferentRpcProtocolsAreNotAGroup()
    {
        Node("a");
        Node("b", protocol: "5.0.0");
        Node("c");

        Assert.Contains("protocols differ", Assert.Single(Compute().Status).Why);
    }

    [Fact]
    public void NoGroupsConfiguredSendsNothingAndAGroupWithoutAModelSaysSo()
    {
        Node("a");
        Node("b");
        Node("c");
        options.Groups["llama-8b-split"].Model = null;
        Assert.Contains("no Model", Assert.Single(Compute().Status).Why);

        options.Groups.Clear();
        var (lists, status) = Compute();
        Assert.Empty(status);
        Assert.All(lists.Values, l => Assert.Empty(l.Groups));
    }

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
