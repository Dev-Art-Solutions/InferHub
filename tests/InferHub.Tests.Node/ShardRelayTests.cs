using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using InferHub.Coordinator.Services;
using InferHub.Node.Backends.Shards;
using InferHub.Node.Configuration;
using InferHub.Shared.Fit;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 139, the hub's real relay and two real tunnels on loopback. A pair with no direct path (each is told an address of
/// the other that nothing answers on, as behind NAT) meets at the relay and a head's bytes reach the peer's worker — still
/// under 135's mTLS, so a wrong thumbprint fails through the relay as it does directly (D1). A direct path is used when
/// there is one. The relay knows a node only by the key the hub gave it (D2), joins only peers the hub listed, and cuts a
/// node the lists dropped (D3).
/// </summary>
public sealed class ShardRelayTests
{
    private sealed class Node : IAsyncDisposable
    {
        private readonly byte[] pfx;
        private readonly TcpListener worker = new(IPAddress.Loopback, 0);

        public Node(string name, bool relay = true)
        {
            Name = name;
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            pfx = cert.Export(X509ContentType.Pkcs12);
            Thumbprint = ShardTunnelWire.Thumbprint(cert.RawData);
            worker.Start();
            _ = Task.Run(EchoAsync);
            Options = new ShardOptions { Enabled = true, RamGiB = 1, Port = ((IPEndPoint)worker.LocalEndpoint).Port, TunnelPort = FreePort(), TunnelRelay = relay };
            Tunnel = new ShardTunnel(Options, () => X509CertificateLoader.LoadPkcs12(pfx, null), () => Thumbprint,
                NullLogger.Instance, null, TimeSpan.FromMilliseconds(300), () => "127.0.0.1");
        }

        public string Name { get; }
        public string Thumbprint { get; }
        public ShardOptions Options { get; }
        public ShardTunnel Tunnel { get; }

        /// <summary>This node as a peer: at its real tunnel port, or — <paramref name="reachable"/> false — at one nothing listens on.</summary>
        public ShardPeer AsPeer(bool reachable, bool relay = true, string? thumbprint = null) =>
            new(Name, Name.ToUpperInvariant(), $"127.0.0.1:{(reachable ? Options.TunnelPort : FreePort())}", thumbprint ?? Thumbprint, relay);

        public ShardRelayInfo At(ShardRelay relay, string? key = null) => new(null, relay.Port, Name, key ?? relay.KeyFor(Name));

