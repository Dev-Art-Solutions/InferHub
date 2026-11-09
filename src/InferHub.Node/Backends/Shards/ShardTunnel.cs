using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using InferHub.Node.Configuration;
using InferHub.Shared.Fit;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends.Shards;

/// <summary>
/// Phase 135: the only way another node reaches this node's shard worker, and this node's way to theirs. A TLS listener on
/// <c>Node:Shards:TunnelPort</c> where both ends present their 127 certificates and each accepts only a thumbprint the hub
/// listed (D1); one byte of mode after the handshake (D2); a loopback forward per peer for a head to use as an <c>--rpc</c>
/// endpoint (D3); and the round trip to each peer, measured through the tunnel (D4).
/// </summary>
public sealed class ShardTunnel : IHostedService, IAsyncDisposable
{
    private const int Pings = 5;
    private static readonly TimeSpan Handshake = TimeSpan.FromSeconds(10);

    private readonly ShardOptions options;
    private readonly Func<X509Certificate2?> certificate;
    private readonly Func<string?> thumbprint;
    private readonly ILogger logger;
    private readonly TimeProvider time;
    private readonly TimeSpan interval;
    private readonly object sync = new();
    private readonly Dictionary<string, Forward> forwards = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Inbound, byte> inbound = new();
    private readonly SemaphoreSlim measureNow = new(0, 1);
    private IReadOnlyList<ShardPeer> peers = [];
    private HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase);
    private TcpListener? listener;
    private CancellationTokenSource? life;
    private Task? accepting;
    private Task? measuring;
    private string? off;

    public ShardTunnel(IOptions<NodeOptions> node, Identity.NodeCredential credential, ILogger<ShardTunnel> logger, TimeProvider? time = null)
        : this(node.Value.Shards, credential.TlsCertificate, () => credential.Thumbprint, logger, time, TimeSpan.FromSeconds(30))
    {
    }

    internal ShardTunnel(ShardOptions options, Func<X509Certificate2?> certificate, Func<string?> thumbprint, ILogger logger, TimeProvider? time, TimeSpan interval)
    {
        this.options = options;
        this.certificate = certificate;
        this.thumbprint = thumbprint;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
        this.interval = interval;
    }

    /// <summary>A peer joined or left, or a round trip was measured: what the hub shows changed.</summary>
    public event Action? Changed;

    /// <summary>Whether this node is meant to have a tunnel at all — an offer and a port. Whether it listens is <see cref="State"/>.</summary>
    public bool Enabled => options.Enabled && options.TunnelPort > 0;

    /// <summary>The port it really listens on — <c>TunnelPort</c>, or what the system gave for 0 in tests. Null when not listening.</summary>
    public int? ListeningPort => listener?.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : null;

    public ShardTunnelState? State() =>
        !options.Enabled
            ? null
            : new ShardTunnelState(ListeningPort ?? options.TunnelPort, Blank(options.TunnelAdvertise), thumbprint(),
                options.TunnelPort <= 0 ? "Node:Shards:TunnelPort is 0: the worker is this machine's alone" : off);

    public IReadOnlyList<ShardPeerState> Peers()
    {
        lock (sync)
        {
            return peers.Select(p => forwards.TryGetValue(p.NodeId, out var f) ? f.State() : new ShardPeerState(p.NodeId, p.Name, p.Endpoint, null, null, "no forward", null)).ToArray();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return Task.CompletedTask;
        }

        life = new CancellationTokenSource();

        if (Listen())
        {
            return Task.CompletedTask;
        }

        // Found in the lab: a node enrolls when it first connects to its hub, after the hosted services start — so the
        // certificate usually arrives seconds later. Wait for it rather than staying off until a restart.
        accepting = Task.Run(async () =>
        {
            while (!life.Token.IsCancellationRequested && listener is null && off is not null && off.StartsWith(NoCertificate, StringComparison.Ordinal))
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), time, life.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (Listen())
                {
                    Changed?.Invoke();
                    await AcceptAsync(life.Token);
                    return;
                }
            }
        });

        return Task.CompletedTask;
    }

    private const string NoCertificate = "no node certificate";

    /// <summary>Starts listening if this node holds a certificate; otherwise records why not. True when it listens.</summary>
    private bool Listen()
    {
        using (var own = certificate())
        {
            if (own is null)
            {
                if (off is null)
                {
                    logger.LogInformation("Shard tunnel waits for this node's certificate (127): it enrolls when it connects to its coordinator");
                }

                off = NoCertificate + ": the tunnel proves this node with the one its coordinator issued (enroll with Coordinator:EnrollmentToken)";
                return false;
            }
        }

        try
        {
            var bound = new TcpListener(IPAddress.IPv6Any, options.TunnelPort);
            bound.Server.DualMode = true;
            bound.Start();
            listener = bound;
        }
        catch (SocketException ex)
        {
            off = $"cannot listen on port {options.TunnelPort}: {ex.Message}";
            logger.LogWarning("Shard tunnel off: {Why}", off);
            return false;
        }

        off = null;
        measuring = Task.Run(() => MeasureLoopAsync(life!.Token));

        if (accepting is null)
        {
            accepting = Task.Run(() => AcceptAsync(life!.Token));
        }

        logger.LogInformation("Shard tunnel listening on port {Port}; it accepts only the nodes its coordinator lists", ListeningPort);

        // A list that arrived before the certificate was kept, not acted on; the hub will not send the same list again.
        IReadOnlyList<ShardPeer> held;

        lock (sync)
        {
            held = peers;
        }

        if (held.Count > 0)
        {
            SetPeers(new ShardPeerList(held));
        }

        return true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (life is null)
        {
            return;
        }

        await life.CancelAsync();
        listener?.Stop();

        lock (sync)
        {
            foreach (var forward in forwards.Values)
            {
                forward.Dispose();
            }

            forwards.Clear();
        }

        foreach (var connection in inbound.Keys)
        {
            connection.Close();
        }

        foreach (var task in new[] { accepting, measuring })
        {
            try
            {
                await (task ?? Task.CompletedTask);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        life?.Dispose();
    }

    /// <summary>
    /// The hub's list (D1), replacing the last whole: forwards opened for new peers and closed for those gone, and every
    /// connection from a peer no longer listed cut — a revoked node's door closes when the hub says, not when it hangs up.
    /// </summary>
    public void SetPeers(ShardPeerList list)
    {
        if (!Enabled || listener is null)
        {
            lock (sync)
            {
                peers = list.Peers;
            }

            return;
        }

        lock (sync)
        {
            peers = list.Peers;
            allowed = list.Peers.Select(p => p.Thumbprint).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var gone in forwards.Keys.Where(id => list.Peers.All(p => !string.Equals(p.NodeId, id, StringComparison.OrdinalIgnoreCase))).ToArray())
            {
                forwards[gone].Dispose();
                forwards.Remove(gone);
            }

            foreach (var peer in list.Peers)
            {
                if (forwards.TryGetValue(peer.NodeId, out var existing))
                {
                    if (existing.Peer == peer)
                    {
                        continue;
                    }

                    existing.Dispose();
                }

                forwards[peer.NodeId] = Forward.Open(this, peer);
            }
        }

        foreach (var connection in inbound.Keys.Where(c => !allowed.Contains(c.Thumbprint)))
        {
            logger.LogInformation("Shard tunnel: closing a connection from a node no longer listed ({Thumbprint})", connection.Thumbprint[..12]);
            connection.Close();
        }

        if (measureNow.CurrentCount == 0)
        {
            measureNow.Release();
        }

        Changed?.Invoke();
    }

    private async Task AcceptAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await listener!.AcceptTcpClientAsync(token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, token), token);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        using var accepted = client;
        client.NoDelay = true;
        using var cert = certificate();

        if (cert is null)
        {
            return;
        }

        string? peer = null;
        await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, presented, _, _) =>
        {
            peer = presented is null ? null : ShardTunnelWire.Thumbprint(presented.GetRawCertData());
            return peer is not null && Allowed(peer);
        });

        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(Handshake);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = cert,
                ClientCertificateRequired = true,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, handshake.Token);
        }
        catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException or IOException or OperationCanceledException)
        {
            logger.LogInformation("Shard tunnel: refused {Remote} — {Why}", client.Client.RemoteEndPoint, peer is null ? "no certificate" : Allowed(peer) ? ex.Message : $"certificate {peer[..12]}… is not one the coordinator listed");
            return;
        }

        var connection = new Inbound(peer!, CancellationTokenSource.CreateLinkedTokenSource(token));
        inbound[connection] = 0;

        try
        {
            var mode = new byte[1];
            await tls.ReadExactlyAsync(mode, connection.Token);

            if (mode[0] == ShardTunnelWire.Ping)
            {
                var frame = new byte[8];

                while (true)
                {
                    await tls.ReadExactlyAsync(frame, connection.Token);
                    await tls.WriteAsync(frame, connection.Token);
                    await tls.FlushAsync(connection.Token);
                }
            }

            if (mode[0] == ShardTunnelWire.Rpc)
            {
                using var worker = new TcpClient { NoDelay = true };
                await worker.ConnectAsync(IPAddress.Loopback, options.Port, connection.Token);
                await Pipe(tls, worker.GetStream(), connection.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or EndOfStreamException or ObjectDisposedException)
        {
        }
        finally
        {
            inbound.TryRemove(connection, out var _);
            connection.Close();
        }
    }

    private bool Allowed(string thumb)
    {
        lock (sync)
        {
            return allowed.Contains(thumb);
        }
    }

    /// <summary>Opens TLS to a peer as a client, checking it is the certificate the hub named, and sends the mode byte.</summary>
    internal async Task<(TcpClient Client, SslStream Tls)> DialAsync(ShardPeer peer, byte mode, CancellationToken token)
    {
        var (host, port) = Split(peer.Endpoint);
        var client = new TcpClient { NoDelay = true };

        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(Handshake);
            await client.ConnectAsync(host, port, handshake.Token);
            var cert = certificate() ?? throw new IOException("this node has no certificate");
            var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, presented, _, _) =>
                presented is not null && string.Equals(ShardTunnelWire.Thumbprint(presented.GetRawCertData()), peer.Thumbprint, StringComparison.OrdinalIgnoreCase));

            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "inferhub-shard",
                ClientCertificates = new X509CertificateCollection { cert },
                LocalCertificateSelectionCallback = (_, _, _, _, _) => cert,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, handshake.Token);

            await tls.WriteAsync(new[] { mode }, token);
            await tls.FlushAsync(token);
            return (client, tls);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>D4: five 8-byte frames through the tunnel; the median round trip in milliseconds.</summary>
    internal async Task<double> MeasureAsync(ShardPeer peer, CancellationToken token)
    {
        var (client, tls) = await DialAsync(peer, ShardTunnelWire.Ping, token);
        using var _ = client;
        await using var __ = tls;
        var frame = new byte[8];
        var answer = new byte[8];
        var rounds = new List<double>(Pings);

        for (var i = 0; i < Pings; i++)
        {
            BitConverter.TryWriteBytes(frame, Stopwatch.GetTimestamp());
            var started = Stopwatch.GetTimestamp();
            await tls.WriteAsync(frame, token);
            await tls.FlushAsync(token);
            await tls.ReadExactlyAsync(answer, token);
            rounds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        rounds.Sort();
        return Math.Round(rounds[rounds.Count / 2], 2);
    }

    private async Task MeasureLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Forward[] now;

            lock (sync)
            {
                now = forwards.Values.ToArray();
            }

            foreach (var forward in now)
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(Handshake * 2);
                    forward.Measured(await MeasureAsync(forward.Peer, deadline.Token), null, time.GetUtcNow());
                }
                catch (Exception ex)
                {
                    // Found by a flaky test: a measurement cut short by the node stopping faulted the loop, and the stop
                    // threw it. Stopping is not a peer's failure.
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    forward.Measured(null, Reason(ex), time.GetUtcNow());
                }
            }

            if (now.Length > 0)
            {
                Changed?.Invoke();
            }

            try
            {
                await measureNow.WaitAsync(interval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static string Reason(Exception ex) => ex switch
    {
        System.Security.Authentication.AuthenticationException => "TLS refused: the peer is not the certificate the coordinator named, or it does not list this node",
        OperationCanceledException => "no answer within the deadline",
        SocketException socket => $"cannot connect: {socket.Message}",
        _ => ex.Message
    };

    private static async Task Pipe(Stream a, Stream b, CancellationToken token)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(token);
        var one = a.CopyToAsync(b, 81920, both.Token);
        var two = b.CopyToAsync(a, 81920, both.Token);
        await Task.WhenAny(one, two);
        await both.CancelAsync();

        try
        {
            await Task.WhenAll(one, two);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    /// <summary><c>host:port</c> or <c>[v6]:port</c>.</summary>
    public static (string Host, int Port) Split(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');

        if (colon <= 0 || !int.TryParse(endpoint.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            throw new FormatException($"'{endpoint}' is not host:port");
        }

        return (endpoint[..colon].Trim('[', ']'), port);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>One connection in from a peer, closable when the hub stops listing it.</summary>
    private sealed class Inbound(string thumbprint, CancellationTokenSource cts)
    {
        public string Thumbprint { get; } = thumbprint;

        public CancellationToken Token => cts.Token;

        public void Close()
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>D3: a loopback listener whose every connection goes through the tunnel to one peer's worker.</summary>
    private sealed class Forward : IDisposable
    {
        private readonly ShardTunnel owner;
        private readonly TcpListener local;
        private readonly CancellationTokenSource cts = new();
        private double? rtt;
        private string? error;
        private DateTimeOffset? at;

        private Forward(ShardTunnel owner, ShardPeer peer, TcpListener local)
        {
            this.owner = owner;
            Peer = peer;
            this.local = local;
        }

        public ShardPeer Peer { get; }

        public static Forward Open(ShardTunnel owner, ShardPeer peer)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var forward = new Forward(owner, peer, listener);
            _ = Task.Run(forward.AcceptAsync);
            return forward;
        }

        public ShardPeerState State()
        {
            var port = ((IPEndPoint)local.LocalEndpoint).Port;
            return new ShardPeerState(Peer.NodeId, Peer.Name, Peer.Endpoint, $"127.0.0.1:{port}", rtt, error, at);
        }

        public void Measured(double? ms, string? why, DateTimeOffset when)
        {
            rtt = ms;
            error = why;
            at = when;
        }

        public void Dispose()
        {
            cts.Cancel();
            local.Stop();
        }

        private async Task AcceptAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient head;

                try
                {
                    head = await local.AcceptTcpClientAsync(cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using var _ = head;
                    head.NoDelay = true;

                    try
                    {
                        var (client, tls) = await owner.DialAsync(Peer, ShardTunnelWire.Rpc, cts.Token);
                        using var __ = client;
                        await using var ___ = tls;
                        await Pipe(head.GetStream(), tls, cts.Token);
                    }
                    catch (Exception ex) when (!cts.IsCancellationRequested)
                    {
                        owner.logger.LogWarning("Shard tunnel to {Peer} ({Endpoint}): {Why}", Peer.Name, Peer.Endpoint, Reason(ex));
                    }
                });
            }
        }
    }
}
