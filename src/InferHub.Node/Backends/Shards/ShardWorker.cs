using System.Diagnostics;
using InferHub.Node.Configuration;
using InferHub.Shared.Fit;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends.Shards;

/// <summary>
/// Phase 134: the slice of this machine offered to groups — llama.cpp's <c>ggml-rpc-server</c>, supervised as any engine
/// the node launches (95's <see cref="EngineProcess"/>), bound to loopback (D1), "running" once it has answered the
/// RPC handshake a head would send, with its protocol (D2). Off: nothing is launched and nothing is reported.
/// </summary>
public sealed class ShardWorker : IHostedService, IAsyncDisposable
{
    /// <summary>The names llama.cpp has shipped the worker under: the Windows/winget release, then a source build's.</summary>
    public static readonly IReadOnlyList<string> Names = OperatingSystem.IsWindows()
        ? ["ggml-rpc-server.exe", "rpc-server.exe"]
        : ["ggml-rpc-server", "rpc-server"];

    private readonly ShardOptions options;
    private readonly string? executable;
    private readonly string? problem;
    private readonly TimeProvider time;
    private readonly EngineProcess? process;
    private readonly object sync = new();
    private readonly List<string> devices = [];
    private string? protocol;
    private bool announced;
    private bool collecting;
    private string? complaint;
    private readonly ShardTunnel? tunnel;

    public ShardWorker(IOptions<NodeOptions> node, IOptions<BackendOptions> backend, ILogger<ShardWorker> logger, ShardTunnel? tunnel = null, TimeProvider? time = null)
        : this(node.Value.Shards, backend.Value, logger, time, File.Exists, Environment.GetEnvironmentVariable("PATH"), tunnel)
    {
    }

    internal ShardWorker(ShardOptions options, BackendOptions backend, ILogger logger, TimeProvider? time, Func<string, bool> exists, string? path, ShardTunnel? tunnel = null)
    {
        this.options = options;
        this.time = time ?? TimeProvider.System;
        this.tunnel = tunnel;

        if (tunnel is not null)
        {
            // Phase 135: a peer joined or a round trip was measured — the hub hears it with the rest of the state.
            tunnel.Changed += () => Changed?.Invoke();
        }

        if (!options.Enabled)
        {
            return;
        }

        executable = Locate(options, backend, exists, path);
        problem = Refusal(options, executable, exists);
        process = new EngineProcess(
            "shards",
            StartInfo,
            () => problem,
            this.time,
            logger,
            limitGroup: null,
            onLine: Read,
            onExit: Exited);
    }

    /// <summary>The worker announced itself, stopped, or failed: what the hub shows changed.</summary>
    public event Action? Changed;

    public bool Enabled => options.Enabled;

    /// <summary>The worker's executable as found (134 D1); a head looks for <c>llama-server</c> beside it (136).</summary>
    public string? Executable => executable;

    private Func<IReadOnlyList<ShardGroupState>>? groups;

    /// <summary>Phase 136: the groups this node heads go out with its state, and their changes with its changes.</summary>
    public void AttachGroups(ShardGroupHost host)
    {
        groups = host.State;
        host.Changed += () => Changed?.Invoke();
    }

    /// <summary>What this node offers groups, for its own estimate and the group estimate (D4). Null when nothing is offered.</summary>
    public ShardOffer? Offer()
    {
        if (!options.Enabled || options.RamMiB + options.VramMiB <= 0)
        {
            return null;
        }

        lock (sync)
        {
            return new ShardOffer(options.RamMiB, options.VramMiB, protocol);
        }
    }

