using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Shared.Brio;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Backends;

/// <summary>
/// One configured engine under <c>Backend:Engines</c> (phase 95): the backend that talks to it, the
/// process that runs it when the node launches it, and what it last said it holds.
/// </summary>
public sealed class Engine(
    string name,
    string type,
    IInferenceBackend backend,
    bool autostart,
    ModelFilterOptions? models = null,
    EngineProcess? process = null,
    Func<IReadOnlyCollection<string>, CancellationToken, Task>? unload = null)
{
    private int inFlight;
    private volatile bool running;

    public string Name { get; } = name;

    public string Type { get; } = type;

    public IInferenceBackend Backend { get; } = backend;

    public bool Autostart { get; } = autostart;

    public ModelFilterOptions Models { get; } = models ?? new ModelFilterOptions();

    /// <summary>Null when the engine runs beside the node rather than under it.</summary>
    public EngineProcess? Process { get; } = process;

    /// <summary>Unloads what this node loaded (Ollama only), so stopping one frees the card.</summary>
    internal Func<IReadOnlyCollection<string>, CancellationToken, Task>? Unload { get; } = unload;

    /// <summary>Whether the node wants it running. The routing table only ever holds these.</summary>
    public bool Running => running;

    public int InFlight => Volatile.Read(ref inFlight);

    /// <summary>Null: not asked since it was started. False: asked and it did not answer.</summary>
    internal bool? Answering { get; set; }

    /// <summary>Whether it has answered once since it was last started — starting versus lost.</summary>
    internal bool AnsweredSinceStart { get; set; }

    internal IReadOnlyList<ModelInfo> LastModels { get; set; } = [];

    internal string? LastError { get; set; }

    /// <summary>Models this node sent work to, which is what a stop unloads — never the owner's others.</summary>
    internal ConcurrentDictionary<string, byte> Routed { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal void SetRunning(bool value) => running = value;

    internal int Enter() => Interlocked.Increment(ref inFlight);

    internal int Leave() => Interlocked.Decrement(ref inFlight);
}

/// <summary>
/// Several engines behind one <see cref="IInferenceBackend"/> (phase 95, D1): the rest of the node —
/// the mesh connection, solo mode, retrieval's embeddings, the model commands — holds one backend
/// exactly as it always has, and this routes each request by its <c>model</c> to the engine that
/// reported it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The routing key is the model name and nothing else</b> — the hub already routes on
/// <c>(kind, model)</c> and the node only has to finish the job. A name two engines both report goes
/// to the one whose name sorts first — .NET configuration hands a section's keys back sorted, so
/// "the order in the file" is not something this process can see — and that is logged once rather
/// than resolved by whichever answered last.
/// </para>
/// <para>
/// <b>A stopped engine leaves the routing table before anything else happens</b> (D5): new work
/// stops at once, in-flight work drains for <c>Backend:StopDrain</c>, and only then is a launched
/// process killed. Its models leave the next report, so the hub stops sending them here.
/// </para>
/// </remarks>
public sealed class MultiBackend : IInferenceBackend, IClosedSetScorer, IModelKinds, IEngineControl, IBackendToolJobs
{
    private static readonly TimeSpan ReadyPoll = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(15);

    private readonly IReadOnlyList<Engine> engines;
    private readonly TimeSpan stopDrain;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly SemaphoreSlim applyGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> loggedCollisions = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Engine> routes = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, bool>? lastOverrides;
    private readonly TaskCompletionSource firstApply = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MultiBackend(IReadOnlyList<Engine> engines, TimeSpan stopDrain, TimeProvider time, ILogger logger)
    {
        this.engines = engines;
        this.stopDrain = stopDrain;
        this.time = time;
        this.logger = logger;
    }

    public event Action? Changed;

    public IReadOnlyList<Engine> Engines => engines;

    public IReadOnlyList<string> EngineNames => engines.Select(e => e.Name).ToArray();

    /// <summary>What the fleet list shows: which engine is the node's, and where it is.</summary>
    public string Name => "engines";

    public string Endpoint
    {
        get
        {
            var running = engines.Where(e => e.Running).Select(e => $"{e.Name}={e.Backend.Endpoint}").ToArray();
            return running.Length == 0 ? "no engine running" : string.Join(", ", running);
        }
    }

    /// <summary>The union over running engines. Per model, <see cref="KindsFor"/> is the answer.</summary>
    public IReadOnlyList<string> Kinds => engines
        .Where(e => e.Running)
        .SelectMany(e => e.Backend.Kinds)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>The engine's answer, and per model where the engine has one (a llama.cpp router, 96 D2).</summary>
    public IReadOnlyList<string>? KindsFor(string model)
        => Resolve(model) is not { } engine
            ? null
            : engine.Backend is IModelKinds perModel
                ? perModel.KindsFor(model) ?? engine.Backend.Kinds
                : engine.Backend.Kinds;

    /// <summary>
    /// Ollama and a llama.cpp router pull, delete, warm and unload, so the node can when one is
    /// configured — declared, not discovered (26). A command for a stopped one is refused by
    /// <see cref="Manager"/> in a sentence.
    /// </summary>
    /// <remarks>
    /// <b>Found in phase 96's live run, wrong since 95:</b> this asked which engines were
    /// <em>running</em>, and a meshed node registers before its engines start (it waits for its
    /// profile, D4) — so the hub was told "cannot manage models" once, at registration, and never
    /// offered a pull to a node with an Ollama engine.
    /// </remarks>
    public bool SupportsModelManagement => engines.Any(e => e.Backend.SupportsModelManagement);

    public async Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken)
    {
        var running = engines.Where(e => e.Running).ToArray();

        if (running.Length == 0)
        {
            Publish();
            return [];
        }

        await Task.WhenAll(running.Select(e => ListOneAsync(e, cancellationToken)));
        Publish();

        // "Could not ask" is not "has none" (69): only when every running engine failed to answer is
        // the whole answer null. One that answered is a real inventory, and the one that did not has
        // its models withdrawn — routing to a dead engine is worse than not routing to it.
        if (running.All(e => e.Answering is false))
        {
            return null;
        }

        return CurrentModels();
    }

    public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken)
        => RunAsync(requestJson, (backend, ct) => backend.GenerateAsync(requestJson, ct), cancellationToken);

    public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken)
        => RunAsync(requestJson, (backend, ct) => backend.ChatAsync(requestJson, ct), cancellationToken);

    public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken)
        => RunAsync(requestJson, (backend, ct) => backend.EmbedAsync(requestJson, ct), cancellationToken);

    public async IAsyncEnumerable<string> StreamAsync(
        string kind,
        string requestJson,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (engine, model) = await RouteAsync(requestJson, cancellationToken);
        engine.Enter();

        try
        {
            Remember(engine, model);

            await foreach (var chunk in engine.Backend.StreamAsync(kind, requestJson, cancellationToken))
            {
                yield return chunk;
            }
        }
        finally
        {
            engine.Leave();
        }
    }

    public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken)
        => PullAsync(model, engine: null, cancellationToken);

    public Task DeleteAsync(string model, CancellationToken cancellationToken)
        => DeleteAsync(model, engine: null, cancellationToken);

    public Task WarmAsync(string model, CancellationToken cancellationToken)
        => WarmAsync(model, engine: null, cancellationToken);

    public Task UnloadAsync(string model, CancellationToken cancellationToken)
        => UnloadAsync(model, engine: null, cancellationToken);

    /// <param name="engine"><c>ModelCommand.Engine</c> (96 D3); null picks as <see cref="Manager"/> says.</param>
    public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, string? engine, CancellationToken cancellationToken)
        => Manager(ModelCommand.KindPull, model, engine).Backend.PullAsync(model, cancellationToken);

    public Task DeleteAsync(string model, string? engine, CancellationToken cancellationToken)
        => Manager(ModelCommand.KindDelete, model, engine).Backend.DeleteAsync(model, cancellationToken);

    public Task WarmAsync(string model, string? engine, CancellationToken cancellationToken)
    {
        var target = Manager(ModelCommand.KindWarm, model, engine);
        Remember(target, model);
        return target.Backend.WarmAsync(model, cancellationToken);
    }

    public Task UnloadAsync(string model, string? engine, CancellationToken cancellationToken)
        => Manager(ModelCommand.KindUnload, model, engine).Backend.UnloadAsync(model, cancellationToken);

    /// <summary>
    /// Which engine a model command is for (96 D3): the one named; else, for anything but a pull, the
    /// one that reports the model; else the only running engine that can manage models. Two that
    /// could and no name is a refusal naming both — <c>owner/model</c> is an Ollama name and a Hugging
    /// Face repo, and a guess here downloads gigabytes into the wrong place.
    /// </summary>
    internal Engine Manager(string kind, string model, string? engine)
    {
        if (!string.IsNullOrWhiteSpace(engine))
        {
            var named = engines.FirstOrDefault(e => string.Equals(e.Name, engine.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"this node has no engine named '{engine}'; it has {string.Join(", ", EngineNames)}");

            if (!named.Running)
            {
                throw new InvalidOperationException($"engine '{named.Name}' is stopped; start it before you {kind} a model on it");
            }

            return named.Backend.SupportsModelManagement
                ? named
                : throw new NotSupportedException($"engine '{named.Name}' ({named.Type}) cannot manage models; Ollama and a llama.cpp router can");
        }

        if (kind != ModelCommand.KindPull && Resolve(model) is { } serving)
        {
            return serving.Backend.SupportsModelManagement
                ? serving
                : throw new NotSupportedException($"'{model}' is served by the {serving.Type} engine '{serving.Name}', which cannot {kind} models");
        }

        var managers = engines.Where(e => e.Running && e.Backend.SupportsModelManagement).ToArray();

        return managers.Length switch
        {
            1 => managers[0],
            0 => throw new NotSupportedException("no running engine on this node can manage models; Ollama and a llama.cpp router can"),
            _ => throw new InvalidOperationException(
                $"{managers.Length} engines on this node can {kind} '{model}' ({string.Join(", ", managers.Select(m => m.Name))}); name one with ?engine=<name>")
        };
    }

    /// <summary>A llama.cpp engine's native routes and reranker (96 D4/D5), by the model's engine.</summary>
    public bool Serves(string capability, string model)
        => Resolve(model) is { Backend: IBackendToolJobs jobs } && jobs.Serves(capability, model);

    public async Task<ToolResult> RunAsync(ToolJob job, CancellationToken cancellationToken)
    {
        if (Resolve(job.Model) is null)
        {
            await ListModelsAsync(cancellationToken);
        }

        if (Resolve(job.Model) is not { Backend: IBackendToolJobs jobs } engine || !jobs.Serves(job.Capability, job.Model))
        {
            return ToolResult.Refused(job.JobId, $"no running engine on this node serves '{job.Capability}' for '{job.Model}'", BrioErrorCodes.ModelNotFound);
        }

        engine.Enter();

        try
        {
            return await jobs.RunAsync(job, cancellationToken);
        }
        finally
        {
            engine.Leave();
        }
    }

    /// <summary>Brio (94 D1) goes to the running colibri engine, the only one with a <c>/v1/brio</c>.</summary>
    public async Task<ToolResult> ScoreAsync(ToolJob job, CancellationToken cancellationToken)
    {
        var engine = engines.FirstOrDefault(e => e.Running && e.Type == BackendOptions.Colibri);

        if (engine?.Backend is not IClosedSetScorer scorer)
        {
            return ToolResult.Failed(job.JobId, "no colibri engine is running on this node; only colibri can score a closed set");
        }

        engine.Enter();

        try
        {
            return await scorer.ScoreAsync(job, cancellationToken);
        }
        finally
        {
            engine.Leave();
        }
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(
        IReadOnlyDictionary<string, bool>? overrides,
        CancellationToken cancellationToken)
    {
        await applyGate.WaitAsync(cancellationToken);

        try
        {
            lastOverrides = overrides;
            firstApply.TrySetResult();
            var changes = new List<string>();

            foreach (var engine in engines)
            {
                var wanted = overrides is not null && overrides.TryGetValue(engine.Name, out var value)
                    ? value
                    : engine.Autostart;

                if (wanted == engine.Running)
                {
                    continue;
                }

                if (wanted)
                {
                    Start(engine);
                    changes.Add($"backend '{engine.Name}' started");
                }
                else
                {
                    await StopAsync(engine, cancellationToken);
                    changes.Add($"backend '{engine.Name}' stopped");
                }
            }

            return changes;
        }
        finally
        {
            applyGate.Release();
        }
    }

    /// <summary>
    /// The boot-time convergence. It re-applies whatever a profile last asked for rather than the
    /// bare autostart set, because the connection can deliver a profile before the host gets here.
    /// </summary>
    public Task<IReadOnlyList<string>> StartAsync(CancellationToken cancellationToken)
        => ApplyAsync(lastOverrides, cancellationToken);

    /// <summary>Completes on the first convergence — a profile (or "no profile") from the hub, or the boot.</summary>
    public Task FirstApplied => firstApply.Task;

    /// <summary>Stops every engine at host shutdown. Launched processes are killed without a drain.</summary>
    public async Task StopAllAsync()
    {
        foreach (var engine in engines)
        {
            engine.SetRunning(false);

            if (engine.Process is { } process)
            {
                await process.StopAsync();
            }
        }

        Publish();
    }

    public NodeBackendState State(string nodeId) => new(
        nodeId,
        engines.Select(e => new NodeEngineInfo(
            e.Name,
            e.Type,
            e.Backend.Endpoint,
            StateOf(e),
            e.Autostart,
            e.Process is not null,
            e.Backend.Kinds,
            e.Running ? e.LastModels.Select(m => m.Name).ToArray() : [],
            e.InFlight,
            e.Process?.LastError ?? e.LastError)).ToArray(),
        time.GetUtcNow());

    internal static string StateOf(Engine engine) => engine switch
    {
        { Running: false } => NodeEngineInfo.Stopped,
        { Process.Failed: true } => NodeEngineInfo.Failed,
        { Answering: true } => NodeEngineInfo.Running,
        // Not answered once since it was started: loading its weights, not lost.
        { AnsweredSinceStart: false } => NodeEngineInfo.Starting,
        _ => NodeEngineInfo.Unreachable
    };

    private void Start(Engine engine)
    {
        engine.Answering = null;
        engine.AnsweredSinceStart = false;
        engine.LastError = null;
        engine.SetRunning(true);
        engine.Process?.Start();

        logger.LogInformation("Engine '{Engine}' ({Type}) started.", engine.Name, engine.Type);

        // The hub learns of it the moment it answers rather than a refresh interval later.
        _ = Task.Run(() => WatchUntilReadyAsync(engine));
    }

    private async Task StopAsync(Engine engine, CancellationToken cancellationToken)
    {
        // D5: out of the routing table first, so nothing new lands on it while it drains.
        engine.SetRunning(false);
        Publish();
        Changed?.Invoke();

        var deadline = time.GetUtcNow() + stopDrain;

        while (engine.InFlight > 0 && time.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), time, cancellationToken);
        }

        if (engine.InFlight > 0)
        {
            logger.LogWarning(
                "Engine '{Engine}' still had {InFlight} request(s) in flight after {Drain}; stopping it under them.",
                engine.Name,
                engine.InFlight,
                stopDrain);
        }

        if (engine.Process is { } process)
        {
            await process.StopAsync();
        }

        if (engine.Unload is { } unload && !engine.Routed.IsEmpty)
        {
            var models = engine.Routed.Keys.ToArray();
            engine.Routed.Clear();

            try
            {
                await unload(models, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Engine '{Engine}' stopped, and unloading its models failed.", engine.Name);
            }
        }

        engine.LastModels = [];
        engine.Answering = null;
        logger.LogInformation("Engine '{Engine}' ({Type}) stopped.", engine.Name, engine.Type);
    }

    private async Task WatchUntilReadyAsync(Engine engine)
    {
        var deadline = time.GetUtcNow() + ReadyTimeout;

        while (engine.Running && time.GetUtcNow() < deadline)
        {
            if (engine.Process is { Failed: true })
            {
                Changed?.Invoke();
                return;
            }

            await ListOneAsync(engine, CancellationToken.None);

            if (!engine.Running)
            {
                return;
            }

            if (engine.Answering is true)
            {
                Publish();
                Changed?.Invoke();
                return;
            }

            try
            {
                await Task.Delay(ReadyPoll, time);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ListOneAsync(Engine engine, CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelInfo>? models;

        try
        {
            models = await engine.Backend.ListModelsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            engine.LastError = ex.Message;
            models = null;
        }

        if (!engine.Running)
        {
            return;
        }

        if (models is null)
        {
            engine.Answering = false;
            engine.LastModels = [];
            engine.LastError ??= $"{engine.Type} at {engine.Backend.Endpoint} did not answer a model listing";
            return;
        }

        engine.Answering = true;
        engine.AnsweredSinceStart = true;
        engine.LastError = null;
        engine.LastModels = ModelFilter.Apply(models, engine.Models);
    }

    /// <summary>Rebuilds the model → engine table from the engines that are running and answered.</summary>
    private void Publish()
    {
        var table = new Dictionary<string, Engine>(StringComparer.OrdinalIgnoreCase);

        foreach (var engine in engines.Where(e => e.Running && e.Answering is true))
        {
            foreach (var model in engine.LastModels.Select(m => m.Name).Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                if (table.TryGetValue(model, out var first))
                {
                    if (loggedCollisions.TryAdd($"{model}\n{engine.Name}", 0))
                    {
                        logger.LogWarning(
                            "Model '{Model}' is reported by engine '{First}' and engine '{Second}'; requests go to '{First}', whose name sorts first. Give one of them a different alias.",
                            model,
                            first.Name,
                            engine.Name,
                            first.Name);
                    }

                    continue;
                }

                table[model] = engine;
            }
        }

        Volatile.Write(ref routes, table);
    }

    private IReadOnlyList<ModelInfo> CurrentModels()
    {
        var table = Volatile.Read(ref routes);

        return table
            .Select(pair => pair.Value.LastModels.First(m => string.Equals(m.Name, pair.Key, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private Engine? Resolve(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var table = Volatile.Read(ref routes);
        model = model.Trim();

        if (table.TryGetValue(model, out var engine) && engine.Running)
        {
            return engine;
        }

        // Ollama's own equivalence: `llama3` is `llama3:latest` (and the other way round).
        var alternative = model.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            ? model[..^":latest".Length]
            : model.Contains(':') ? null : model + ":latest";

        return alternative is not null && table.TryGetValue(alternative, out engine) && engine.Running
            ? engine
            : null;
    }

    private async Task<(Engine Engine, string Model)> RouteAsync(string requestJson, CancellationToken cancellationToken)
    {
        var model = ModelOf(requestJson);

        if (Resolve(model) is { } engine)
        {
            return (engine, model!);
        }

        // A request can arrive before the first listing, or right after an engine came up: ask once.
        await ListModelsAsync(cancellationToken);

        return Resolve(model) is { } refreshed
            ? (refreshed, model!)
            : throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(model)
                    ? "the request names no model, and this node runs several engines; it cannot tell which one to send it to"
                    : $"no running engine on this node serves '{model}'");
    }

    private async Task<string> RunAsync(
        string requestJson,
        Func<IInferenceBackend, CancellationToken, Task<string>> call,
        CancellationToken cancellationToken)
    {
        var (engine, model) = await RouteAsync(requestJson, cancellationToken);
        engine.Enter();

        try
        {
            Remember(engine, model);
            return await call(engine.Backend, cancellationToken);
        }
        finally
        {
            engine.Leave();
        }
    }

    private static void Remember(Engine engine, string model)
    {
        if (engine.Unload is not null)
        {
            engine.Routed.TryAdd(model, 0);
        }
    }

    internal static string? ModelOf(string requestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJson);

            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("model", out var model)
                   && model.ValueKind == JsonValueKind.String
                ? model.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
