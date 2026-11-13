using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using InferHub.Shared.Fit;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary><c>ShardRelay</c> (phase 139). Off by default: nothing listens and no node is told of a relay.</summary>
public sealed class ShardRelayOptions
{
    public const string SectionName = "ShardRelay";

    public bool Enabled { get; set; }

    /// <summary>The TCP port nodes dial (outbound from them, like their hub connection).</summary>
    public int Port { get; set; } = 50054;

    /// <summary>The host nodes dial for the relay. Null: each node uses the host of the coordinator URL it already dials.</summary>
    public string? Host { get; set; }

    /// <summary>Pairs relayed at once; a dial past it is refused with the sentence.</summary>
    public int MaxPairs { get; set; } = 64;

    /// <summary>How long a dial waits for the node it asked for to have a connection waiting.</summary>
    public int WaitSeconds { get; set; } = 5;
}

/// <summary>What the relay is doing now, for <c>GET /api/admin/shards/relay</c>.</summary>
public sealed record ShardRelayStatus(
    bool Enabled,
    int? Port,
    string? Host,
    int Pairs,
    long PairsTotal,
    long BytesRelayed,
    long Refused,
    IReadOnlyDictionary<string, int> Waiting);

/// <summary>
/// Phase 139: the meeting point for two nodes with no direct path (both behind NAT, or one of them). Each such node dials
/// out to here — as it dials its hub — and holds a few connections waiting; a node that wants to reach it dials here too
/// and names it; the relay joins the two sockets and copies bytes. It never reads them: the nodes run 135's mTLS through the
/// pipe and check each other's thumbprints as on a direct path (D1). A node proves itself with an HMAC over the relay's
/// nonce, keyed by a secret the hub gave it over its own connection (D2); a dial is allowed only between nodes the hub
/// listed as peers, and a node that leaves the lists loses its key, its waiting connections and its pairs at once (D3).
/// In memory (rule 4).
/// </summary>
public sealed class ShardRelay : IHostedService, IAsyncDisposable
{
    private const int MaxWaitingPerNode = 8;
    private static readonly TimeSpan Hello = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    private readonly ShardRelayOptions options;
    private readonly ILogger logger;
    private readonly ConcurrentDictionary<string, string> keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Waiting>> waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Pair, byte> pairs = new();
    private TcpListener? listener;
    private CancellationTokenSource? life;
    private Task? accepting;
    private long pairsTotal;
    private long bytes;
    private long refused;

    public ShardRelay(IOptions<ShardRelayOptions> options, ILogger<ShardRelay> logger)
        : this(options.Value, logger)
    {
    }

    internal ShardRelay(ShardRelayOptions options, ILogger logger)
    {
        this.options = options;
        this.logger = logger;
    }

    public bool Enabled => options.Enabled;

    public string? Host => string.IsNullOrWhiteSpace(options.Host) ? null : options.Host.Trim();

    /// <summary>The port it really listens on — <c>Port</c>, or what the system gave for 0 in tests.</summary>
    public int Port => listener?.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : options.Port;

    /// <summary>D3: whether one node may be joined to another — the publisher's latest lists. Unset: nobody may.</summary>
    public Func<string, string, bool>? Authorize { get; set; }

