using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using InferHub.Node.Configuration;
using InferHub.Shared.Fit;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends.Shards;

/// <summary>
/// Phase 136: the groups this node heads. The coordinator sends the list (D1); for each, once every member's forward is open,
/// this node runs <c>llama-server</c> over its own worker and its tunnels, every layer on a worker, each worker's share in
/// proportion to its offer (D3), supervised as any engine (95). Only on a node whose owner allowed it (D2).
/// </summary>
public sealed class ShardGroupHost : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan HealthEvery = TimeSpan.FromSeconds(2);

    private readonly ShardOptions options;
    private readonly string? modelsDir;
    private readonly Func<string?> executable;
    private readonly Func<IReadOnlyList<ShardPeerState>> peers;
    private readonly Func<string, int, IInferenceBackend> backendFor;
    private readonly Func<string, Task<bool>> healthy;
    private readonly ILogger logger;
    private readonly TimeProvider time;
    private readonly object sync = new();
    private readonly Dictionary<string, Running> running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Running> stopping = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Phase 137 D3: how long a stopping group waits for its requests in flight before it stops anyway.</summary>
    internal TimeSpan DrainFor { get; init; } = TimeSpan.FromSeconds(60);
    private IReadOnlyList<ShardGroupSpec> desired = [];
    private CancellationTokenSource? life;
    private Task? watch;

    public ShardGroupHost(
        IOptions<NodeOptions> node,
        IOptions<BackendOptions> backend,
        ShardWorker worker,
        ShardTunnel tunnel,
        IServiceProvider services,
        ILogger<ShardGroupHost> logger,
        TimeProvider? time = null)
        : this(
            node.Value.Shards,
            Blank(node.Value.Shards.ModelsDir) ?? LlamaEngine(backend.Value)?.Serve.ModelsDir,
            () => HeadExecutable(node.Value.Shards, backend.Value, worker.Executable),
            tunnel.Peers,
            (name, port) => Compose(services, name, port),
            port => HealthyAsync(services, port),
            logger,
            time)
    {
        tunnel.Changed += Converge;
        HeadName = string.IsNullOrWhiteSpace(node.Value.Name) ? "head" : node.Value.Name.Trim();
        worker.AttachGroups(this);
    }

    internal ShardGroupHost(
        ShardOptions options,
        string? modelsDir,
        Func<string?> executable,
        Func<IReadOnlyList<ShardPeerState>> peers,
        Func<string, int, IInferenceBackend> backendFor,
        Func<string, Task<bool>> healthy,
        ILogger logger,
        TimeProvider? time)
    {
        this.options = options;
        this.modelsDir = modelsDir;
        this.executable = executable;
        this.peers = peers;
        this.backendFor = backendFor;
        this.healthy = healthy;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>A group started, answered, failed or stopped: the hub's view and the model list changed.</summary>
    public event Action? Changed;

    /// <summary>Whether a group has ever answered here — from then on the model list is said even when the own engine cannot list.</summary>
    public bool Answered { get; set; }

    /// <summary>D2: this node may head a group — it offers memory and its owner said so.</summary>
    public bool Enabled => options.Enabled && options.AllowHead;

    public IReadOnlyList<ShardGroupState> State()
    {
        lock (sync)
        {
            var draining = stopping.Values.Select(g => g.State()).ToArray();
            var wanted = desired
                .Where(spec => running.ContainsKey(spec.Name) || !stopping.ContainsKey(spec.Name))
                .Select(spec => running.TryGetValue(spec.Name, out var group)
                    ? group.State()
                    : new ShardGroupState(spec.Name, spec.Model, ShardGroupState.Waiting, spec.Members.Select(m => m.Name).ToArray(), null, Waiting(spec)));
            return [.. draining, .. wanted];
        }
    }

    /// <summary>
    /// Phase 137 D3: a request for a running group, counted until it is done, so a stop can wait for it. Null when the model is
    /// not a group that takes new requests here (none, starting, or draining).
    /// </summary>
    public Lease? Acquire(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        lock (sync)
        {
            if (!running.TryGetValue(model.Trim(), out var group) || !group.Healthy)
            {
                return null;
            }

            Interlocked.Increment(ref group.InFlight);
            return new Lease(group.Backend, () => Interlocked.Decrement(ref group.InFlight));
        }
    }

    /// <summary>Why a group by this name takes no request here now (draining, starting), or null when it is not one of ours.</summary>
    public string? Unavailable(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        lock (sync)
        {
            if (stopping.TryGetValue(model.Trim(), out var leaving))
            {
                return $"group '{leaving.Spec.Name}' is draining on this head: it finishes its requests in flight and stops";
            }

            return running.TryGetValue(model.Trim(), out var group) && !group.Healthy
                ? $"group '{group.Spec.Name}' is starting on this head"
                : null;
        }
    }

    /// <summary>One request's hold on a group: release it when the request is done.</summary>
    public sealed class Lease(IInferenceBackend backend, Action release) : IDisposable
    {
        private int released;

        public IInferenceBackend Backend { get; } = backend;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                release();
            }
        }
    }

    /// <summary>The backend of a running group by the model name a request carries; null when no such group runs here.</summary>
    public IInferenceBackend? BackendFor(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        lock (sync)
        {
            return running.TryGetValue(model.Trim(), out var group) && group.Healthy ? group.Backend : null;
        }
    }

    /// <summary>The models the running groups answer as — chat models in this node's list (D4).</summary>
    public IReadOnlyList<string> Models()
    {
        lock (sync)
        {
            return running.Values.Where(g => g.Healthy).Select(g => g.Spec.Name).ToArray();
        }
    }

    /// <summary>D1: the hub's list, whole. Groups not in it stop; new or changed ones start when their members' doors are open.</summary>
    public void SetGroups(ShardGroupList list)
    {
        if (!Enabled)
        {
            logger.LogWarning("The coordinator sent {Count} group(s) for this node to head, and Node:Shards:AllowHead is off; none runs", list.Groups.Count);
            return;
        }

        lock (sync)
        {
            desired = list.Groups;
        }

        Converge();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return Task.CompletedTask;
        }

        life = new CancellationTokenSource();
        watch = Task.Run(() => WatchAsync(life.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (life is not null)
        {
            await life.CancelAsync();

            try
            {
                await (watch ?? Task.CompletedTask);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Running[] all;

        lock (sync)
        {
            all = [.. running.Values, .. stopping.Values];
            stopping.Clear();
            running.Clear();
        }

        foreach (var group in all)
        {
            await group.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        life?.Dispose();
    }

    /// <summary>
    /// The arguments for one group's <c>llama-server</c> (D3): the model, its own worker then one forward per member, every
    /// layer on those devices in that order, the shares by weight, the context, loopback, the group's name as the alias.
    /// </summary>
    public static IReadOnlyList<string> Arguments(ShardGroupSpec spec, string modelPath, IReadOnlyList<string> endpoints, int port, int? threads)
    {
        var args = new List<string>
        {
            "-m", modelPath,
            "--rpc", string.Join(',', endpoints),
            "--device", string.Join(',', endpoints.Select((_, i) => "RPC" + i.ToString(CultureInfo.InvariantCulture))),
            "-ngl", "999",
            "--tensor-split", string.Join(',', new[] { spec.HeadWeight }.Concat(spec.Members.Select(m => m.Weight)).Select(w => Math.Max(1, w).ToString(CultureInfo.InvariantCulture))),
            "-c", Math.Max(512, spec.Context).ToString(CultureInfo.InvariantCulture),
            "--host", "127.0.0.1",
            "--port", port.ToString(CultureInfo.InvariantCulture),
            "--alias", spec.Name
        };

        if (threads is > 0)
        {
            args.AddRange(["-t", threads.Value.ToString(CultureInfo.InvariantCulture)]);
        }

        return args;
    }

    /// <summary>D2: the file a name points at in the models directory, or why it cannot be used. A path is never accepted.</summary>
    public static (string? Path, string? Problem) ModelFile(string? directory, string model, Func<string, bool>? exists = null)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Contains('/') || model.Contains('\\') || model.Contains("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(model))
        {
            return (null, $"'{model}' is not a file name: a group's model is named, never given as a path");
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            return (null, "no models directory: set Node:Shards:ModelsDir (or the llama.cpp engine's Serve:ModelsDir)");
        }

        var path = System.IO.Path.Combine(directory, model);
        return (exists ?? File.Exists)(path) ? (path, null) : (null, $"'{model}' is not in {directory}");
    }

    private void Converge()
    {
        if (!Enabled)
        {
            return;
        }

        var stop = new List<Running>();
        var changed = false;

        lock (sync)
        {
            var doors = peers().Where(p => p.Local is not null).ToDictionary(p => p.NodeId, p => p.Local!, StringComparer.OrdinalIgnoreCase);

            foreach (var name in running.Keys.ToArray())
            {
                var spec = desired.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                var endpoints = spec is null ? null : Endpoints(spec, doors);

                if (spec is null || endpoints is null || running[name].Spec != spec || !running[name].Endpoints.SequenceEqual(endpoints))
                {
                    running[name].Draining = true;
                    stopping[name] = running[name];
                    stop.Add(running[name]);
                    running.Remove(name);

                    changed = true;
                }
            }

            foreach (var spec in desired)
            {
                // D3 (137): the same name starts again only once the old process has stopped — two llama-servers on one
                // worker would hold the layers twice.
                if (running.ContainsKey(spec.Name) || stopping.ContainsKey(spec.Name) || Endpoints(spec, doors) is not { } endpoints)
                {
                    continue;
                }

                running[spec.Name] = Start(spec, endpoints);
                changed = true;
            }
        }

        foreach (var group in stop)
        {
            logger.LogInformation("Stopping group {Group}: the coordinator's list or a member's door changed; {InFlight} request(s) in flight finish first", group.Spec.Name, group.InFlight);
            _ = Task.Run(() => DrainAsync(group));
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// D3 (137): a stopping group takes no new request (it is out of <c>running</c>), finishes those in flight — up to
    /// <see cref="DrainFor"/> — and only then stops its process; then the same name may start again.
    /// </summary>
    private async Task DrainAsync(Running group)
    {
        var deadline = time.GetUtcNow() + DrainFor;

        while (Volatile.Read(ref group.InFlight) > 0 && time.GetUtcNow() < deadline && group.Process.IsRunning && !group.Process.Failed)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), time, life?.Token ?? CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (Volatile.Read(ref group.InFlight) > 0)
        {
            logger.LogWarning("Group {Group} stops with {InFlight} request(s) still in flight after {Seconds} s", group.Spec.Name, group.InFlight, DrainFor.TotalSeconds);
        }

        await group.DisposeAsync();

        lock (sync)
        {
            if (stopping.TryGetValue(group.Spec.Name, out var same) && ReferenceEquals(same, group))
            {
                stopping.Remove(group.Spec.Name);
            }
        }

        logger.LogInformation("Group {Group} stopped", group.Spec.Name);
        Changed?.Invoke();
        Converge();
    }

    /// <summary>The head's own worker, then each member's local forward — null while a member's forward is not open (D3).</summary>
    private IReadOnlyList<string>? Endpoints(ShardGroupSpec spec, IReadOnlyDictionary<string, string> doors)
    {
        var list = new List<string> { $"127.0.0.1:{options.Port}" };

        foreach (var member in spec.Members)
        {
            if (!doors.TryGetValue(member.NodeId, out var door))
            {
                return null;
            }

            list.Add(door);
        }

        return list;
    }

    private string Waiting(ShardGroupSpec spec)
    {
        var doors = peers().Where(p => p.Local is not null).Select(p => p.NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = spec.Members.Where(m => !doors.Contains(m.NodeId)).Select(m => m.Name).ToArray();
        return missing.Length > 0 ? $"no tunnel to {string.Join(", ", missing)} yet" : "starting";
    }

    private Running Start(ShardGroupSpec spec, IReadOnlyList<string> endpoints)
    {
        var port = FreePort();
        var (path, problem) = ModelFile(modelsDir, spec.Model);
        var exe = executable();
        problem ??= exe is null ? "no llama-server found: set Node:Shards:HeadExecutable, or put llama-server beside the worker" : null;
        var args = path is null ? [] : Arguments(spec, path, endpoints, port, options.Threads);

        var process = new EngineProcess(
            "group:" + spec.Name,
            () =>
            {
                var info = new ProcessStartInfo(exe ?? "llama-server")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                foreach (var arg in args)
                {
                    info.ArgumentList.Add(arg);
                }

                return info;
            },
            () => problem,
            time,
            logger,
            limitGroup: null);

        logger.LogInformation("Heading group {Group}: {Model} over {Endpoints}", spec.Name, spec.Model, string.Join(", ", endpoints));
        process.Start();
        var group = new Running(spec, endpoints, port, process, backendFor(spec.Name, port));

        if (path is not null)
        {
            // D4 (137): which worker holds which layers, by llama.cpp's own rule over the same split — the header is read once.
            _ = Task.Run(async () =>
            {
                group.Layers = await LayersAsync(spec, path);
                Changed?.Invoke();
            });
        }

        return group;
    }

    /// <summary>
    /// D4 (137): the model's block count from its GGUF header, then <see cref="ShardLayers.Split"/> over the weights the head's
    /// <c>--tensor-split</c> carries, in the same order — the head's own worker first. Null when the header cannot be read.
    /// </summary>
    internal async Task<IReadOnlyList<ShardGroupLayers>?> LayersAsync(ShardGroupSpec spec, string path)
    {
        try
        {
            await using var file = File.OpenRead(path);
            var header = await GgufHeader.ReadAsync(file);
            var blocks = header.Values.FirstOrDefault(v => v.Key.EndsWith(".block_count", StringComparison.Ordinal)).Value;

            if (blocks is null || !int.TryParse(Convert.ToString(blocks, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var layers))
            {
                return null;
            }

            var names = new[] { HeadName }.Concat(spec.Members.Select(m => m.Name)).ToArray();
            var split = ShardLayers.Split(layers, new[] { spec.HeadWeight }.Concat(spec.Members.Select(m => m.Weight)).ToArray());
            return split.Select((s, i) => new ShardGroupLayers(names[i], s.First, s.Count, s.Output)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or EndOfStreamException or UnauthorizedAccessException)
        {
            logger.LogInformation(ex, "Could not read the layer count of {Model}; the console shows no layers for group {Group}", spec.Model, spec.Name);
            return null;
        }
    }

    /// <summary>How the head names itself among a group's layers: its node name.</summary>
    internal string HeadName { get; init; } = "head";

    private async Task WatchAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Running[] all;

            lock (sync)
            {
                all = running.Values.ToArray();
            }

            var changed = false;

            foreach (var group in all)
            {
                var ok = group.Process.IsRunning && await healthy($"http://127.0.0.1:{group.Port}/health");

                if (ok != group.Healthy)
                {
                    group.Healthy = ok;
                    changed = true;
                    logger.LogInformation("Group {Group} {State}", group.Spec.Name, ok ? "answers" : "stopped answering");
                }
            }

            if (changed)
            {
                Changed?.Invoke();
            }

            try
            {
                await Task.Delay(HealthEvery, time, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
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

    private static EngineOptions? LlamaEngine(BackendOptions backend) =>
        backend.Engines.Values.FirstOrDefault(e => e.NormalizedType() == BackendOptions.LlamaCpp);

    /// <summary><c>HeadExecutable</c>, else the llama.cpp engine's <c>llama-server</c> when it is a path, else the one beside the worker.</summary>
    public static string? HeadExecutable(ShardOptions options, BackendOptions backend, string? worker)
    {
        if (Blank(options.HeadExecutable) is { } configured)
        {
            return configured;
        }

        if (LlamaEngine(backend)?.Serve.Executable is { } engine && (engine.Contains('/') || engine.Contains('\\')))
        {
            return engine.Trim();
        }

        if (worker is null || System.IO.Path.GetDirectoryName(worker) is not { } directory)
        {
            return null;
        }

        var beside = System.IO.Path.Combine(directory, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
        return File.Exists(beside) ? beside : null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>D4: the same shape a llama.cpp engine has (95/96) — an upstream at its <c>/v1</c>, llama.cpp's own routes at the root.</summary>
    private static IInferenceBackend Compose(IServiceProvider services, string name, int port)
    {
        var factory = services.GetRequiredService<IHttpClientFactory>();
        var upstream = new UpstreamBackend(
            factory,
            Options.Create(new BackendOptions { Type = BackendOptions.LlamaCpp }),
            Options.Create(new UpstreamBackendOptions { BaseUrl = $"http://127.0.0.1:{port}/v1", TimeoutSeconds = 600 }),
            services.GetRequiredService<ILogger<UpstreamBackend>>(),
            [Shared.Contracts.CapabilityKinds.Chat]);

        return new LlamaCppBackend(
            upstream,
            new EngineOptions { Type = BackendOptions.LlamaCpp, TimeoutSeconds = 600 },
            router: false,
            () =>
            {
                var http = factory.CreateClient(UpstreamBackend.HttpClientName);
                http.BaseAddress = new Uri($"http://127.0.0.1:{port}/");
                http.Timeout = TimeSpan.FromMinutes(10);
                return http;
            },
            services.GetService<TimeProvider>() ?? TimeProvider.System,
            services.GetRequiredService<ILogger<ShardGroupHost>>());
    }

    private static async Task<bool> HealthyAsync(IServiceProvider services, string url)
    {
        try
        {
            using var http = services.GetRequiredService<IHttpClientFactory>().CreateClient();
            http.Timeout = TimeSpan.FromSeconds(3);
            using var response = await http.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private sealed class Running(ShardGroupSpec spec, IReadOnlyList<string> endpoints, int port, EngineProcess process, IInferenceBackend backend) : IAsyncDisposable
    {
        public ShardGroupSpec Spec { get; } = spec;
        public IReadOnlyList<string> Endpoints { get; } = endpoints;
        public int Port { get; } = port;
        public EngineProcess Process { get; } = process;
        public IInferenceBackend Backend { get; } = backend;
        public volatile bool Healthy;

        /// <summary>Phase 137 D3: requests holding a lease on this group now.</summary>
        public int InFlight;

        /// <summary>Phase 137 D3: out of the routes, finishing what it has.</summary>
        public volatile bool Draining;

        /// <summary>Phase 137 D4: which worker holds which layers, once the model's header has been read.</summary>
        public volatile IReadOnlyList<ShardGroupLayers>? Layers;

        public ShardGroupState State()
        {
            var state = Draining
                ? ShardGroupState.Draining
                : Healthy ? ShardGroupState.Running : Process.Failed ? ShardGroupState.Failed : ShardGroupState.Starting;
            var why = Draining ? $"finishing {InFlight} request(s), then stopping" : Healthy ? null : Process.LastError;
            return new ShardGroupState(Spec.Name, Spec.Model, state, Spec.Members.Select(m => m.Name).ToArray(), $"127.0.0.1:{Port}", why, Layers);
        }

        public async ValueTask DisposeAsync() => await Process.DisposeAsync();
    }
}

/// <summary>
/// Phase 136 D4: on a head, a request for a group's name goes to that group's <c>llama-server</c>; everything else to the
/// node's own backend, untouched. The groups' names are added to the model list as chat models.
/// </summary>
public sealed class ShardGroupBackend(IInferenceBackend inner, ShardGroupHost groups) : IInferenceBackend, IModelKinds
{
    public string Name => inner.Name;

    public string Endpoint => inner.Endpoint;

    public IReadOnlyList<string> Kinds => inner.Kinds.Contains(Shared.Contracts.CapabilityKinds.Chat) ? inner.Kinds : [.. inner.Kinds, Shared.Contracts.CapabilityKinds.Chat];

    public bool SupportsModelManagement => inner.SupportsModelManagement;

    public bool SupportsPull => inner.SupportsPull;

    public int? ModelSlots => inner.ModelSlots;

    public async Task<IReadOnlyList<Shared.Contracts.ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken)
    {
        var own = await inner.ListModelsAsync(cancellationToken);
        var extra = groups.Models().Select(name => new Shared.Contracts.ModelInfo(name, ShardGroupState.Digest, null)).ToArray();

        if (extra.Length > 0)
        {
            groups.Answered = true;
        }

        // Found in the lab: an own engine that cannot list returns null, and the node then leaves the hub's inventory alone
        // (69) — so a group that stopped stayed in the catalogue. Once this node has served a group, its list is said even
        // while its own engine is down: the groups it runs now, possibly none. Its own models come back with its engine.
        return own is null && !groups.Answered ? null : [.. own ?? [], .. extra];
    }

    // Phase 137 D3: a group's request holds a lease until it is done, so a stopping group can wait for it.
    public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken) =>
        Leased(requestJson, (backend, json) => backend.GenerateAsync(json, cancellationToken));

    public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken) =>
        Leased(requestJson, (backend, json) => backend.ChatAsync(json, cancellationToken));

    public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken) =>
        Leased(requestJson, (backend, json) => backend.EmbedAsync(json, cancellationToken));

    public async IAsyncEnumerable<string> StreamAsync(string kind, string requestJson, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var lease = groups.Acquire(ModelOf(requestJson));

        await foreach (var chunk in (lease?.Backend ?? Refused(requestJson) ?? inner).StreamAsync(kind, requestJson, cancellationToken))
        {
            yield return chunk;
        }
    }

    private async Task<string> Leased(string requestJson, Func<IInferenceBackend, string, Task<string>> call)
    {
        using var lease = groups.Acquire(ModelOf(requestJson));
        return await call(lease?.Backend ?? Refused(requestJson) ?? inner, requestJson);
    }

    /// <summary>
    /// A request for a group this head is draining or still starting: refused here with the reason, rather than handed to
    /// the node's own engine, which has never heard of the name. Null for any other model.
    /// </summary>
    private IInferenceBackend? Refused(string requestJson) =>
        groups.Unavailable(ModelOf(requestJson)) is { } why
            ? throw new InvalidOperationException(why)
            : null;

    public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken) =>
        inner.PullAsync(model, cancellationToken);

    public Task DeleteAsync(string model, CancellationToken cancellationToken) => inner.DeleteAsync(model, cancellationToken);

    public Task WarmAsync(string model, CancellationToken cancellationToken) =>
        groups.BackendFor(model) is not null ? Task.CompletedTask : inner.WarmAsync(model, cancellationToken);

    public Task UnloadAsync(string model, CancellationToken cancellationToken) => inner.UnloadAsync(model, cancellationToken);

    public IReadOnlyList<string>? KindsFor(string model) =>
        groups.BackendFor(model) is not null ? [Shared.Contracts.CapabilityKinds.Chat] : (inner as IModelKinds)?.KindsFor(model);



    internal static string? ModelOf(string requestJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(requestJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("model", out var model)
                   && model.ValueKind == System.Text.Json.JsonValueKind.String
                ? model.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
