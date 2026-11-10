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
            return desired.Select(spec => running.TryGetValue(spec.Name, out var group)
                    ? group.State()
                    : new ShardGroupState(spec.Name, spec.Model, ShardGroupState.Waiting, spec.Members.Select(m => m.Name).ToArray(), null, Waiting(spec)))
                .ToArray();
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
            all = running.Values.ToArray();
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
                    stop.Add(running[name]);
                    running.Remove(name);
                    changed = true;
                }
            }

            foreach (var spec in desired)
            {
                if (running.ContainsKey(spec.Name) || Endpoints(spec, doors) is not { } endpoints)
                {
                    continue;
                }

                running[spec.Name] = Start(spec, endpoints);
                changed = true;
            }
        }

        foreach (var group in stop)
        {
            logger.LogInformation("Stopping group {Group}: the coordinator's list or a member's door changed", group.Spec.Name);
            _ = group.DisposeAsync().AsTask();
        }

        if (changed)
        {
            Changed?.Invoke();
        }
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
        return new Running(spec, endpoints, port, process, backendFor(spec.Name, port));
    }

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

        public ShardGroupState State()
        {
            var state = Healthy
                ? ShardGroupState.Running
                : Process.Failed ? ShardGroupState.Failed : ShardGroupState.Starting;
            return new ShardGroupState(Spec.Name, Spec.Model, state, Spec.Members.Select(m => m.Name).ToArray(), $"127.0.0.1:{Port}",
                Healthy ? null : Process.LastError);
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

    public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken) =>
        Pick(requestJson).GenerateAsync(requestJson, cancellationToken);

    public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken) =>
        Pick(requestJson).ChatAsync(requestJson, cancellationToken);

    public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken) =>
        Pick(requestJson).EmbedAsync(requestJson, cancellationToken);

    public IAsyncEnumerable<string> StreamAsync(string kind, string requestJson, CancellationToken cancellationToken) =>
        Pick(requestJson).StreamAsync(kind, requestJson, cancellationToken);

    public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken) =>
        inner.PullAsync(model, cancellationToken);

    public Task DeleteAsync(string model, CancellationToken cancellationToken) => inner.DeleteAsync(model, cancellationToken);

    public Task WarmAsync(string model, CancellationToken cancellationToken) =>
        groups.BackendFor(model) is not null ? Task.CompletedTask : inner.WarmAsync(model, cancellationToken);

    public Task UnloadAsync(string model, CancellationToken cancellationToken) => inner.UnloadAsync(model, cancellationToken);

    public IReadOnlyList<string>? KindsFor(string model) =>
        groups.BackendFor(model) is not null ? [Shared.Contracts.CapabilityKinds.Chat] : (inner as IModelKinds)?.KindsFor(model);

    private IInferenceBackend Pick(string requestJson) => groups.BackendFor(ModelOf(requestJson)) ?? inner;

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