    /// <summary>D2: the secret one node proves itself with here; made on first use, gone when the node leaves the lists.</summary>
    public string KeyFor(string nodeId) =>
        keys.GetOrAdd(nodeId.Trim(), _ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));

    /// <summary>
    /// D3: the nodes that may use the relay now. Everyone else loses the key, the waiting connections and any pair it is part
    /// of; a pair between two nodes that are no longer peers is cut too.
    /// </summary>
    public void Keep(IReadOnlySet<string> nodeIds)
    {
        foreach (var gone in keys.Keys.Where(id => !nodeIds.Contains(id)).ToArray())
        {
            keys.TryRemove(gone, out _);

            if (waiting.TryRemove(gone, out var queue))
            {
                while (queue.TryDequeue(out var idle))
                {
                    idle.Close();
                }
            }
        }

        foreach (var pair in pairs.Keys)
        {
            if (!nodeIds.Contains(pair.From) || !nodeIds.Contains(pair.To) || Authorize?.Invoke(pair.From, pair.To) != true)
            {
                logger.LogInformation("Shard relay: cutting {From} → {To} — no longer peers", pair.From, pair.To);
                pair.Close();
            }
        }
    }

    public ShardRelayStatus Status() => new(
        options.Enabled,
        listener is null ? null : Port,
        Host,
        pairs.Count,
        Interlocked.Read(ref pairsTotal),
        Interlocked.Read(ref bytes),
        Interlocked.Read(ref refused),
        waiting.Where(w => !w.Value.IsEmpty).ToDictionary(w => w.Key, w => w.Value.Count(c => !c.Closed), StringComparer.OrdinalIgnoreCase));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return Task.CompletedTask;
        }

        if (options.Port is < 0 or > 65535)
        {
            throw new InvalidOperationException($"ShardRelay:Port must be 1–65535 (got {options.Port})");
        }

        try
        {
            var bound = new TcpListener(IPAddress.IPv6Any, options.Port);
            bound.Server.DualMode = true;
            bound.Start();
            listener = bound;
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"ShardRelay:Enabled is set and port {options.Port} cannot be listened on: {ex.Message}", ex);
        }

        life = new CancellationTokenSource();
        accepting = Task.Run(() => AcceptAsync(life.Token));
        logger.LogInformation("Shard relay listening on port {Port}: nodes with no direct path meet here; it copies their TLS and reads none of it", Port);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (life is null)
        {
            return;
        }

        await life.CancelAsync();
        listener?.Stop();

        foreach (var queue in waiting.Values)
        {
            while (queue.TryDequeue(out var idle))
            {
                idle.Close();
            }
        }

        foreach (var pair in pairs.Keys)
        {
            pair.Close();
        }

        try
        {
            await (accepting ?? Task.CompletedTask);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        life?.Dispose();
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

            _ = Task.Run(() => HandleAsync(client, token), token);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        var handedOver = false;

        try
        {
            client.NoDelay = true;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            var stream = client.GetStream();
            var nonce = RandomNumberGenerator.GetBytes(ShardRelayWire.NonceBytes);

            using var hello = CancellationTokenSource.CreateLinkedTokenSource(token);
            hello.CancelAfter(Hello);
            await stream.WriteAsync(nonce, hello.Token);

            if (await ReadLineAsync(stream, hello.Token) is not { } line
                || !ShardRelayWire.TryParseHello(line, out var role, out var nodeId, out var target, out var proof))
            {
                await RefuseAsync(stream, "not a relay hello", token);
                return;
            }

            // The same answer for a node with no key and one with the wrong proof: nothing to learn by probing ids.
            if (!keys.TryGetValue(nodeId, out var key)
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ShardRelayWire.Proof(key, nonce, role, nodeId, target)), Encoding.ASCII.GetBytes(proof)))
            {
                await RefuseAsync(stream, "this coordinator's relay does not know this node", token);
                return;
            }

            if (role == ShardRelayWire.Listen)
            {
                var queue = waiting.GetOrAdd(nodeId, _ => new ConcurrentQueue<Waiting>());

                if (queue.Count(w => !w.Closed) >= MaxWaitingPerNode)
                {
                    await RefuseAsync(stream, $"this node already has {MaxWaitingPerNode} connections waiting", token);
                    return;
                }

                await stream.WriteAsync(new[] { ShardRelayWire.Ok }, token);
                var idle = new Waiting(client);
                queue.Enqueue(idle);
                handedOver = true;
                _ = Task.Run(() => idle.KeepAliveAsync(KeepAlive, token), token);
                return;
            }

            if (Authorize?.Invoke(nodeId, target!) != true)
            {
                await RefuseAsync(stream, "the coordinator does not list that node as this node's peer", token);
                return;
            }

            if (pairs.Count >= Math.Max(1, options.MaxPairs))
            {
                await RefuseAsync(stream, $"the relay is full (ShardRelay:MaxPairs {options.MaxPairs})", token);
                return;
            }

            if (await TakeAsync(target!, token) is not { } other)
            {
                await RefuseAsync(stream, "that node has no connection waiting at the relay (is Node:Shards:TunnelRelay set on it, and can it reach this port?)", token);
                return;
            }

            await stream.WriteAsync(new[] { ShardRelayWire.Ok }, token);
            var pair = new Pair(nodeId, target!, client, other);
            pairs[pair] = 0;
            Interlocked.Increment(ref pairsTotal);
            handedOver = true;

            try
            {
                await pair.RunAsync(count => Interlocked.Add(ref bytes, count), token);
            }
            finally
            {
                pairs.TryRemove(pair, out _);
                pair.Close();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            if (!handedOver)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>A waiting connection of that node, told it is paired; waits a little for one to arrive (a node replaces each it gives up).</summary>
    private async Task<TcpClient?> TakeAsync(string nodeId, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, options.WaitSeconds));

        while (true)
        {
            if (waiting.TryGetValue(nodeId, out var queue))
            {
                while (queue.TryDequeue(out var idle))
                {
                    if (await idle.ClaimAsync(token))
                    {
                        return idle.Client;
                    }
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(50, token);
        }
    }

    private async Task RefuseAsync(NetworkStream stream, string why, CancellationToken token)
    {
        Interlocked.Increment(ref refused);
        logger.LogInformation("Shard relay refused {Remote}: {Why}", stream.Socket.RemoteEndPoint, why);

        try
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"{(char)ShardRelayWire.No}{why}\n"), token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[ShardRelayWire.MaxLine];
        var length = 0;

        // A byte at a time: nothing after the newline may be consumed — it belongs to the pipe.
        while (length < buffer.Length)
        {
            if (await stream.ReadAsync(buffer.AsMemory(length, 1), token) == 0)
            {
                return null;
            }

            if (buffer[length] == (byte)'\n')
            {
                return Encoding.ASCII.GetString(buffer, 0, length);
            }

            length++;
        }

        return null;
    }

    /// <summary>One connection a node holds open here until somebody asks for it.</summary>
    private sealed class Waiting(TcpClient client)
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private bool claimed;

        public TcpClient Client { get; } = client;

        public bool Closed { get; private set; }

        /// <summary>Tells the node its connection was paired. False when the connection is already dead.</summary>
        public async Task<bool> ClaimAsync(CancellationToken token)
        {
            await gate.WaitAsync(token);

            try
            {
                if (Closed)
                {
                    return false;
                }

                claimed = true;
                await Client.GetStream().WriteAsync(new[] { ShardRelayWire.Paired }, token);
                return true;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                Close();
                return false;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>A byte now and then while it waits: a NAT forgets a silent connection, and a dead one is found here.</summary>
        public async Task KeepAliveAsync(TimeSpan every, CancellationToken token)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(every, token);
                    await gate.WaitAsync(token);

                    try
                    {
                        if (claimed || Closed)
                        {
                            return;
                        }

                        await Client.GetStream().WriteAsync(new[] { ShardRelayWire.Alive }, token);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                if (!claimed)
                {
                    Close();
                }
            }
        }

        public void Close()
        {
            Closed = true;
            Client.Dispose();
        }
    }

    /// <summary>Two nodes joined: bytes copied both ways until either end closes.</summary>
    private sealed class Pair(string from, string to, TcpClient dialer, TcpClient listener)
    {
        public string From { get; } = from;

        public string To { get; } = to;

        public async Task RunAsync(Action<int> counted, CancellationToken token)
        {
            using var both = CancellationTokenSource.CreateLinkedTokenSource(token);
            var one = CopyAsync(dialer.GetStream(), listener.GetStream(), counted, both.Token);
            var two = CopyAsync(listener.GetStream(), dialer.GetStream(), counted, both.Token);
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

        public void Close()
        {
            dialer.Dispose();
            listener.Dispose();
        }

        private static async Task CopyAsync(NetworkStream from, NetworkStream to, Action<int> counted, CancellationToken token)
        {
            var buffer = new byte[81920];

            while (await from.ReadAsync(buffer, token) is var read and > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), token);
                counted(read);
            }
        }
    }
}
