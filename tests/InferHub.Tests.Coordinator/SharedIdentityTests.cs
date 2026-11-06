using System.Security.Cryptography;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Cluster;
using InferHub.Shared.Contracts;
using InferHub.Tests.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 131 against a real Postgres (<c>INFERHUB_TEST_POSTGRES</c>): two coordinators of one cluster share the node CA and
/// its record, so what one issued the other verifies; concurrent writes do not lose each other's changes.
/// </summary>
public sealed class SharedIdentityTests
{
    private readonly string schema = "idt_" + Guid.NewGuid().ToString("N")[..12];

    [PostgresFact]
    public void WhatTheActiveIssuedTheNextActiveVerifiesAndARevocationFollowsTheLease()
    {
        var membershipA = new ClusterMembership("hub-a");
        var membershipB = new ClusterMembership("hub-b");
        var hubA = Authority(out _, membershipA);
        var hubB = Authority(out _, membershipB);
        membershipA.MarkActive(1, DateTimeOffset.UtcNow);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (token, _) = hubA.CreateToken("gpu-1", "admin:x", "lab-b");
        var issued = hubA.Enroll(new NodeEnrollRequest(token, "node-1", "gpu-1", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        var der = Convert.FromBase64String(issued.Certificate);
        Dictionary<string, string> Proof() => NodeProof.Create(der, key, "node-1", DateTimeOffset.UtcNow).ToDictionary(h => h.Key, h => h.Value);

        // The standby reads the shared record, so it already knows the node.
        Assert.Equal(hubA.AuthorityFingerprint(), hubB.AuthorityFingerprint());
        var first = Proof();
        Assert.True(hubB.Verify(name => first.GetValueOrDefault(name))!.Valid);
        Assert.Equal((true, "lab-b"), hubB.OrganisationOf("node-1"));

        // Failover: B leads, A stands by. B revokes; A, reading afresh as a standby, sees it at once.
        membershipA.MarkStandby("lost the lease");
        membershipB.MarkActive(2, DateTimeOffset.UtcNow);
        hubB.Revoke("node-1", "admin:y", "moved");
        var second = Proof();
        Assert.Contains("moved", hubA.Verify(name => second.GetValueOrDefault(name))!.Reason);

        // Failback: A leads again with a new fence and reads afresh before trusting its copy.
        membershipB.MarkStandby("failing back");
        membershipA.MarkActive(3, DateTimeOffset.UtcNow);
        var third = Proof();
        Assert.Contains("moved", hubA.Verify(name => third.GetValueOrDefault(name))!.Reason);
    }

    [PostgresFact]
    public async Task TwoCoordinatorsBootingAtOnceMakeOneAuthority()
    {
        var fingerprints = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => Authority(out _).AuthorityFingerprint())));

        Assert.Single(fingerprints.Distinct());
    }

    [PostgresFact]
    public async Task ConcurrentWritesFromTwoCoordinatorsAreAllKept()
    {
        var hubA = Authority(out _);
        var hubB = Authority(out _);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() => (i % 2 == 0 ? hubA : hubB).CreateToken($"t{i}", "admin:x"))));

        Assert.Equal(10, Authority(out _).Snapshot()!.PendingTokens);
    }

    [PostgresFact]
    public void AHubThatJoinsAClusterBringsTheNodesItCertifiedAndOnlyTheFirstOneDoes()
    {
        var directoryA = Path.Combine(Path.GetTempPath(), "idt-" + Guid.NewGuid().ToString("N"));
        var directoryB = Path.Combine(Path.GetTempPath(), "idt-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Before 3.91: hub-a certified a node in its own files; hub-b made a CA of its own.
            var alone = new NodeCertificateAuthority(Options.Create(new NodeIdentityOptions { Enabled = true, DataDirectory = directoryA }), TimeProvider.System, NullLogger<NodeCertificateAuthority>.Instance);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var (token, _) = alone.CreateToken("gpu-1", "admin:x");
            var der = Convert.FromBase64String(alone.Enroll(new NodeEnrollRequest(token, "node-1", "gpu-1", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()))).Certificate);
            var other = new NodeCertificateAuthority(Options.Create(new NodeIdentityOptions { Enabled = true, DataDirectory = directoryB }), TimeProvider.System, NullLogger<NodeCertificateAuthority>.Instance);
            Assert.NotEqual(alone.AuthorityFingerprint(), other.AuthorityFingerprint());

            var hubA = Seeded(directoryA);
            var hubB = Seeded(directoryB);

            Assert.Equal(alone.AuthorityFingerprint(), hubA.AuthorityFingerprint());
            Assert.Equal(alone.AuthorityFingerprint(), hubB.AuthorityFingerprint());
            var proof = NodeProof.Create(der, key, "node-1", DateTimeOffset.UtcNow).ToDictionary(h => h.Key, h => h.Value);
            Assert.True(hubB.Verify(name => proof.GetValueOrDefault(name))!.Valid);
            Assert.True(File.Exists(Path.Combine(directoryB, "ca.key")), "the loser's own files stay where they were");
        }
        finally
        {
            foreach (var directory in new[] { directoryA, directoryB })
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        NodeCertificateAuthority Seeded(string directory) => new(
            Options.Create(new NodeIdentityOptions { Enabled = true }),
            TimeProvider.System,
            NullLogger<NodeCertificateAuthority>.Instance,
            new PostgresNodeIdentityStore(
                Options.Create(new ClusterOptions { Enabled = true, ConnectionString = PostgresTestGate.ConnectionString!, Schema = schema }),
                new FileNodeIdentityStore(directory)));
    }

    private NodeCertificateAuthority Authority(out PostgresNodeIdentityStore store, IClusterMembership? membership = null)
    {
        var cluster = Options.Create(new ClusterOptions { Enabled = true, ConnectionString = PostgresTestGate.ConnectionString!, Schema = schema });
        store = new PostgresNodeIdentityStore(cluster);
        return new NodeCertificateAuthority(
            Options.Create(new NodeIdentityOptions { Enabled = true }),
            TimeProvider.System,
            NullLogger<NodeCertificateAuthority>.Instance,
            store,
            membership);
    }
}
