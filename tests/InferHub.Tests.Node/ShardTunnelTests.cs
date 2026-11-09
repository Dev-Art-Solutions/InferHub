using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using InferHub.Node.Backends.Shards;
using InferHub.Node.Configuration;
using InferHub.Shared.Fit;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 135 on the node, over real TLS on loopback: a head's bytes reach a peer's worker through the forward and the tunnel
/// (D2, D3); both ends accept only the certificates the hub listed (D1) — a peer not listed, or listed under another
/// thumbprint, is refused, and a peer dropped from the list loses its open connection; the round trip is measured through
/// the tunnel (D4); without a certificate there is no tunnel, and the state says why.
/// </summary>
public sealed class ShardTunnelTests
{
    private sealed class Node : IAsyncDisposable
    {
        private readonly byte[] pfx;

        public Node(string name)
        {
            Name = name;
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            pfx = cert.Export(X509ContentType.Pkcs12);
            Thumbprint = ShardTunnelWire.Thumbprint(cert.RawData);
            Worker = new EchoWorker();
            Options = new ShardOptions { Enabled = true, RamGiB = 1, Port = Worker.Port, TunnelPort = FreePort() };
            Tunnel = new ShardTunnel(Options, () => X509CertificateLoader.LoadPkcs12(pfx, null), () => Thumbprint,
                NullLogger.Instance, null, TimeSpan.FromMilliseconds(200));
        }

        public string Name { get; }
        public string Thumbprint { get; }
        public EchoWorker Worker { get; }
        public ShardOptions Options { get; }
        public ShardTunnel Tunnel { get; }

        public ShardPeer AsPeer(string? thumbprint = null) =>
            new(Name, Name.ToUpperInvariant(), $"127.0.0.1:{Options.TunnelPort}", thumbprint ?? Thumbprint);

        public async ValueTask DisposeAsync()
        {
            await Tunnel.DisposeAsync();
            Worker.Dispose();
        }
    }