    public NodeShardState State(string nodeId)
    {
        if (!options.Enabled)
        {
            return new NodeShardState(nodeId, NodeShardState.Off, 0, 0, null, null, [], null, time.GetUtcNow());
        }

        lock (sync)
        {
            var state = process is null || process.Failed || problem is not null
                ? NodeShardState.Failed
                : announced
                    ? NodeShardState.Running
                    : process.IsRunning && process.LastError is null ? NodeShardState.Starting : NodeShardState.Failed;

            return new NodeShardState(
                nodeId,
                state,
                options.RamMiB,
                options.VramMiB,
                $"127.0.0.1:{options.Port}",
                protocol,
                devices.ToArray(),
                state == NodeShardState.Running ? options.CommunityProblem : problem ?? Said(process?.LastError) ?? (state == NodeShardState.Starting ? null : "not started"),
                time.GetUtcNow(),
                tunnel?.State(),
                tunnel?.Peers(),
                groups?.Invoke() is { Count: > 0 } heading ? heading : null,
                options.JoinsCommunity);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (process is null)
        {
            return Task.CompletedTask;
        }

        process.Start();
        watching = new CancellationTokenSource();
        watch = Task.Run(() => WatchAsync(watching.Token));
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (watching is { } cts)
        {
            await cts.CancelAsync();

            try
            {
                await (watch ?? Task.CompletedTask);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (process is not null)
        {
            await process.StopAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        watching?.Dispose();
    }

    /// <summary>
    /// D2: until the worker has answered, ask it what a head would ask first — every two seconds while it runs. Once it
    /// has, it is not asked again: a head connected to it holds its one connection, and a second caller would wait, not
    /// fail. Its exit is what takes "running" back.
    /// </summary>
    private async Task WatchAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            bool ask;

            lock (sync)
            {
                ask = !announced && process is { IsRunning: true };
            }

            if (ask && await HelloAsync(options.Port, TimeSpan.FromSeconds(3), token) is { } version)
            {
                lock (sync)
                {
                    protocol = version;
                    announced = true;
                }

                Changed?.Invoke();
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), time, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// llama.cpp's RPC handshake on loopback, as a head sends it: <c>HELLO</c> (command 14 — "must be always 14" in the
    /// source), 24 bytes of connection capabilities; the answer's first three bytes are the protocol. Measured: b10424
    /// answers 5.0.0, b12182 8.0.1. Null when nothing answers, or answers something else.
    /// </summary>
    public static async Task<string?> HelloAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, deadline.Token);
            var stream = client.GetStream();

            var request = new byte[1 + 8 + CapsSize];
            request[0] = HelloCommand;
            BitConverter.TryWriteBytes(request.AsSpan(1, 8), (ulong)CapsSize);
            await stream.WriteAsync(request, deadline.Token);

            var answer = new byte[8 + 4 + CapsSize];
            await stream.ReadExactlyAsync(answer, deadline.Token);

            return BitConverter.ToUInt64(answer, 0) == (ulong)(4 + CapsSize)
                ? $"{answer[8]}.{answer[9]}.{answer[10]}"
                : null;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or OperationCanceledException or EndOfStreamException)
        {
            return null;
        }
    }

    private const byte HelloCommand = 14;

    // RPC_CONN_CAPS_SIZE in llama.cpp's ggml-rpc/transport.h: 24 in b10424 and in b12182.
    private const int CapsSize = 24;

    private CancellationTokenSource? watching;
    private Task? watch;

    /// <summary>
    /// <c>Node:Shards:Executable</c>, else a worker beside a llama.cpp engine's <c>llama-server</c> (its directory, or the
    /// directory on PATH where a bare name resolves). Null when none is found.
    /// </summary>
    public static string? Locate(ShardOptions options, BackendOptions backend, Func<string, bool> exists, string? path)
    {
        if (!string.IsNullOrWhiteSpace(options.Executable))
        {
            return options.Executable.Trim();
        }

        var servers = backend.Engines.Values
            .Where(e => e.NormalizedType() == BackendOptions.LlamaCpp && !string.IsNullOrWhiteSpace(e.Serve.Executable))
            .Select(e => e.Serve.Executable.Trim());

        foreach (var server in servers)
        {
            var directories = Path.IsPathRooted(server) || server.Contains('/') || server.Contains('\\')
                ? [Path.GetDirectoryName(Path.GetFullPath(server))]
                : (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(dir => Candidates(server).Any(name => exists(Path.Combine(dir, name))));

            foreach (var directory in directories.OfType<string>())
            {
                if (Names.Select(name => Path.Combine(directory, name)).FirstOrDefault(exists) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>Why the worker cannot run as configured, in a sentence (95 D3: one error, no process), or null.</summary>
    public static string? Refusal(ShardOptions options, string? executable, Func<string, bool>? exists = null)
    {
        if (options.RamMiB + options.VramMiB <= 0)
        {
            return "Node:Shards offers nothing: set RamGiB or VramGiB to the GiB this machine gives groups";
        }

        if (options.Port is < 1 or > 65535)
        {
            return $"Node:Shards:Port must be 1–65535 (got {options.Port})";
        }

        if (executable is null)
        {
            return $"no llama.cpp RPC worker found: set Node:Shards:Executable, or put {string.Join(" or ", Names)} beside a llama.cpp engine's llama-server";
        }

        var rooted = Path.IsPathRooted(executable) || executable.Contains('/') || executable.Contains('\\');

        return rooted && !(exists ?? File.Exists)(executable)
            ? $"Node:Shards:Executable '{executable}' does not exist"
            : null;
    }

    /// <summary>The arguments the worker runs with (D1: the host is always loopback; there is no key to change it).</summary>
    public static IReadOnlyList<string> Arguments(ShardOptions options)
    {
        var args = new List<string> { "-H", "127.0.0.1", "-p", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) };

        if (options.Threads is > 0 and var threads)
        {
            args.AddRange(["-t", threads.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        if (!string.IsNullOrWhiteSpace(options.Devices))
        {
            args.AddRange(["-d", options.Devices.Trim()]);
        }

        if (options.Cache)
        {
            args.Add("-c");
        }

        return args;
    }

    /// <summary>One line of the worker's output (D2): its protocol, then its devices.</summary>
    internal void Read(string line)
    {
        var changed = false;

        lock (sync)
        {
            if (ShardBanner.Protocol(line) is { } version)
            {
                // A relaunch prints the banner again; the device list is the new one. The banner says what it offers, not
                // that it listens: it is printed before the socket, and on Windows a pipe holds it until the worker exits.
                protocol ??= version;
                devices.Clear();
                collecting = true;
            }
            else if (collecting && ShardBanner.Device(line) is { } device)
            {
                // A later "transport" line ends the list (b10424, b12182).
                devices.Add(device);
                changed = true;
            }
            else if (collecting && line.TrimStart().StartsWith("transport", StringComparison.Ordinal))
            {
                collecting = false;
            }
            else if (line.Contains("fail", StringComparison.OrdinalIgnoreCase) || line.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                // Its own words for why it stopped — "Failed to create server socket" on a port Windows reserved (found
                // live: 50172–50271 here) says more than "exited with code 1".
                complaint = line.Trim();
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    internal void Exited()
    {
        lock (sync)
        {
            announced = false;
            collecting = false;
        }

        Changed?.Invoke();
    }

    private string? Said(string? exit)
    {
        lock (sync)
        {
            return complaint is null ? exit : exit is null ? complaint : $"{complaint} ({exit})";
        }
    }

    /// <summary>Called by <see cref="EngineProcess"/> once per launch, so a relaunch forgets the last run's complaint.</summary>
    private ProcessStartInfo StartInfo()
    {
        lock (sync)
        {
            complaint = null;
        }

        var info = new ProcessStartInfo(executable ?? Names[0])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in Arguments(options))
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    private static IEnumerable<string> Candidates(string server) =>
        OperatingSystem.IsWindows() && !server.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? [server + ".exe", server]
            : [server];
}
