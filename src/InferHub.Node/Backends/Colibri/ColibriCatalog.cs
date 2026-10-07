using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using InferHub.Shared.Brio;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Backends.Colibri;

/// <summary>
/// What the hub and the node's profile applier can ask of a colibri catalogue (phase 97). Registered
/// only on a node that has one, so the applier and the connection hold a nullable.
/// </summary>
public interface IColibriControl
{
    /// <summary>The catalogue as the box has it now — the ceiling a profile picks from (97 D4).</summary>
    IReadOnlyList<string> CatalogNames { get; }

    int MaxLoaded { get; }

    /// <summary>Converges on a clamped profile block; null is "the box's own <c>Preload</c> and <c>OnDemand</c>".</summary>
    Task<IReadOnlyList<string>> ApplyAsync(ColibriProfile? desired, CancellationToken cancellationToken);

    /// <summary>Completes on the first <see cref="ApplyAsync"/> — a profile, or "no profile", from the hub.</summary>
    Task FirstApplied { get; }

    NodeColibriState State(string nodeId);

    /// <summary>A model started loading, loaded, failed or was stopped.</summary>
    event Action? Changed;
}

/// <summary>The process behind one loaded catalogue model, so the catalogue can be tested without Python.</summary>
public interface IColibriLauncher
{
    IColibriProcess Launch(string model, string directory, int port);
}

public interface IColibriProcess
{
    /// <summary>The OpenAI base, <c>http://127.0.0.1:{port}/v1</c>.</summary>
    string BaseUrl { get; }

    /// <summary>Null while it runs; otherwise why it stopped or never started.</summary>
    string? Exited { get; }

    Task StopAsync();
}

/// <summary>
/// A colibri node's catalogue of converted models (phase 97): every model is listed to the hub, the
/// one a request names is loaded, at most <c>Serve:MaxLoaded</c> run at once, and on demand an idle
/// one is stopped to give its RAM back.
/// </summary>
/// <remarks>
/// <para>
/// <b>One <c>coli serve</c> per loaded model</b> (D1) — the gateway takes exactly one
/// <c>--model</c>, so there is no engine-side router to drive the way 96 drives llama.cpp's. Each
/// loaded model has its own port, its own <see cref="EngineProcess"/> and its own
/// <see cref="UpstreamBackend"/>; everything a v3.58 colibri node does per request (the dialect, the
/// sized bodies, the KV slot pinning, Brio) is that class, unchanged.
/// </para>
/// <para>
/// <b>Admission</b> (D2): loaded → go; a slot free → launch and wait for <c>/health</c>; none free →
/// stop the least recently used model with nothing in flight that the hub did not pin, then launch;
/// every slot busy → wait; every slot pinned → refuse naming them. A process that exits before it
/// answers is a failed load with the exit in the sentence, never a fifteen-minute hang.
/// </para>
/// <para>
/// <b>Pinned</b> (D4) is the hub's profile <c>colibri.loaded</c>, or the box's <c>Serve:Preload</c>
/// when the profile says nothing: those are loaded when the catalogue starts and are never evicted or
/// idled out. <b>On demand</b> (D3) stops the rest after <c>Serve:IdleUnload</c> without a request.
/// </para>
/// </remarks>
public sealed class ColibriCatalog : IInferenceBackend, IClosedSetScorer, IModelKinds, IColibriControl, IEngineLifecycle
{
    private static readonly string[] ChatAndScore = [CapabilityKinds.Chat, CapabilityKinds.Score];

    private static readonly TimeSpan BusyPoll = TimeSpan.FromMilliseconds(200);

    private readonly ColibriOptions options;
    private readonly IColibriLauncher launcher;
    private readonly Func<string, UpstreamBackend> upstreamFor;
    private readonly Func<HttpClient> probeClient;
    private readonly TimeSpan stopDrain;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Resident> residents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ports whose process is draining or dying: still RAM and still a bound port, so still a slot.</summary>
    private readonly HashSet<int> stopping = [];
    private readonly ConcurrentDictionary<string, string> failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> warned = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource firstApply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IReadOnlyList<string> preload;
    private HashSet<string> pinned;
    private bool? onDemandOverride;
    private volatile bool running;
    private CancellationTokenSource? sweeper;

