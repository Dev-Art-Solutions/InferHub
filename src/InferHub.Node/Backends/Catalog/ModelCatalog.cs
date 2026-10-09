using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Backends.Catalog;

/// <summary>
/// What the hub and the node's profile applier can ask of a model catalogue — colibri's (phase 97)
/// or Strata's (phase 99). Registered only on a node that has one, so the applier and the connection
/// hold a nullable per engine.
/// </summary>
public interface ICatalogControl
{
    /// <summary>The engine type the catalogue serves: <c>colibri</c> or <c>strata</c>.</summary>
    string Name { get; }

    /// <summary>The catalogue as the box has it now — the ceiling a profile picks from (97 D4).</summary>
    IReadOnlyList<string> CatalogNames { get; }

    int MaxLoaded { get; }

    /// <summary>Converges on a clamped profile block; null is "the box's own <c>Preload</c> and <c>OnDemand</c>".</summary>
    Task<IReadOnlyList<string>> ApplyAsync(CatalogProfile? desired, CancellationToken cancellationToken);

    /// <summary>Completes on the first <see cref="ApplyAsync"/> — a profile, or "no profile", from the hub.</summary>
    Task FirstApplied { get; }

    NodeCatalogState State(string nodeId);

    /// <summary>A model started loading, loaded, failed, was stopped — or, for Strata, was installed.</summary>
    event Action? Changed;
}

/// <summary>The keys every catalogue's <c>Serve:</c> section has (97 D1–D3), whichever engine it launches.</summary>
public interface ICatalogServeOptions
{
    /// <summary>The first loopback port; loaded model <c>i</c> listens on <c>Port + i</c>.</summary>
    int Port { get; }

    int MaxLoaded { get; }

    bool OnDemand { get; }

    TimeSpan IdleUnload { get; }

    TimeSpan LoadTimeout { get; }

    List<string> Preload { get; }
}

/// <summary>The process behind one loaded catalogue model, so a catalogue can be tested without Python.</summary>
public interface ICatalogLauncher
{
    /// <param name="path">What the scan found for the model: colibri's converted directory, Strata's config file.</param>
    ICatalogProcess Launch(string model, string path, int port);
}

public interface ICatalogProcess
{
    /// <summary>The OpenAI base, <c>http://127.0.0.1:{port}/v1</c>.</summary>
    string BaseUrl { get; }

    /// <summary>Null while it runs; otherwise why it stopped or never started.</summary>
    string? Exited { get; }

    Task StopAsync();
}

/// <summary>
/// A node's catalogue of models one engine process each (phase 97, generalised in 99): every model is
/// listed to the hub, the one a request names is loaded, at most <c>Serve:MaxLoaded</c> run at once,
/// and on demand an idle one is stopped to give its RAM back.
/// </summary>
/// <remarks>
/// <para>
/// <b>One process per loaded model</b> (97 D1) — <c>coli serve</c> and Strata's server each take
/// exactly one model, so there is no engine-side router to drive the way 96 drives llama.cpp's. Each
/// loaded model has its own port, its own <see cref="EngineProcess"/> and its own
/// <see cref="UpstreamBackend"/>; everything an engine does per request is that class, unchanged.
/// </para>
/// <para>
/// <b>Admission</b> (97 D2): loaded → go; a slot free → launch and wait for <c>/health</c>; none free →
/// stop the least recently used model with nothing in flight that the hub did not pin, then launch;
/// every slot busy → wait; every slot pinned → refuse naming them. A process that exits before it
/// answers is a failed load with the exit in the sentence, never a fifteen-minute hang.
/// </para>
/// <para>
/// <b>Pinned</b> (97 D4) is the hub's profile block, or the box's <c>Serve:Preload</c> when the profile
/// says nothing: those are loaded when the catalogue starts and are never evicted or idled out. <b>On
/// demand</b> (97 D3) stops the rest after <c>Serve:IdleUnload</c> without a request.
/// </para>
/// <para>
/// <b>Why a base class</b> (99 D1): Strata is the same shape as colibri — a Python server per model,
/// tens of gigabytes of RAM each — and the admission, eviction and pin rules are exactly the ones a
/// second copy would get subtly wrong. What differs is what a model <em>is</em> on disk
/// (<see cref="Scan"/>), how it is launched (the launcher), and how ready looks (<see cref="IsReadyAsync"/>).
/// </para>
/// </remarks>
public abstract class ModelCatalog : IInferenceBackend, IModelKinds, ICatalogControl, IEngineLifecycle
{
    private static readonly TimeSpan BusyPoll = TimeSpan.FromMilliseconds(200);