    /// <summary>Stands in for <c>ggml-rpc-server</c> on loopback: whatever arrives goes back.</summary>
    private sealed class EchoWorker : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);

        public EchoWorker()
        {
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;

                    try
                    {
                        client = await listener.AcceptTcpClientAsync();
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
            });
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public void Dispose() => listener.Stop();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<(Node A, Node B)> PairAsync()
    {
        var a = new Node("a");
        var b = new Node("b");
        await a.Tunnel.StartAsync(CancellationToken.None);
        await b.Tunnel.StartAsync(CancellationToken.None);
        return (a, b);
    }

    private static int LocalPort(ShardTunnel tunnel, string peer) =>
        ShardTunnel.Split(tunnel.Peers().Single(p => p.NodeId == peer).Local!).Port;

    /// <summary>Writes through the forward and reads it back from the peer's worker; null when the tunnel closed instead.</summary>
    private static async Task<string?> RoundTripAsync(Stream stream, string text)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes);
        var back = new byte[bytes.Length];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await stream.ReadExactlyAsync(back, deadline.Token);
            return System.Text.Encoding.ASCII.GetString(back);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException)
        {
            return null;
        }
    }

    [Fact]
    public async Task AHeadsBytesReachThePeersWorkerThroughTheTunnelWhenBothAreListed()
    {
        var (a, b) = await PairAsync();
        await using var _ = a;
        await using var __ = b;

        a.Tunnel.SetPeers(new ShardPeerList([b.AsPeer()]));
        b.Tunnel.SetPeers(new ShardPeerList([a.AsPeer()]));

        var local = a.Tunnel.Peers().Single();
        Assert.StartsWith("127.0.0.1:", local.Local);
        Assert.Equal($"127.0.0.1:{b.Options.TunnelPort}", local.Endpoint);

        using var head = new TcpClient();
        await head.ConnectAsync(IPAddress.Loopback, LocalPort(a.Tunnel, "b"));
        Assert.Equal("tensor bytes", await RoundTripAsync(head.GetStream(), "tensor bytes"));

        var rtt = await a.Tunnel.MeasureAsync(b.AsPeer(), CancellationToken.None);
        Assert.InRange(rtt, 0, 1000);
    }

    [Fact]
    public async Task APeerThatDoesNotListThisNodeRefusesIt()
    {
        var (a, b) = await PairAsync();
        await using var _ = a;
        await using var __ = b;

        a.Tunnel.SetPeers(new ShardPeerList([b.AsPeer()]));
        b.Tunnel.SetPeers(new ShardPeerList([]));

        using var head = new TcpClient();
        await head.ConnectAsync(IPAddress.Loopback, LocalPort(a.Tunnel, "b"));
        Assert.Null(await RoundTripAsync(head.GetStream(), "should not arrive"));
        await Assert.ThrowsAnyAsync<Exception>(() => a.Tunnel.MeasureAsync(b.AsPeer(), CancellationToken.None));
    }

    [Fact]
    public async Task APeerUnderAnotherThumbprintIsNotTheNodeTheHubNamed()
    {
        var (a, b) = await PairAsync();
        await using var _ = a;
        await using var __ = b;

        // A was told B's endpoint with a certificate B does not hold: A refuses B's handshake, whatever B allows.
        var wrong = b.AsPeer(new string('0', 64));
        a.Tunnel.SetPeers(new ShardPeerList([wrong]));
        b.Tunnel.SetPeers(new ShardPeerList([a.AsPeer()]));

        await Assert.ThrowsAnyAsync<Exception>(() => a.Tunnel.MeasureAsync(wrong, CancellationToken.None));
    }

    [Fact]
    public async Task APeerDroppedFromTheListLosesTheConnectionItHasOpen()
    {
        var (a, b) = await PairAsync();
        await using var _ = a;
        await using var __ = b;

        a.Tunnel.SetPeers(new ShardPeerList([b.AsPeer()]));
        b.Tunnel.SetPeers(new ShardPeerList([a.AsPeer()]));

        using var head = new TcpClient();
        await head.ConnectAsync(IPAddress.Loopback, LocalPort(a.Tunnel, "b"));
        var stream = head.GetStream();
        Assert.Equal("first", await RoundTripAsync(stream, "first"));

        // The hub stops listing A (revoked, gone): B cuts the connection that is already open.
        b.Tunnel.SetPeers(new ShardPeerList([]));
        await Task.Delay(300);
        Assert.Null(await RoundTripAsync(stream, "second"));
    }

    [Fact]
    public async Task TheRoundTripIsMeasuredAndShownForEachPeer()
    {
        var (a, b) = await PairAsync();
        await using var _ = a;
        await using var __ = b;

        a.Tunnel.SetPeers(new ShardPeerList([b.AsPeer()]));
        b.Tunnel.SetPeers(new ShardPeerList([a.AsPeer()]));

        ShardPeerState? seen = null;

        for (var i = 0; i < 50 && seen?.RttMs is null; i++)
        {
            await Task.Delay(100);
            seen = a.Tunnel.Peers().Single();
        }

        Assert.NotNull(seen?.RttMs);
        Assert.Null(seen!.Error);
        Assert.NotNull(seen.AtUtc);
    }

    [Fact]
    public async Task WithoutACertificateThereIsNoTunnelAndTheStateSaysWhy()
    {
        var options = new ShardOptions { Enabled = true, RamGiB = 1, TunnelPort = FreePort() };
        await using var tunnel = new ShardTunnel(options, () => null, () => null, NullLogger.Instance, null, TimeSpan.FromSeconds(30));
        await tunnel.StartAsync(CancellationToken.None);

        Assert.Null(tunnel.ListeningPort);
        Assert.Contains("no node certificate", tunnel.State()!.Off);

        // Off by default: no offer, no tunnel state at all.
        await using var none = new ShardTunnel(new ShardOptions(), () => null, () => null, NullLogger.Instance, null, TimeSpan.FromSeconds(30));
        Assert.False(none.Enabled);
        Assert.Null(none.State());
    }

    [Fact]
    public async Task ACertificateThatArrivesLaterOpensTheTunnelAndTheListItWasSentFirst()
    {
        // Found in the lab: the node enrolls when it first connects, seconds after the tunnel started — and the hub's list
        // may already be there, and is not sent twice.
        var (real, b) = await PairAsync();
        await using var _ = real;
        await using var __ = b;
        await real.Tunnel.StopAsync(CancellationToken.None);

        var enrolled = false;
        var pfx = new Node("late");
        await using var ___ = pfx;
        var options = new ShardOptions { Enabled = true, RamGiB = 1, Port = pfx.Worker.Port, TunnelPort = FreePort() };
        await using var late = new ShardTunnel(options,
            () => enrolled ? X509CertificateLoader.LoadPkcs12(PfxOf(pfx), null) : null, () => pfx.Thumbprint, NullLogger.Instance, null, TimeSpan.FromSeconds(30));

        await late.StartAsync(CancellationToken.None);
        late.SetPeers(new ShardPeerList([b.AsPeer()]));
        Assert.Null(late.ListeningPort);
        Assert.Contains("no node certificate", late.State()!.Off);

        enrolled = true;

        for (var i = 0; i < 50 && late.ListeningPort is null; i++)
        {
            await Task.Delay(100);
        }

        Assert.Equal(options.TunnelPort, late.ListeningPort);
        Assert.Null(late.State()!.Off);
        Assert.StartsWith("127.0.0.1:", late.Peers().Single().Local);
    }

    private static byte[] PfxOf(Node node) =>
        (byte[])typeof(Node).GetField("pfx", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(node)!;

    [Theory]
    [InlineData("b:50053", "b", 50053)]
    [InlineData("10.0.0.7:50053", "10.0.0.7", 50053)]
    [InlineData("[fd00::7]:50053", "fd00::7", 50053)]
    public void AnEndpointIsHostAndPort(string endpoint, string host, int port) =>
        Assert.Equal((host, port), ShardTunnel.Split(endpoint));
}