    public ColibriCatalog(
        ColibriOptions options,
        IColibriLauncher launcher,
        Func<string, UpstreamBackend> upstreamFor,
        Func<HttpClient> probeClient,
        TimeSpan stopDrain,
        TimeProvider time,
        ILogger logger)
    {
        this.options = options;
        this.launcher = launcher;
        this.upstreamFor = upstreamFor;
        this.probeClient = probeClient;
        this.stopDrain = stopDrain;
        this.time = time;
        this.logger = logger;

        preload = options.Serve.Preload
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        pinned = new HashSet<string>(preload, StringComparer.OrdinalIgnoreCase);
    }

    public event Action? Changed;

    public string Name => BackendOptions.Colibri;

    public string Endpoint
    {
        get
        {
            var loaded = Snapshot().Select(r => $"{r.Model}={r.Process.BaseUrl}").ToArray();
            return loaded.Length == 0 ? "colibri catalogue, nothing loaded" : string.Join(", ", loaded);
        }
    }

    public IReadOnlyList<string> Kinds => ChatAndScore;

    public IReadOnlyList<string>? KindsFor(string model) => Resolve(model) is null ? null : ChatAndScore;

    /// <summary>Warm and unload are a load and a stop here (96 D3's commands); a pull is not (97 non-goal).</summary>
    public bool SupportsModelManagement => true;

    public bool SupportsPull => false;

    public int MaxLoaded => options.Serve.MaxLoaded;

    public bool OnDemand => onDemandOverride ?? options.Serve.OnDemand;

    public IReadOnlyList<string> CatalogNames => Scan().Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public Task FirstApplied => firstApply.Task;

    public bool Running => running;

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        if (running)
        {
            return;
        }

        running = true;
        sweeper = new CancellationTokenSource();
        var token = sweeper.Token;
        _ = Task.Run(() => SweepLoopAsync(token));

        LoadPinnedInBackground();
        Changed?.Invoke();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        running = false;