    private readonly ICatalogServeOptions serve;
    private readonly ICatalogLauncher launcher;
    private readonly Func<string, UpstreamBackend> upstreamFor;
    private readonly Func<HttpClient> probeClient;
    private readonly TimeSpan stopDrain;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Resident> residents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ports whose process is draining or dying: still RAM and still a bound port, so still a slot.</summary>
    private readonly HashSet<int> stopping = [];
    private readonly ConcurrentDictionary<string, string> failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource firstApply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IReadOnlyList<string> preload;
    private HashSet<string> pinned;
    private bool? onDemandOverride;
    private volatile bool running;
    private CancellationTokenSource? sweeper;

    protected ModelCatalog(
        ICatalogServeOptions serve,
        ICatalogLauncher launcher,
        Func<string, UpstreamBackend> upstreamFor,
        Func<HttpClient> probeClient,
        TimeSpan stopDrain,
        TimeProvider time,
        ILogger logger)
    {
        this.serve = serve;
        this.launcher = launcher;
        this.upstreamFor = upstreamFor;
        this.probeClient = probeClient;
        this.stopDrain = stopDrain;
        Time = time;
        Logger = logger;

        preload = serve.Preload
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        pinned = new HashSet<string>(preload, StringComparer.OrdinalIgnoreCase);
    }

    public event Action? Changed;

    /// <summary>The engine type: <c>colibri</c>, <c>strata</c>. Also the word every sentence uses.</summary>
    public abstract string Name { get; }

    /// <summary>The configuration section a sentence points an operator at: <c>Colibri</c>, <c>Strata</c>.</summary>
    protected abstract string SectionName { get; }

    /// <summary>What a loaded model's process is called in a sentence: <c>coli serve</c>, <c>Strata's server</c>.</summary>
    protected abstract string ProcessName { get; }

    /// <summary>Where the catalogue comes from, for the sentence an empty one says.</summary>
    protected abstract string CatalogueSource { get; }

    protected TimeProvider Time { get; }

    protected ILogger Logger { get; }

    public string Endpoint
    {
        get
        {
            var loaded = Snapshot().Select(r => $"{r.Model}={r.Process.BaseUrl}").ToArray();
            return loaded.Length == 0 ? $"{Name} catalogue, nothing loaded" : string.Join(", ", loaded);
        }
    }

    public abstract IReadOnlyList<string> Kinds { get; }

    public IReadOnlyList<string>? KindsFor(string model) => Resolve(model) is null ? null : Kinds;

    /// <summary>Warm and unload are a load and a stop here (96 D3's commands).</summary>
    public bool SupportsModelManagement => true;

    public virtual bool SupportsPull => false;

    public int MaxLoaded => serve.MaxLoaded;

    public bool OnDemand => onDemandOverride ?? serve.OnDemand;

    public IReadOnlyList<string> CatalogNames => Scan().Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public Task FirstApplied => firstApply.Task;

    public bool Running => running;

    /// <summary>
    /// Read from the box on every call, so a model added to the catalogue is listed on the next refresh
    /// without a restart — it is a directory listing, and the hub asks once a minute. Name → path.
    /// </summary>
    protected internal abstract Dictionary<string, string> Scan();

    /// <summary>The client a loaded model is reached through. Strata's carries the config's own API key.</summary>
    protected virtual UpstreamBackend UpstreamFor(string model, string path, string baseUrl) => upstreamFor(baseUrl);

    /// <summary>Whether a <c>/health</c> answer means "loaded". Default: any 2xx.</summary>
    protected virtual Task<bool> IsReadyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => Task.FromResult(response.IsSuccessStatusCode);

    /// <summary>Phase 99: what the node could install, for the hub's panel. Null: this engine installs nothing.</summary>
    protected virtual IReadOnlyList<CatalogInstallable>? Installable(IReadOnlyCollection<string> catalogue) => null;

    /// <summary>Phase 100: whether the model at <paramref name="path"/> reads pictures; null where the engine does not say.</summary>
    protected internal virtual bool? ImagesOf(string path) => null;

