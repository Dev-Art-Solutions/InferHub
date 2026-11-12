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

        Assert.Equal("shard-b runs llama.cpp RPC v5.0.0, the head v8.0.1", Assert.Single(Compute().Status).Why);
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

    [Fact]
    public void AStoppedGroupLeavesTheCatalogueEvenWhenTheHeadsOwnEngineIsDownAndItsModelsAreHeld()
    {
        // Found on the published 3.96.0 image: the head's Ollama is down, so its empty report is held (69) — and the group it
        // had just stopped stayed in /v1/models. The engine's models are held; a group is not.
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("conn-a", new NodeRegistration("a", "shard-a", "http://x", "3.96.0"), now);
        registry.ReportModels("conn-a", new NodeModels("a",
            [new ModelInfo("qwen2.5:0.5b", "sha256:abc", 1), new ModelInfo("llama-8b-split", ShardGroupState.Digest, null)], now), now);
        registry.Touch("conn-a", new Heartbeat("a", now, InFlight: 0, Backend: BackendHealth.Unreachable), now);

        registry.ReportModels("conn-a", new NodeModels("a", [], now.AddSeconds(5)), now.AddSeconds(5));

        Assert.Equal(["qwen2.5:0.5b"], registry.FindNodesWithModel("qwen2.5:0.5b", includeUnserviceable: true).Select(_ => "qwen2.5:0.5b"));
        Assert.Empty(registry.FindNodesWithModel("llama-8b-split", includeUnserviceable: true));
    }

    [Fact]
    public void ASpareStandsInForAMemberThatLeftAndTheStatusNamesIt()
    {
        options.Groups["llama-8b-split"].Spares = ["shard-d"];
        Node("a");
        Node("b");
        Node("d", 4096);  // shard-c never came

        var (lists, status) = Compute();

        var spec = Assert.Single(lists["conn-a"].Groups);
        Assert.Equal([("b", 3072L), ("d", 4096L)], spec.Members.Select(m => (m.NodeId, m.Weight)));
        var group = Assert.Single(status);
        Assert.True(group.Ready);
        Assert.Equal(["shard-b", "shard-d"], group.Members);
        Assert.Contains("shard-d stands in for shard-c (member 'shard-c' is not connected)", Assert.Single(group.Substitutions));
    }

    [Fact]
    public void AMemberPausedByItsOwnerOrCordonedIsNotReadyAndASpareTakesItsPlace()
    {
        options.Groups["llama-8b-split"].Spares = ["shard-d"];
        Node("a");
        Node("b");
        Node("c");
        Node("d");
        registry.RecordSharing("conn-b", new NodeSharingState("b", "paused", true, "outside sharing hours", DateTimeOffset.UtcNow, 0, 0));

        var group = Assert.Single(Compute().Status);
        Assert.True(group.Ready);
        Assert.Contains("shard-d stands in for shard-b (shard-b is paused by its owner (outside sharing hours))", Assert.Single(group.Substitutions));

        // shard-c cordoned too, and the only spare is taken: not ready, with both reasons' first.
        registry.Cordon("c");
        var stuck = Assert.Single(Compute().Status);
        Assert.False(stuck.Ready);
        Assert.Equal("shard-c is cordoned, and no spare can stand in", stuck.Why);
    }

    [Fact]
    public void AConfiguredGroupThatAnswersNobodyIsAReasonNotAMissingModel()
    {
        Node("a");
        Node("b");
        var publisher = Publisher();

        Assert.Equal("group 'llama-8b-split' is not running: member 'shard-c' is not connected", publisher.WhyGroupIsNotServing("llama-8b-split"));
        Assert.Null(publisher.WhyGroupIsNotServing("qwen2.5:0.5b"));

        // Ready and sent, the head not answering yet: starting.
        Node("c");
        Assert.StartsWith("group 'llama-8b-split' is starting on shard-a", publisher.WhyGroupIsNotServing("llama-8b-split"));

        // The head reports it running: nothing to explain — the router finds it.
        shards.Report(shards.Of("a")! with { Groups = [new ShardGroupState("llama-8b-split", "llama31-8b.gguf", ShardGroupState.Running, ["shard-b", "shard-c"], "127.0.0.1:1", null)] }, IPAddress.Loopback);
        Assert.Null(publisher.WhyGroupIsNotServing("llama-8b-split"));
    }

    private void Community(string id, long ramMiB = 3072, string? organisation = null, bool sealedNode = false)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (token, _) = authority.CreateToken(id, "admin:test", organisation);
        authority.Enroll(new NodeEnrollRequest(token, id, id, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        registry.Upsert("conn-" + id, new NodeRegistration(id, "shard-" + id, "http://x", "3.98.0", Sealed: sealedNode ? true : null), DateTimeOffset.UtcNow);
        shards.Report(new NodeShardState(id, NodeShardState.Running, ramMiB, 0, "127.0.0.1:50052", "8.0.1", [], null, DateTimeOffset.UtcNow,
            new ShardTunnelState(50053, $"shard-{id}:50053", null, null), Community: true), IPAddress.Loopback);
    }

    [Fact]
    public void ACommunityGroupPicksTheLargestCommunityOffersAcrossOrganisationsAndNeverAPrivateOrSealedNode()
    {
        options.Groups.Clear();
        options.Groups["llama-8b-community"] = new ShardGroupDefinition { Mode = "community", Head = "shard-a", Workers = 2, Model = "llama31-8b.gguf" };
        Community("a", organisation: "alice");
        Community("b", 4096, organisation: "bob");
        Community("c", 2048, organisation: "carol");
        Community("d", 8192, organisation: "dave", sealedNode: true);   // sealed: never
        Node("p", 16384, organisation: "alice");                        // private, the largest offer: never

        var (lists, status) = Compute();

        var spec = Assert.Single(lists["conn-a"].Groups);
        Assert.True(spec.Community);
        Assert.Equal(["b", "c"], spec.Members.Select(m => m.NodeId));
        Assert.True(Assert.Single(status).Ready);
        Assert.True(Publisher().IsCommunityGroup("llama-8b-community"));
        Assert.False(Publisher().IsCommunityGroup("qwen2.5:0.5b"));
    }

    [Fact]
    public void ACommunityGroupNeedsACommunityHeadAndEnoughCommunityNodes()
    {
        options.Groups.Clear();
        options.Groups["llama-8b-community"] = new ShardGroupDefinition { Mode = "community", Head = "shard-a", Workers = 3, Model = "llama31-8b.gguf" };
        Node("a");
        Community("b");
        Assert.Contains("shard-a has not joined community groups", Assert.Single(Compute().Status).Why);

        Community("a");
        Community("c");
        Assert.Equal("only 2 community node(s) can take part now; the group wants 3", Assert.Single(Compute().Status).Why);
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