        if (Interlocked.Exchange(ref sweeper, null) is { } cts)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }

        Resident[] all;

        await gate.WaitAsync(cancellationToken);

        try
        {
            all = Snapshot();

            foreach (var r in all)
            {
                Retire(r);
            }
        }
        finally
        {
            gate.Release();
        }

        await Task.WhenAll(all.Select(r => DrainAndStopAsync(r, "the colibri engine was stopped")));
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ the hub's selection (D4)

    public async Task<IReadOnlyList<string>> ApplyAsync(ColibriProfile? desired, CancellationToken cancellationToken)
    {
        var changes = new List<string>();
        var released = new List<Resident>();
        bool pinsChanged;

        await gate.WaitAsync(cancellationToken);

        try
        {
            var wanted = new HashSet<string>(
                desired?.Loaded is { } loaded
                    ? loaded.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())
                    : preload,
                StringComparer.OrdinalIgnoreCase);

            pinsChanged = !wanted.SetEquals(pinned);

            foreach (var model in pinned.Where(m => !wanted.Contains(m)))
            {
                changes.Add($"colibri '{model}' unpinned");

                // Desired state: a model the hub took out of the set is unloaded, not left to idle.
                if (Take(model) is { } resident)
                {
                    Retire(resident);
                    released.Add(resident);
                }
            }

            foreach (var model in wanted.Where(m => !pinned.Contains(m)))
            {
                changes.Add($"colibri '{model}' pinned");
            }

            lock (residents)
            {
                pinned = wanted;
            }

            if (desired?.OnDemand != onDemandOverride)
            {
                var before = OnDemand;
                onDemandOverride = desired?.OnDemand;

                if (OnDemand != before)
                {
                    changes.Add($"colibri on-demand {(OnDemand ? "on" : "off")}");
                }
            }
        }
        finally
        {
            gate.Release();
        }

        firstApply.TrySetResult();

        // Stopped before the new pins load, so a switch on a one-slot box never holds both.
        var stops = released.Select(r => DrainAndStopAsync(r, "the hub unpinned it")).ToArray();

        if (running && pinsChanged)
        {
            _ = Task.Run(async () =>
            {
                await Task.WhenAll(stops);
                LoadPinnedInBackground();
            });
        }

        if (changes.Count > 0)
        {
            Changed?.Invoke();
        }

        return changes;
    }

    public NodeColibriState State(string nodeId)
    {
        var now = time.GetUtcNow();
        var loaded = Snapshot().ToDictionary(r => r.Model, StringComparer.OrdinalIgnoreCase);
        HashSet<string> pins;

        lock (residents)
        {
            pins = new HashSet<string>(pinned, StringComparer.OrdinalIgnoreCase);
        }

        var names = Scan().Keys
            .Concat(loaded.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        return new NodeColibriState(
            nodeId,
            OnDemand,
            options.Serve.IdleUnload.TotalSeconds,
            MaxLoaded,
            running,
            names.Select(name =>
            {
                if (loaded.TryGetValue(name, out var r))
                {
                    var ready = r.IsLoaded;
                    return new NodeColibriModel(
                        r.Model,
                        ready ? NodeColibriModel.Loaded : NodeColibriModel.Loading,
                        pins.Contains(name),
                        r.InFlight,
                        ready && r.InFlight == 0 ? Math.Round((now - r.LastUsed).TotalSeconds) : null);
                }

                return failures.TryGetValue(name, out var error)
                    ? new NodeColibriModel(name, NodeColibriModel.Failed, pins.Contains(name), 0, null, error)
                    : new NodeColibriModel(name, NodeColibriModel.Unloaded, pins.Contains(name), 0);
            }).ToArray(),
            now);
    }

    // ------------------------------------------------------------------ IInferenceBackend

    /// <summary>The catalogue, loaded or not — the hub routes every one of them here (D2). Never null: it is a directory.</summary>
    public Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ModelInfo>?>(
            CatalogNames.Select(name => new ModelInfo(name, Digest: null, SizeBytes: null)).ToArray());

    public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken)
        => RunAsync(requestJson, (backend, ct) => backend.ChatAsync(requestJson, ct), cancellationToken);

    public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken)
        => RunAsync(requestJson, (backend, ct) => backend.GenerateAsync(requestJson, ct), cancellationToken);

    public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken)
        => RunAsync(requestJson, (backend, ct) => backend.EmbedAsync(requestJson, ct), cancellationToken);

    public async IAsyncEnumerable<string> StreamAsync(
        string kind,
        string requestJson,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var resident = await EnsureLoadedAsync(MultiBackend.ModelOf(requestJson), cancellationToken);

        try
        {
            await foreach (var chunk in resident.Upstream.StreamAsync(kind, requestJson, cancellationToken))
            {
                yield return chunk;
            }
        }
        finally
        {
            Release(resident);
        }
    }

    public async Task<ToolResult> ScoreAsync(ToolJob job, CancellationToken cancellationToken)
    {
        Resident resident;

        try
        {
            resident = await EnsureLoadedAsync(job.Model, cancellationToken);
        }
        catch (ColibriCatalogException ex) when (ex.NotInCatalogue)
        {
            return ToolResult.Refused(job.JobId, ex.Message, BrioErrorCodes.ModelNotFound);
        }
        catch (ColibriCatalogException ex)
        {
            return ToolResult.Failed(job.JobId, ex.Message);
        }

        try
        {
            return await resident.Upstream.ScoreAsync(job, cancellationToken);
        }
        finally
        {
            Release(resident);
        }
    }

    public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            $"colibri models are converted with `coli convert`, not pulled; put the converted directory under {ColibriOptions.SectionName}:Serve:ModelsDir and it is listed on the next refresh");

    public Task DeleteAsync(string model, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            $"this node does not delete colibri models; remove the directory from {ColibriOptions.SectionName}:Serve:ModelsDir on the box");

    public async Task WarmAsync(string model, CancellationToken cancellationToken)
        => Release(await EnsureLoadedAsync(model, cancellationToken));

    /// <summary>96 D3's unload: stop the model's <c>coli serve</c>, after its in-flight work drains.</summary>
    public async Task UnloadAsync(string model, CancellationToken cancellationToken)
    {
        var name = Resolve(model)?.Name ?? throw ColibriCatalogException.NotIn(model, CatalogNames);
        Resident? resident;

        await gate.WaitAsync(cancellationToken);

        try
        {
            if (pinned.Contains(name))
            {
                throw new InvalidOperationException(
                    $"'{name}' is pinned by this node's profile (colibri.loaded); unpin it from the hub's colibri panel to unload it");
            }

            resident = Take(name);

            if (resident is not null)
            {
                Retire(resident);
            }
        }
        finally
        {
            gate.Release();
        }

        if (resident is not null)
        {
            await DrainAndStopAsync(resident, "unloaded by a model command");
            Changed?.Invoke();
        }
    }

    // ------------------------------------------------------------------ admission (D2)

    private async Task<string> RunAsync(
        string requestJson,
        Func<IInferenceBackend, CancellationToken, Task<string>> call,
        CancellationToken cancellationToken)
    {
        var resident = await EnsureLoadedAsync(MultiBackend.ModelOf(requestJson), cancellationToken);

        try
        {
            return await call(resident.Upstream, cancellationToken);
        }
        finally
        {
            Release(resident);
        }
    }

    /// <summary>
    /// Returns the model's resident with one request counted in flight — the caller must
    /// <see cref="Release"/> it. Loads it first, evicting as D2 says, when it is not loaded.
    /// </summary>
    internal async Task<Resident> EnsureLoadedAsync(string? model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ColibriCatalogException("the request names no model, and colibri on this node serves a catalogue; name one of " + string.Join(", ", CatalogNames), notInCatalogue: true);
        }

        var deadline = time.GetUtcNow() + options.Serve.LoadTimeout;

        while (true)
        {
            Resident? resident = null;

            await gate.WaitAsync(cancellationToken);

            try
            {
                if (!running)
                {
                    throw new ColibriCatalogException("the colibri engine on this node is stopped");
                }

                var (name, directory) = Resolve(model) ?? throw ColibriCatalogException.NotIn(model, CatalogNames);

                if (residents.TryGetValue(name, out resident))
                {
                    resident.Enter();
                }
                else if (Occupied < MaxLoaded || (stopping.Count < MaxLoaded && Victim() is not null))
                {
                    if (Occupied >= MaxLoaded && Victim() is { } victim)
                    {
                        Take(victim.Model);
                        Retire(victim);
                        victim.Stopped = true;

                        logger.LogInformation(
                            "Stopping colibri model '{Victim}' (idle {Idle}) to load '{Model}': {MaxLoaded} may be loaded at once.",
                            victim.Model,
                            time.GetUtcNow() - victim.LastUsed,
                            name,
                            MaxLoaded);

                        // Inside the gate on purpose: its RAM is free before the next one maps its weights.
                        await victim.Process.StopAsync();
                        Unretire(victim);
                    }

                    if (directory is null)
                    {
                        throw ColibriCatalogException.NotIn(model, CatalogNames);
                    }

                    resident = Launch(name, directory, FreePort());
                    resident.Enter();
                }
                else if (stopping.Count == 0 && residents.Values.All(r => pinned.Contains(r.Model)))
                {
                    throw new ColibriCatalogException(
                        $"every one of this node's {MaxLoaded} colibri slot(s) holds a model the hub pinned ({string.Join(", ", residents.Keys)}); unpin one or raise {ColibriOptions.SectionName}:Serve:MaxLoaded to load '{name}'");
                }
            }
            finally
            {
                gate.Release();
            }

            if (resident is null)
            {
                if (time.GetUtcNow() >= deadline)
                {
                    throw new ColibriCatalogException(
                        $"every colibri slot on this node stayed busy for {options.Serve.LoadTimeout}; '{model}' was not loaded");
                }

                await Task.Delay(BusyPoll, time, cancellationToken);
                continue;
            }

            string? error;

            try
            {
                error = await resident.Ready.WaitAsync(cancellationToken);
            }
            catch
            {
                Release(resident);
                throw;
            }

            if (error is null)
            {
                return resident;
            }

            Release(resident);
            await ForgetFailedAsync(resident, error);
            throw new ColibriCatalogException($"colibri could not load '{resident.Model}': {error}");
        }
    }

    internal void Release(Resident resident)
    {
        resident.LastUsed = time.GetUtcNow();
        resident.Leave();
    }

    /// <summary>The least recently used loaded model that the hub did not pin and nothing is using.</summary>
    private Resident? Victim() => residents.Values
        .Where(r => !pinned.Contains(r.Model) && r.InFlight == 0 && r.Ready.IsCompleted)
        .OrderBy(r => r.LastUsed)
        .FirstOrDefault();

    private int FreePort()
    {
        var taken = residents.Values.Select(r => r.Port).ToHashSet();

        lock (stopping)
        {
            taken.UnionWith(stopping);
        }

        return Enumerable.Range(options.Serve.Port, MaxLoaded).First(port => !taken.Contains(port));
    }

    private Resident Launch(string model, string directory, int port)
    {
        failures.TryRemove(model, out _);

        var process = launcher.Launch(model, directory, port);
        var resident = new Resident(model, port, process, upstreamFor(process.BaseUrl), time.GetUtcNow());

        lock (residents)
        {
            residents[model] = resident;
        }

        resident.Ready = Task.Run(() => WaitReadyAsync(resident));

        logger.LogInformation("Loading colibri model '{Model}' from {Directory} on port {Port}.", model, directory, port);
        Changed?.Invoke();
        return resident;
    }

    /// <summary>Null when <c>/health</c> answers; otherwise the sentence that says why it never will.</summary>
    private async Task<string?> WaitReadyAsync(Resident resident)
    {
        var started = time.GetUtcNow();
        var deadline = started + options.Serve.LoadTimeout;
        var health = new Uri(MultiBackendComposition.RootOf(resident.Process.BaseUrl)!, "health");

        while (time.GetUtcNow() < deadline)
        {
            if (resident.Stopped)
            {
                return "it was stopped while it loaded";
            }

            if (resident.Process.Exited is { } exited)
            {
                return $"coli serve stopped before it answered ({exited}); the node's log has its stderr";
            }

            try
            {
                using var http = probeClient();
                using var response = await http.GetAsync(health);

                if (response.IsSuccessStatusCode)
                {
                    resident.IsLoaded = true;
                    logger.LogInformation(
                        "colibri model '{Model}' loaded in {Seconds:0.0} s.",
                        resident.Model,
                        (time.GetUtcNow() - started).TotalSeconds);
                    Changed?.Invoke();
                    return null;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Still reading its weights: the gateway binds its port once the engine is up.
            }

            await Task.Delay(ReadyPoll, time);
        }

        return $"it did not answer within {options.Serve.LoadTimeout} ({ColibriOptions.SectionName}:Serve:LoadTimeout)";
    }

    private TimeSpan ReadyPoll => options.Serve.LoadTimeout < TimeSpan.FromSeconds(10)
        ? TimeSpan.FromMilliseconds(100)
        : TimeSpan.FromSeconds(1);

    private async Task ForgetFailedAsync(Resident resident, string error)
    {
        await gate.WaitAsync();

        try
        {
            if (residents.TryGetValue(resident.Model, out var current) && ReferenceEquals(current, resident))
            {
                Take(resident.Model);
                Retire(resident);
            }
        }
        finally
        {
            gate.Release();
        }

        if (!resident.Stopped)
        {
            resident.Stopped = true;
            failures[resident.Model] = error;
            logger.LogWarning("colibri model '{Model}' did not load: {Error}", resident.Model, error);
            await resident.Process.StopAsync();
            Unretire(resident);
            Changed?.Invoke();
        }
    }

    /// <summary>95 D5 for one model: out of the table first (the caller did that), drain, then kill.</summary>
    private async Task DrainAndStopAsync(Resident resident, string why)
    {
        var deadline = time.GetUtcNow() + stopDrain;

        while (resident.InFlight > 0 && time.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), time);
        }

        if (resident.InFlight > 0)
        {
            logger.LogWarning(
                "colibri model '{Model}' still had {InFlight} request(s) in flight after {Drain}; stopping it under them.",
                resident.Model,
                resident.InFlight,
                stopDrain);
        }

        resident.Stopped = true;
        await resident.Process.StopAsync();
        Unretire(resident);
        logger.LogInformation("Stopped colibri model '{Model}' ({Why}); its RAM is free.", resident.Model, why);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ on demand (D3)

    private async Task SweepLoopAsync(CancellationToken token)
    {
        var interval = TimeSpan.FromTicks(Math.Clamp(options.Serve.IdleUnload.Ticks / 4, TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromSeconds(30).Ticks));

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, time, token);
                await SweepAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The colibri idle sweep failed; it runs again in {Interval}.", interval);
            }
        }
    }

    /// <summary>Stops every loaded, unpinned model idle for <c>Serve:IdleUnload</c>, when on demand.</summary>
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        if (!OnDemand)
        {
            return;
        }

        var now = time.GetUtcNow();
        Resident[] idle;

        await gate.WaitAsync(cancellationToken);

        try
        {
            idle = residents.Values
                .Where(r => r.IsLoaded && r.InFlight == 0 && !pinned.Contains(r.Model) && now - r.LastUsed >= options.Serve.IdleUnload)
                .ToArray();

            foreach (var r in idle)
            {
                Take(r.Model);
                Retire(r);
            }
        }
        finally
        {
            gate.Release();
        }

        foreach (var r in idle)
        {
            await DrainAndStopAsync(r, $"idle for {now - r.LastUsed:hh\\:mm\\:ss}, on demand");
        }
    }

    private void LoadPinnedInBackground()
    {
        string[] wanted;

        lock (residents)
        {
            wanted = pinned.ToArray();
        }

        if (wanted.Length == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var model in wanted)
            {
                if (!running)
                {
                    return;
                }

                try
                {
                    Release(await EnsureLoadedAsync(model, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Could not load pinned colibri model '{Model}': {Error}", model, ex.Message);
                }
            }
        });
    }

    // ------------------------------------------------------------------ the catalogue

    /// <summary>
    /// Read from the box on every call, so a model converted into <c>ModelsDir</c> is listed on the
    /// next refresh without a restart — it is a directory listing, and the hub asks once a minute.
    /// </summary>
    internal Dictionary<string, string> Scan()
    {
        var catalogue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir = options.Serve.ModelsDir;

        if (!string.IsNullOrWhiteSpace(dir))
        {
            if (Directory.Exists(dir))
            {
                foreach (var sub in Directory.EnumerateDirectories(dir).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(Path.Combine(sub, "config.json")))
                    {
                        continue;
                    }

                    var name = Path.GetFileName(sub);

                    // A dot-directory is somebody's work in progress (v3.63's conversions stage in
                    // `.converting-<name>`), not a model somebody misnamed: skipped without a warning.
                    if (name.StartsWith('.'))
                    {
                        continue;
                    }

                    if (!ColibriOptions.IsModelName(name))
                    {
                        if (warned.TryAdd("name:" + name, 0))
                        {
                            logger.LogWarning(
                                "'{Directory}' is a converted model, but '{Name}' is not a model name (letters, digits, '.', '_', '-'); it is not listed. Rename it, or name it under {Section}:Serve:Models.",
                                sub, name, ColibriOptions.SectionName);
                        }

                        continue;
                    }

                    catalogue[name] = sub;
                }
            }
            else if (warned.TryAdd("dir:" + dir, 0))
            {
                logger.LogWarning(
                    "{Section}:Serve:ModelsDir is '{Directory}', which is not a directory this node can see; it lists nothing until it is mounted.",
                    ColibriOptions.SectionName, dir);
            }
        }

        foreach (var pair in options.Serve.Models)
        {
            if (ColibriOptions.IsModelName(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                catalogue[pair.Key.Trim()] = pair.Value.Trim();
            }
        }

        return catalogue;
    }

    /// <summary>
    /// The catalogue's own spelling of <paramref name="model"/> and its directory, from one scan.
    /// <c>:latest</c> is Ollama's and means nothing here. A model loaded and since removed from the
    /// box resolves with no directory: it is served until it stops, and never launched again.
    /// </summary>
    private (string Name, string? Directory)? Resolve(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        model = model.Trim();

        if (model.EndsWith(":latest", StringComparison.OrdinalIgnoreCase))
        {
            model = model[..^":latest".Length];
        }

        foreach (var pair in Scan())
        {
            if (string.Equals(pair.Key, model, StringComparison.OrdinalIgnoreCase))
            {
                return (pair.Key, pair.Value);
            }
        }

        lock (residents)
        {
            var loaded = residents.Keys.FirstOrDefault(k => string.Equals(k, model, StringComparison.OrdinalIgnoreCase));
            return loaded is null ? null : (loaded, null);
        }
    }

    /// <summary>Loaded models plus the ones still dying — each is a process holding RAM and a port.</summary>
    private int Occupied
    {
        get
        {
            lock (stopping)
            {
                return residents.Count + stopping.Count;
            }
        }
    }

    private void Retire(Resident resident)
    {
        lock (stopping)
        {
            stopping.Add(resident.Port);
        }
    }

    private void Unretire(Resident resident)
    {
        lock (stopping)
        {
            stopping.Remove(resident.Port);
        }
    }

    /// <summary>Removes a resident from the table. Writers hold <c>gate</c>; the lock is for readers that do not.</summary>
    private Resident? Take(string model)
    {
        lock (residents)
        {
            return residents.Remove(model, out var resident) ? resident : null;
        }
    }

    private Resident[] Snapshot()
    {
        lock (residents)
        {
            return residents.Values.ToArray();
        }
    }

    /// <summary>One loaded (or loading) model: its port, its process, its client, and who is using it.</summary>
    internal sealed class Resident(string model, int port, IColibriProcess process, UpstreamBackend upstream, DateTimeOffset now)
    {
        private int inFlight;

        public string Model { get; } = model;

        public int Port { get; } = port;

        public IColibriProcess Process { get; } = process;

        public UpstreamBackend Upstream { get; } = upstream;

        public Task<string?> Ready { get; set; } = Task.FromResult<string?>(null);

        public volatile bool IsLoaded;

        public volatile bool Stopped;

        public DateTimeOffset LastUsed { get; set; } = now;

        public int InFlight => Volatile.Read(ref inFlight);

        public void Enter() => Interlocked.Increment(ref inFlight);

        public void Leave() => Interlocked.Decrement(ref inFlight);
    }
}