    /// <summary>
    /// Phase 100: a refusal of the request before its model is loaded, or null to go ahead. A request
    /// the model cannot serve is a sentence in milliseconds, not one after minutes of loading.
    /// </summary>
    protected virtual string? Refuse(string model, string path, string requestJson) => null;

    /// <summary>Raises <see cref="Changed"/> — for a subclass whose catalogue grew (an install finished).</summary>
    protected void OnChanged() => Changed?.Invoke();

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

        await Task.WhenAll(all.Select(r => DrainAndStopAsync(r, $"the {Name} engine was stopped")));
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ the hub's selection (97 D4)

    public async Task<IReadOnlyList<string>> ApplyAsync(CatalogProfile? desired, CancellationToken cancellationToken)
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
                changes.Add($"{Name} '{model}' unpinned");

                // Desired state: a model the hub took out of the set is unloaded, not left to idle.
                if (Take(model) is { } resident)
                {
                    Retire(resident);
                    released.Add(resident);
                }
            }

            foreach (var model in wanted.Where(m => !pinned.Contains(m)))
            {
                changes.Add($"{Name} '{model}' pinned");
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
                    changes.Add($"{Name} on-demand {(OnDemand ? "on" : "off")}");
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

    public NodeCatalogState State(string nodeId)
    {
        var now = Time.GetUtcNow();
        var loaded = Snapshot().ToDictionary(r => r.Model, StringComparer.OrdinalIgnoreCase);
        HashSet<string> pins;

        lock (residents)
        {
            pins = new HashSet<string>(pinned, StringComparer.OrdinalIgnoreCase);
        }

        var scanned = Scan();
        var catalogue = scanned.Keys.ToArray();
        bool? Images(string name) => scanned.TryGetValue(name, out var path) ? ImagesOf(path) : null;

        var names = catalogue
            .Concat(loaded.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        return new NodeCatalogState(
            nodeId,
            OnDemand,
            serve.IdleUnload.TotalSeconds,
            MaxLoaded,
            running,
            names.Select(name =>
            {
                if (loaded.TryGetValue(name, out var r))
                {
                    var ready = r.IsLoaded;
                    return new NodeCatalogModel(
                        r.Model,
                        ready ? NodeCatalogModel.Loaded : NodeCatalogModel.Loading,
                        pins.Contains(name),
                        r.InFlight,
                        ready && r.InFlight == 0 ? Math.Round((now - r.LastUsed).TotalSeconds) : null,
                        Images: Images(name));
                }

                return failures.TryGetValue(name, out var error)
                    ? new NodeCatalogModel(name, NodeCatalogModel.Failed, pins.Contains(name), 0, null, error, Images(name))
                    : new NodeCatalogModel(name, NodeCatalogModel.Unloaded, pins.Contains(name), 0, Images: Images(name));
            }).ToArray(),
            now,
            Installable(catalogue));
    }

    // ------------------------------------------------------------------ IInferenceBackend

    /// <summary>The catalogue, loaded or not — the hub routes every one of them here (97 D2). Never null: it is a directory.</summary>
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
        Admit(requestJson);
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

    public abstract IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken);

    public abstract Task DeleteAsync(string model, CancellationToken cancellationToken);

    public async Task WarmAsync(string model, CancellationToken cancellationToken)
        => Release(await EnsureLoadedAsync(model, cancellationToken));

    /// <summary>96 D3's unload: stop the model's process, after its in-flight work drains.</summary>
    public async Task UnloadAsync(string model, CancellationToken cancellationToken)
    {
        var name = Resolve(model)?.Name ?? throw NotIn(model);
        Resident? resident;

        await gate.WaitAsync(cancellationToken);

        try
        {
            if (pinned.Contains(name))
            {
                throw new InvalidOperationException(
                    $"'{name}' is pinned by this node's profile ({Name}.loaded); unpin it from the hub's {Name} panel to unload it");
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

    // ------------------------------------------------------------------ admission (97 D2)

    private async Task<string> RunAsync(
        string requestJson,
        Func<IInferenceBackend, CancellationToken, Task<string>> call,
        CancellationToken cancellationToken)
    {
        Admit(requestJson);
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

    /// <summary>100: <see cref="Refuse"/>, for a model in the catalogue; an unknown one is <see cref="EnsureLoadedAsync"/>'s sentence.</summary>
    private void Admit(string requestJson)
    {
        if (Resolve(MultiBackend.ModelOf(requestJson)) is ({ } name, { } path) && Refuse(name, path, requestJson) is { } refusal)
        {
            throw new CatalogException(refusal, status: Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>
    /// Returns the model's resident with one request counted in flight — the caller must
    /// <see cref="Release"/> it. Loads it first, evicting as 97 D2 says, when it is not loaded.
    /// </summary>
    internal async Task<Resident> EnsureLoadedAsync(string? model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new CatalogException($"the request names no model, and {Name} on this node serves a catalogue; name one of " + string.Join(", ", CatalogNames), notInCatalogue: true);
        }

        var deadline = Time.GetUtcNow() + serve.LoadTimeout;

        while (true)
        {
            Resident? resident = null;

            await gate.WaitAsync(cancellationToken);

            try
            {
                if (!running)
                {
                    throw new CatalogException($"the {Name} engine on this node is stopped");
                }

                var (name, path) = Resolve(model) ?? throw NotIn(model);

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

                        Logger.LogInformation(
                            "Stopping {Engine} model '{Victim}' (idle {Idle}) to load '{Model}': {MaxLoaded} may be loaded at once.",
                            Name,
                            victim.Model,
                            Time.GetUtcNow() - victim.LastUsed,
                            name,
                            MaxLoaded);

                        // Inside the gate on purpose: its RAM is free before the next one maps its weights.
                        await victim.Process.StopAsync();
                        Unretire(victim);
                    }

                    if (path is null)
                    {
                        throw NotIn(model);
                    }

                    resident = Launch(name, path, FreePort());
                    resident.Enter();
                }
                else if (stopping.Count == 0 && residents.Values.All(r => pinned.Contains(r.Model)))
                {
                    throw new CatalogException(
                        $"every one of this node's {MaxLoaded} {Name} slot(s) holds a model the hub pinned ({string.Join(", ", residents.Keys)}); unpin one or raise {SectionName}:Serve:MaxLoaded to load '{name}'");
                }
            }
            finally
            {
                gate.Release();
            }

            if (resident is null)
            {
                if (Time.GetUtcNow() >= deadline)
                {
                    throw new CatalogException(
                        $"every {Name} slot on this node stayed busy for {serve.LoadTimeout}; '{model}' was not loaded");
                }

                await Task.Delay(BusyPoll, Time, cancellationToken);
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
            throw new CatalogException($"{Name} could not load '{resident.Model}': {error}");
        }
    }

    internal void Release(Resident resident)
    {
        resident.LastUsed = Time.GetUtcNow();
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

        return Enumerable.Range(serve.Port, MaxLoaded).First(port => !taken.Contains(port));
    }

    private Resident Launch(string model, string path, int port)
    {
        failures.TryRemove(model, out _);

        var process = launcher.Launch(model, path, port);
        var resident = new Resident(model, port, process, UpstreamFor(model, path, process.BaseUrl), Time.GetUtcNow());

        lock (residents)
        {
            residents[model] = resident;
        }

        resident.Ready = Task.Run(() => WaitReadyAsync(resident));

        Logger.LogInformation("Loading {Engine} model '{Model}' from {Path} on port {Port}.", Name, model, path, port);
        Changed?.Invoke();
        return resident;
    }

    /// <summary>Null when <c>/health</c> says ready; otherwise the sentence that says why it never will.</summary>
    private async Task<string?> WaitReadyAsync(Resident resident)
    {
        var started = Time.GetUtcNow();
        var deadline = started + serve.LoadTimeout;
        var health = new Uri(MultiBackendComposition.RootOf(resident.Process.BaseUrl)!, "health");

        while (Time.GetUtcNow() < deadline)
        {
            if (resident.Stopped)
            {
                return "it was stopped while it loaded";
            }

            if (resident.Process.Exited is { } exited)
            {
                return $"{ProcessName} stopped before it answered ({exited}); the node's log has its stderr";
            }

            try
            {
                using var http = probeClient();
                using var response = await http.GetAsync(health);

                if (await IsReadyAsync(response, CancellationToken.None))
                {
                    resident.IsLoaded = true;
                    Logger.LogInformation(
                        "{Engine} model '{Model}' loaded in {Seconds:0.0} s.",
                        Name,
                        resident.Model,
                        (Time.GetUtcNow() - started).TotalSeconds);
                    Changed?.Invoke();
                    return null;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Still reading its weights: the server binds its port once the engine is up.
            }

            await Task.Delay(ReadyPoll, Time);
        }

        return $"it did not answer within {serve.LoadTimeout} ({SectionName}:Serve:LoadTimeout)";
    }

    private TimeSpan ReadyPoll => serve.LoadTimeout < TimeSpan.FromSeconds(10)
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
            Logger.LogWarning("{Engine} model '{Model}' did not load: {Error}", Name, resident.Model, error);
            await resident.Process.StopAsync();
            Unretire(resident);
            Changed?.Invoke();
        }
    }

    /// <summary>95 D5 for one model: out of the table first (the caller did that), drain, then kill.</summary>
    private async Task DrainAndStopAsync(Resident resident, string why)
    {
        var deadline = Time.GetUtcNow() + stopDrain;

        while (resident.InFlight > 0 && Time.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), Time);
        }

        if (resident.InFlight > 0)
        {
            Logger.LogWarning(
                "{Engine} model '{Model}' still had {InFlight} request(s) in flight after {Drain}; stopping it under them.",
                Name,
                resident.Model,
                resident.InFlight,
                stopDrain);
        }

        resident.Stopped = true;
        await resident.Process.StopAsync();
        Unretire(resident);
        Logger.LogInformation("Stopped {Engine} model '{Model}' ({Why}); its RAM is free.", Name, resident.Model, why);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ on demand (97 D3)

    private async Task SweepLoopAsync(CancellationToken token)
    {
        var interval = TimeSpan.FromTicks(Math.Clamp(serve.IdleUnload.Ticks / 4, TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromSeconds(30).Ticks));

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, Time, token);
                await SweepAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "The {Engine} idle sweep failed; it runs again in {Interval}.", Name, interval);
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

        var now = Time.GetUtcNow();
        Resident[] idle;

        await gate.WaitAsync(cancellationToken);

        try
        {
            idle = residents.Values
                .Where(r => r.IsLoaded && r.InFlight == 0 && !pinned.Contains(r.Model) && now - r.LastUsed >= serve.IdleUnload)
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
                    Logger.LogWarning("Could not load pinned {Engine} model '{Model}': {Error}", Name, model, ex.Message);
                }
            }
        });
    }

    // ------------------------------------------------------------------ the catalogue

    /// <summary>
    /// The catalogue's own spelling of <paramref name="model"/> and its path, from one scan.
    /// <c>:latest</c> is Ollama's and means nothing here. A model loaded and since removed from the
    /// box resolves with no path: it is served until it stops, and never launched again.
    /// </summary>
    private (string Name, string? Path)? Resolve(string? model)
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

    /// <summary>The refusal for a name the catalogue does not have — a 404, never a failure.</summary>
    protected CatalogException NotIn(string model)
    {
        var names = CatalogNames;

        return new CatalogException(
            names.Count == 0
                ? $"{Name} on this node has no model '{model}'; its catalogue is empty ({CatalogueSource})"
                : $"{Name} on this node has no model '{model}'; it has {string.Join(", ", names)}",
            notInCatalogue: true);
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
    internal sealed class Resident(string model, int port, ICatalogProcess process, UpstreamBackend upstream, DateTimeOffset now)
    {
        private int inFlight;

        public string Model { get; } = model;

        public int Port { get; } = port;

        public ICatalogProcess Process { get; } = process;

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
public sealed class CatalogException(string message, bool notInCatalogue = false, int? status = null) : InvalidOperationException(message)
{
    public bool NotInCatalogue { get; } = notInCatalogue;

    /// <summary>Phase 100: the request's own fault, as its HTTP status (a 4xx), or null for a node-side failure.</summary>
    public int? Status { get; } = status;
}

/// <summary>The launched process behind one catalogue model: 95's supervised loop and Job Object.</summary>
public sealed class LaunchedCatalogProcess(EngineProcess process, string baseUrl) : ICatalogProcess
{
    public string BaseUrl { get; } = baseUrl;

    public string? Exited => process.Failed || process.LastError is not null ? process.LastError ?? "it could not be started" : null;

    public Task StopAsync() => process.StopAsync();
}