        private async Task EchoAsync()
        {
            while (true)
            {
                TcpClient client;

                try
                {
                    client = await worker.AcceptTcpClientAsync();
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using var _ = client;

                    try
                    {
                        await client.GetStream().CopyToAsync(client.GetStream());
                    }
                    catch (Exception)
                    {
                    }
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Tunnel.DisposeAsync();
            worker.Stop();
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<ShardRelay> RelayAsync(Func<string, string, bool>? authorize = null)
    {
        var relay = new ShardRelay(new ShardRelayOptions { Enabled = true, Port = 0, WaitSeconds = 5 }, NullLogger.Instance)
        {
            Authorize = authorize ?? ((_, _) => true)
        };
        await relay.StartAsync(CancellationToken.None);
        return relay;
    }

    private static async Task<(Node A, Node B)> NodesAsync(bool relayB = true)
    {
        var a = new Node("a");
        var b = new Node("b", relayB);
        await a.Tunnel.StartAsync(CancellationToken.None);
        await b.Tunnel.StartAsync(CancellationToken.None);
        return (a, b);
    }

    private static async Task<string?> RoundTripAsync(Stream stream, string text)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            await stream.WriteAsync(bytes, deadline.Token);
            var back = new byte[bytes.Length];
            await stream.ReadExactlyAsync(back, deadline.Token);
            return System.Text.Encoding.ASCII.GetString(back);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException)
        {
            return null;
        }
    }

    private static int LocalPort(ShardTunnel tunnel, string peer) =>
        ShardTunnel.Split(tunnel.Peers().Single(p => p.NodeId == peer).Local!).Port;

    [Fact]
    public async Task APairWithNoDirectPathMeetsAtTheRelayAndTheWorkerAnswers()
    {
        await using var relay = await RelayAsync();
        var (a, b) = await NodesAsync();
        await using var _ = a;
        await using var __ = b;

        a.Tunnel.SetPeers(new ShardPeerList([b.AsPeer(reachable: false)], a.At(relay)));
        b.Tunnel.SetPeers(new ShardPeerList([a.AsPeer(reachable: false)], b.At(relay)));

        using var head = new TcpClient();
        await head.ConnectAsync(IPAddress.Loopback, LocalPort(a.Tunnel, "b"));
        Assert.Equal("tensor bytes", await RoundTripAsync(head.GetStream(), "tensor bytes"));

        var (ms, via) = await a.Tunnel.MeasureViaAsync(b.AsPeer(reachable: false), CancellationToken.None);
        Assert.Equal(ShardPeerState.Relayed, via);
        Assert.InRange(ms, 0, 2000);

        var status = relay.Status();
        Assert.True(status.PairsTotal >= 2);
        Assert.True(status.BytesRelayed > 0);
        Assert.True(a.Tunnel.State()!.Relay);
        Assert.Null(a.Tunnel.State()!.RelayError);
    }

    [Fact]
    public async Task ADirectPathIsUsedWhenThereIsOne()
    {
        await using var relay = await RelayAsync();
        var (a, b) = await NodesAsync();
        await using var _ = a;
        await using var __ = b;

        a.Tunnel.SetPeers(new ShardPeerList([b.AsPeer(reachable: true)], a.At(relay)));
        b.Tunnel.SetPeers(new ShardPeerList([a.AsPeer(reachable: true)], b.At(relay)));

        var (_, via) = await a.Tunnel.MeasureViaAsync(b.AsPeer(reachable: true), CancellationToken.None);

        Assert.Equal(ShardPeerState.Direct, via);
        Assert.Equal(0, relay.Status().PairsTotal);
    }

    [Fact]
    public async Task ThePeersCertificateIsCheckedThroughTheRelayAsOnADirectPath()
    {
        await using var relay = await RelayAsync();
        var (a, b) = await NodesAsync();
        await using var _ = a;
        await using var __ = b;

        var wrong = b.AsPeer(reachable: false, thumbprint: new string('0', 64));
        a.Tunnel.SetPeers(new ShardPeerList([wrong], a.At(relay)));
        b.Tunnel.SetPeers(new ShardPeerList([a.AsPeer(reachable: false)], b.At(relay)));

        await Assert.ThrowsAnyAsync<Exception>(() => a.Tunnel.MeasureViaAsync(wrong, CancellationToken.None));
    }

    [Fact]
    public async Task APeerThatDidNotOptInIsNeverAskedForAtTheRelay()
    {
        await using var relay = await RelayAsync();
        var (a, b) = await NodesAsync(relayB: false);
        await using var _ = a;
        await using var __ = b;

        var peer = b.AsPeer(reachable: false, relay: false);
        a.Tunnel.SetPeers(new ShardPeerList([peer], a.At(relay)));
        b.Tunnel.SetPeers(new ShardPeerList([a.AsPeer(reachable: false)]));

        await Assert.ThrowsAnyAsync<Exception>(() => a.Tunnel.MeasureViaAsync(peer, CancellationToken.None));
        Assert.Equal(0, relay.Status().PairsTotal);
        Assert.Null(b.Tunnel.State()!.Relay);
    }

    [Fact]
    public async Task TheRelayRefusesANodeWhoseKeyIsNotTheOneTheHubGaveIt()
    {
        await using var relay = await RelayAsync();
        var (a, b) = await NodesAsync();
        await using var _ = a;
        await using var __ = b;

        relay.KeyFor("a");
        a.Tunnel.SetPeers(new ShardPeerList([b.AsPeer(reachable: false)], a.At(relay, key: "not-the-key")));
        b.Tunnel.SetPeers(new ShardPeerList([a.AsPeer(reachable: false)], b.At(relay)));

        var ex = await Assert.ThrowsAnyAsync<IOException>(() => a.Tunnel.MeasureViaAsync(b.AsPeer(reachable: false), CancellationToken.None));
        Assert.Contains("does not know this node", ex.Message);
        Assert.True(relay.Status().Refused > 0);

        // And the node says so in its own state: it is not waiting there either.
        for (var i = 0; i < 100 && a.Tunnel.State()!.RelayError is null; i++)
        {
            await Task.Delay(50);
        }

        Assert.Contains("does not know this node", a.Tunnel.State()!.RelayError);
    }

    [Fact]
    public async Task TheRelayJoinsOnlyNodesTheHubListedAsPeers()
    {
        await using var relay = await RelayAsync((from, to) => false);
        var (a, b) = await NodesAsync();
        await using var _ = a;
        await using var __ = b;

        a.Tunnel.SetPeers(new ShardPeerList([b.AsPeer(reachable: false)], a.At(relay)));
        b.Tunnel.SetPeers(new ShardPeerList([a.AsPeer(reachable: false)], b.At(relay)));

        var ex = await Assert.ThrowsAnyAsync<IOException>(() => a.Tunnel.MeasureViaAsync(b.AsPeer(reachable: false), CancellationToken.None));
        Assert.Contains("does not list that node", ex.Message);
    }

    [Fact]
    public async Task ANodeTheListsDroppedLosesItsPairAndItsPlace()
    {
        await using var relay = await RelayAsync();
        var (a, b) = await NodesAsync();
        await using var _ = a;
        await using var __ = b;

        a.Tunnel.SetPeers(new ShardPeerList([b.AsPeer(reachable: false)], a.At(relay)));
        b.Tunnel.SetPeers(new ShardPeerList([a.AsPeer(reachable: false)], b.At(relay)));

        using var head = new TcpClient();
        await head.ConnectAsync(IPAddress.Loopback, LocalPort(a.Tunnel, "b"));
        Assert.Equal("before", await RoundTripAsync(head.GetStream(), "before"));
        Assert.True(relay.Status().Pairs >= 1); // the head's, and whatever round trip is being measured

        relay.Keep(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a" });

        Assert.Null(await RoundTripAsync(head.GetStream(), "after"));
        Assert.DoesNotContain("b", relay.Status().Waiting.Keys);
        var ex = await Assert.ThrowsAnyAsync<IOException>(() => a.Tunnel.MeasureViaAsync(b.AsPeer(reachable: false), CancellationToken.None));
        Assert.Contains("refused", ex.Message);
    }

    [Fact]
    public async Task ARelayThatIsOffListensNowhere()
    {
        await using var relay = new ShardRelay(new ShardRelayOptions(), NullLogger.Instance);
        await relay.StartAsync(CancellationToken.None);

        var status = relay.Status();
        Assert.False(status.Enabled);
        Assert.Null(status.Port);
    }

    [Fact]
    public void TheHelloNamesTheRoleTheNodeAndTheTargetAndItsProofIsBoundToTheNonce()
    {
        var nonce = RandomNumberGenerator.GetBytes(ShardRelayWire.NonceBytes);
        var line = ShardRelayWire.Hello("key", nonce, ShardRelayWire.Dial, "node a", "node b").TrimEnd('\n');

        Assert.True(ShardRelayWire.TryParseHello(line, out var role, out var nodeId, out var target, out var proof));
        Assert.Equal((ShardRelayWire.Dial, "node a", "node b"), (role, nodeId, target));
        Assert.Equal(ShardRelayWire.Proof("key", nonce, ShardRelayWire.Dial, "node a", "node b"), proof);
        Assert.NotEqual(proof, ShardRelayWire.Proof("key", RandomNumberGenerator.GetBytes(ShardRelayWire.NonceBytes), ShardRelayWire.Dial, "node a", "node b"));
        Assert.NotEqual(proof, ShardRelayWire.Proof("key", nonce, ShardRelayWire.Dial, "node a", "node c"));
        Assert.NotEqual(proof, ShardRelayWire.Proof("other", nonce, ShardRelayWire.Dial, "node a", "node b"));
        Assert.DoesNotContain("key", line);

        Assert.False(ShardRelayWire.TryParseHello("GET / HTTP/1.1", out _, out _, out _, out _));
        Assert.False(ShardRelayWire.TryParseHello(ShardRelayWire.Hello("key", nonce, ShardRelayWire.Listen, "a", "b").TrimEnd('\n'), out _, out _, out _, out _));
    }
}