/// <summary>A catalogue refusal with the sentence an operator reads; <see cref="NotInCatalogue"/> is a 404, not a failure.</summary>
public sealed class ColibriCatalogException(string message, bool notInCatalogue = false) : InvalidOperationException(message)
{
    public bool NotInCatalogue { get; } = notInCatalogue;

    public static ColibriCatalogException NotIn(string model, IReadOnlyList<string> names) => new(
        names.Count == 0
            ? $"colibri on this node has no model '{model}'; its catalogue is empty ({ColibriOptions.SectionName}:Serve:ModelsDir)"
            : $"colibri on this node has no model '{model}'; it has {string.Join(", ", names)}",
        notInCatalogue: true);
}

/// <summary>The shipped launcher: one supervised <c>coli serve</c> per model, 95's loop and Job Object.</summary>
public sealed class ColibriProcessLauncher(ColibriOptions options, TimeProvider time, ILogger logger) : IColibriLauncher
{
    public IColibriProcess Launch(string model, string directory, int port)
    {
        var process = new EngineProcess(
            $"colibri:{model}",
            () => ColibriServe.StartInfo(options, directory, model, port),
            () => Directory.Exists(directory) ? null : $"'{directory}' is not a directory this node can see",
            time,
            logger);

        process.Start();
        return new Launched(process, $"http://127.0.0.1:{port}/v1");
    }

    private sealed class Launched(EngineProcess process, string baseUrl) : IColibriProcess
    {
        public string BaseUrl { get; } = baseUrl;

        public string? Exited => process.Failed || process.LastError is not null ? process.LastError ?? "it could not be started" : null;

        public Task StopAsync() => process.StopAsync();
    }
}
