using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Tools;

/// <summary>
/// The live workers for one manifest: warm, pooled, bounded, and put back when they die
/// (phase 41, D4/D6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Warm and pooled, not spawned per request.</b> <c>faster-whisper</c> spends seconds loading
/// weights; per-request spawn would put that on every transcription and thrash a card's memory.
/// </para>
/// <para>
/// <b>Every level has a deadline and a bound.</b> A tool failure is a failed <em>job</em>, never a
/// failed node and never a hung one: start deadline, request deadline, liveness ping, kill on
/// timeout, and a restart budget with backoff lifted from <c>OllamaSupervisor</c> (phase-36 D4)
/// rather than re-derived. Past the budget the pool stops starting workers, logs once at Error,
/// withdraws its capabilities and <em>keeps probing</em> — so a tool that recovers is noticed and
/// one that does not cannot spin.
/// </para>
/// </remarks>
internal sealed class ToolWorkerPool : IAsyncDisposable
{
    private readonly ToolManifest manifest;
    private readonly ToolOptions options;
    private readonly TimeProvider time;
    private readonly ILogger logger;

    /// <summary>The concurrency bound. Holding a permit is what entitles a caller to a process.</summary>
    private readonly SemaphoreSlim slots;

    private readonly object gate = new();
    private readonly Stack<ToolWorkerProcess> idle = new();
    private readonly Queue<DateTimeOffset> startFailures = new();
    private readonly List<Task> terminations = new();

    private int gaveUpFlag;
    private int suspendedFlag;
    private bool gaveUpLogged;
    private DateTimeOffset lastRecoveryProbe;

    // Reported to the hub (phase 45). Counted here rather than at the edge because the edge knows
    // the capability and not which manifest served it, and "which tool is failing" is the question.
    private long requestsServed;
    private long requestsFailed;
    private volatile string? lastError;
    private long lastErrorAtTicks;

    private readonly int declaredVramMiB;

    /// <summary>
    /// The declared budget minus the reserve — what is actually available to a model, and the figure
    /// the worker's own prefetch gate needs (phase 60). Separate from
    /// <see cref="declaredVramMiB"/>, which is the whole-card number the measured cross-check
    /// compares against and would be the wrong one to fetch on.
    /// </summary>
    private readonly int modelBudgetMiB;

    public ToolWorkerPool(
        ToolManifest manifest,
        ToolOptions options,
        TimeProvider time,
        ILogger logger,
        int declaredVramMiB = 0,
        int modelBudgetMiB = 0)
    {
        this.manifest = manifest;
        this.options = options;
        this.time = time;
        this.logger = logger;
        this.declaredVramMiB = declaredVramMiB;
        this.modelBudgetMiB = modelBudgetMiB;
        slots = new SemaphoreSlim(manifest.MaxWorkers, manifest.MaxWorkers);
        lastRecoveryProbe = time.GetUtcNow();
    }

    public ToolManifest Manifest => manifest;

    /// <summary>
    /// The last VRAM figure a worker of this pool measured, in MiB (phase 48, D1). Reported beside
    /// the declared budget on <c>/api/status</c>; never used to decide anything.
    /// </summary>
    public int? ReportedVramTotalMiB { get; private set; }

    /// <summary>
    /// Switched off by a coordinator profile (phase 43). A suspended pool holds no workers, declares
    /// nothing, and is not a candidate for a request — which reads to a client as "this node does not
    /// provide it" rather than as a queue, because that is what it is.
    /// </summary>
    public bool Suspended => Volatile.Read(ref suspendedFlag) == 1;

    /// <summary>
    /// Whether any declared capability defers its model list to the worker (phase 42). Such a pool
    /// must start one worker eagerly — see <see cref="StartAsync"/>.
    /// </summary>
    private bool HasOpenModelSet => manifest.Capabilities.Any(capability => capability.Models.Count == 0);

    /// <summary>
    /// How many warm workers this pool must keep, whatever <c>minWorkers</c> says.
    /// </summary>
    /// <remarks>
    /// <b>An open model set forces a floor of one, and this is a bug fix as much as a feature</b>
    /// (phase 48). The eager start exists because nothing declares an open-ended capability until a
    /// worker has reported (the v3.10.0 deadlock) — but until now the maintenance pass would happily
    /// retire that very worker after <c>idleTimeoutSeconds</c>, leaving a pool that declares models
    /// with no process able to re-declare when one lands or leaves, and killing a prefetch in
    /// flight. The last worker of such a pool is kept and hinted instead (D3): a Python process that
    /// has freed its pipelines costs a few hundred megabytes of RAM and no VRAM at all, and it
    /// saves the next caller the interpreter and the import of torch on top of the weights.
    /// </remarks>
    private int WorkerFloor => Math.Max(manifest.MinWorkers, HasOpenModelSet ? 1 : 0);

    /// <summary>Idle workers this pool is holding. Test-only: nothing in the runtime branches on it.</summary>
    internal int LiveWorkerCount
    {
        get
        {
            lock (gate)
            {
                return idle.Count;
            }
        }
    }

    /// <summary>
    /// What this pool currently offers the mesh. Empty once the pool has given up, which is what
    /// unroutes the node for this capability — the same mechanism phase-36 D7 uses for a broken
    /// backend, reused rather than replaced by a health field.
    /// </summary>
    public IReadOnlyList<NodeCapability> Capabilities { get; private set; } = Array.Empty<NodeCapability>();

    /// <summary>Raised when <see cref="Capabilities"/> changes, so the node can re-report at once.</summary>
    public event Action? CapabilitiesChanged;

    /// <summary>
    /// Raised when this pool has told a worker it is idle (phase 48, D3), so whatever was tracking
    /// what the worker held can stop believing it holds it.
    /// </summary>
    public event Action? WentIdle;

    /// <summary>
    /// What the hub is told about this pool (phase 45). Read-only over what the pool already tracks
    /// — nothing here measures anything it was not measuring, which is phase-28 D2 applied to a
    /// second reporter.
    /// </summary>
    public NodeToolInfo Report()
    {
        int idleCount;

        lock (gate)
        {
            idleCount = idle.Count;
        }

        // A permit that is out is a worker serving a request. `slots` is the concurrency bound, so
        // this is the one number that knows about leased workers — the idle stack does not.
        var busy = Math.Max(0, manifest.MaxWorkers - slots.CurrentCount);
        var errorTicks = Interlocked.Read(ref lastErrorAtTicks);

        return new NodeToolInfo(
            manifest.Id,
            Allowed: true,
            State: Suspended
                ? NodeToolInfo.Suspended
                : Volatile.Read(ref gaveUpFlag) == 1 ? NodeToolInfo.Stopped : NodeToolInfo.Running,
            Capabilities,
            manifest.MaxWorkers,
            idleCount + busy,
            busy,
            Interlocked.Read(ref requestsServed),
            Interlocked.Read(ref requestsFailed),
            lastError,
            errorTicks == 0 ? null : new DateTimeOffset(errorTicks, TimeSpan.Zero));
    }

    private void RecordError(string message)
    {
        lastError = message;
        Interlocked.Exchange(ref lastErrorAtTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Narrow against nothing rather than taking the manifest verbatim: an open-ended capability
        // (`"models": []`) has no answer until a worker has been asked, and advertising `speak: []`
        // in the meantime is a declaration that this node serves nothing under that kind.
        Capabilities = Narrow(manifest.Capabilities, reported: null);

        return StartEagerWorkersAsync(cancellationToken);
    }

    /// <summary>
    /// Stops this pool and withdraws its capabilities, without forgetting that it exists — a profile
    /// that switches it back on gets a running tool, not a node restart (phase-43 D6).
    /// </summary>
    public async Task SuspendAsync()
    {
        if (Interlocked.Exchange(ref suspendedFlag, 1) == 1)
        {
            return;
        }

        logger.LogInformation(
            "Tool '{ToolId}' is switched off by the coordinator's node profile; its workers are stopped and its capabilities are withdrawn from this node.",
            manifest.Id);

        List<ToolWorkerProcess> workers;

        lock (gate)
        {
            workers = idle.ToList();
            idle.Clear();
        }

        foreach (var worker in workers)
        {
            await worker.DisposeAsync();
        }

        Capabilities = Array.Empty<NodeCapability>();
        RaiseCapabilitiesChanged();
    }

    /// <summary>Restores a suspended pool to what <see cref="StartAsync"/> leaves behind.</summary>
    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref suspendedFlag, 0) == 0)
        {
            return;
        }

        logger.LogInformation("Tool '{ToolId}' is switched back on by the coordinator's node profile.", manifest.Id);

        Capabilities = Narrow(manifest.Capabilities, reported: null);
        RaiseCapabilitiesChanged();

        // An open model set still forces an eager worker, for the deadlock reason below: nothing
        // would ever ask the worker what it has, so nothing would ever declare it.
        await StartEagerWorkersAsync(cancellationToken);
    }

    private async Task StartEagerWorkersAsync(CancellationToken cancellationToken)
    {
        // An open-ended capability forces one eager worker, whatever MinWorkers says.
        //
        // FOUND BY RUNNING THE PUBLISHED IMAGE, and it was a deadlock: nothing declares the
        // capability until a worker has reported, no worker starts until a request is routed, and
        // no request is routed to a capability nobody declares. A TTS node with a voice sitting on
        // its volume answered "this node does not provide 'speak'" forever. An open set is a
        // promise to ask the worker, so the asking cannot be deferred until somebody needs the
        // answer.
        var eager = HasOpenModelSet ? Math.Max(manifest.MinWorkers, 1) : manifest.MinWorkers;

        for (var i = 0; i < eager; i++)
        {
            // Eager start is best-effort: a min-workers tool that cannot start must not stop the
            // node from booting, for the same reason a bad manifest does not.
            try
            {
                await slots.WaitAsync(cancellationToken);

                try
                {
                    var worker = await StartWorkerAsync(cancellationToken);
                    ReturnToIdle(worker);
                }
                finally
                {
                    slots.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not pre-start a worker for tool '{ToolId}'", manifest.Id);
                break;
            }
        }
    }

    /// <summary>
    /// Takes a worker, waiting up to the queue budget for one. Past it: the caller's 503 +
    /// <c>Retry-After</c>, deliberately the same shape as every other saturation refusal in the
    /// project.
    /// </summary>
    public async Task<ToolWorkerLease> AcquireAsync(CancellationToken cancellationToken)
    {
        // The runtime already skips a suspended pool when it picks a candidate; this closes the
        // window where it is suspended between the pick and the acquire.
        if (Suspended)
        {
            throw new ToolUnavailableException(
                $"tool '{manifest.Id}' is switched off by the coordinator's node profile for this node");
        }

        if (Volatile.Read(ref gaveUpFlag) == 1)
        {
            throw new ToolUnavailableException(
                $"tool '{manifest.Id}' is not running: it failed to start {options.MaxStartAttempts} times and this node has stopped retrying it. It is still being probed.");
        }

        var budget = TimeSpan.FromSeconds(Math.Max(0, options.QueueMaxWaitSeconds));

        if (!await slots.WaitAsync(budget, cancellationToken))
        {
            throw new ToolBusyException(manifest.Id, manifest.MaxWorkers, options.QueueMaxWaitSeconds);
        }

        ToolWorkerProcess? worker = null;

        try
        {
            worker = TakeIdle();

            if (worker is null)
            {
                worker = await StartWorkerAsync(cancellationToken);
            }

            return new ToolWorkerLease(this, worker, manifest)
            {
                CancelGrace = TimeSpan.FromSeconds(Math.Max(1, options.CancelGraceSeconds))
            };
        }
        catch
        {
            if (worker is not null)
            {
                await worker.DisposeAsync();
            }

            slots.Release();
            throw;
        }
    }

    /// <summary>
    /// Retires idle workers past their timeout and, for a pool that has given up, tries one worker
    /// anyway. Driven by the runtime's maintenance loop; internal so the tests can tick it.
    /// </summary>
    public async Task MaintainAsync(CancellationToken cancellationToken)
    {
        // A suspended pool holds nothing to retire, and probing it would start the very worker the
        // profile switched off.
        if (Suspended)
        {
            return;
        }

        var now = time.GetUtcNow();
        var retire = new List<ToolWorkerProcess>();
        var hint = new List<ToolWorkerProcess>();

        lock (gate)
        {
            var keep = new List<ToolWorkerProcess>();

            while (idle.Count > 0)
            {
                var worker = idle.Pop();

                if (!worker.IsAlive)
                {
                    retire.Add(worker);
                    continue;
                }

                var gone = now - worker.LastUsed > manifest.IdleTimeout;

                // A rarely-used tool must not hold VRAM forever; WorkerFloor is the number below
                // which retiring would leave this pool unable to do its job at all.
                if (gone && keep.Count >= WorkerFloor)
                {
                    retire.Add(worker);
                    continue;
                }

                if (gone && !worker.IdleHinted)
                {
                    // Kept, but told (phase-48 D3). This is the last worker of a pool that must
                    // hold one, so retiring it is not an option — but a diffusion worker sitting on
                    // twelve gigabytes of VRAM nobody is using is exactly what the hint is for.
                    hint.Add(worker);
                }

                keep.Add(worker);
            }

            foreach (var worker in Enumerable.Reverse(keep))
            {
                idle.Push(worker);
            }
        }

        foreach (var worker in retire)
        {
            logger.LogInformation(
                "Retiring an idle worker for tool '{ToolId}' after {IdleTimeout}.",
                manifest.Id,
                manifest.IdleTimeout);

            await worker.DisposeAsync();
        }

        foreach (var worker in hint)
        {
            logger.LogInformation(
                "Tool '{ToolId}' has been idle for {IdleTimeout}; hinting it to free what it is holding. The process stays alive.",
                manifest.Id,
                manifest.IdleTimeout);

            if (await worker.HintIdleAsync(cancellationToken))
            {
                try
                {
                    WentIdle?.Invoke();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "An idle subscriber threw for tool '{ToolId}'", manifest.Id);
                }
            }
        }

        await ProbeIdleWorkersAsync(cancellationToken);

        if (Volatile.Read(ref gaveUpFlag) == 1 && now - lastRecoveryProbe >= options.RecoveryProbeInterval)
        {
            lastRecoveryProbe = now;
            await ProbeRecoveryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The ping/pong liveness probe on idle workers (v3.14.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Phase-41 D6 specified this and nothing ever called it.</b> <c>ToolWorkerProcess.PingAsync</c>
    /// was written, tested by nothing, and dead from v3.9.0 until v3.14.1 — found while fixing a
    /// different bug, which is how that class of thing is always found.
    /// </para>
    /// <para>
    /// It now earns its place twice: a worker that has wedged without exiting is retired here
    /// rather than at the expense of the next caller's queue budget, and — the reason it went in —
    /// an idle worker has <em>nobody reading its stdout</em>, so a late <c>ready</c> frame sits in
    /// the pipe until something drains it. This is that something, which is what makes a
    /// re-declaration reach the coordinator within one maintenance interval.
    /// </para>
    /// <para>
    /// <b>It takes the concurrency slot.</b> Without that, maintenance holding the only worker out
    /// of the idle stack would let a concurrent request conclude the pool is empty and start a
    /// second process — two copies of a multi-gigabyte model on one card, which is the
    /// out-of-memory error <c>MaxWorkers</c> exists to prevent. A slot that is already taken means
    /// the worker is busy serving, and a busy worker's own read loop picks the frame up anyway.
    /// </para>
    /// </remarks>
    private async Task ProbeIdleWorkersAsync(CancellationToken cancellationToken)
    {
        if (!await slots.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return;
        }

        ToolWorkerProcess? worker = null;

        try
        {
            worker = TakeIdle();

            if (worker is null)
            {
                return;
            }

            if (await worker.PingAsync(ProbeTimeout, cancellationToken))
            {
                ReturnToIdle(worker);
                worker = null;
                return;
            }

            logger.LogWarning(
                "An idle worker for tool '{ToolId}' did not answer a liveness probe; retiring it.",
                manifest.Id);
        }
        catch (OperationCanceledException)
        {
            if (worker is not null)
            {
                ReturnToIdle(worker);
                worker = null;
            }
        }
        finally
        {
            slots.Release();

            if (worker is not null)
            {
                await worker.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// How long an idle worker gets to answer a ping. Short on purpose: an idle worker is by
    /// definition doing nothing, so anything but an immediate answer means it is wedged.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public async ValueTask DisposeAsync()
    {
        List<ToolWorkerProcess> workers;
        Task[] pending;

        lock (gate)
        {
            workers = idle.ToList();
            idle.Clear();
            pending = terminations.Where(task => !task.IsCompleted).ToArray();
            terminations.Clear();
        }

        foreach (var worker in workers)
        {
            await worker.DisposeAsync();
        }

        // A failed worker is killed on a background task; waiting for it here is the difference
        // between a clean shutdown and an orphaned child process.
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A worker for tool '{ToolId}' did not finish shutting down", manifest.Id);
            }
        }

        slots.Dispose();
    }

    // ---- internals -----------------------------------------------------------------------------

    internal void ReleaseLease(ToolWorkerProcess worker, bool healthy)
    {
        Interlocked.Increment(ref requestsServed);

        if (!healthy)
        {
            Interlocked.Increment(ref requestsFailed);
        }

        if (healthy && worker.IsAlive)
        {
            ReturnToIdle(worker);
            slots.Release();
            return;
        }

        // A worker that failed its request is not put back into the pool: whatever wedged it is
        // still wedged, and handing it to the next caller turns one failed job into a queue of
        // them. It is *terminated* rather than disposed — the slot is released only once the
        // process is actually gone, so a polite five-second wait here is five seconds of the next
        // caller's queue budget spent on a worker that has already stopped answering.
        //
        // The task is *tracked*, not fire-and-forget: DisposeAsync waits for it. A shutdown that
        // raced this would leave an orphaned child process behind — which is exactly what it did,
        // and the leak was found by a build failing on a locked DLL rather than by any assertion.
        var termination = Task.Run(async () =>
        {
            try
            {
                await worker.TerminateAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not dispose a failed worker for tool '{ToolId}'", manifest.Id);
            }
            finally
            {
                slots.Release();
            }
        });

        lock (gate)
        {
            terminations.RemoveAll(task => task.IsCompleted);
            terminations.Add(termination);
        }
    }

    private void ReturnToIdle(ToolWorkerProcess worker)
    {
        lock (gate)
        {
            idle.Push(worker);
        }
    }

    private ToolWorkerProcess? TakeIdle()
    {
        while (true)
        {
            ToolWorkerProcess? worker;

            lock (gate)
            {
                if (idle.Count == 0)
                {
                    return null;
                }

                worker = idle.Pop();
            }

            if (worker.IsAlive)
            {
                return worker;
            }

            _ = worker.DisposeAsync();
        }
    }

    private async Task<ToolWorkerProcess> StartWorkerAsync(CancellationToken cancellationToken)
    {
        if (!TryConsumeStartBudget(out var attempt, out var backoff))
        {
            GiveUp();

            throw new ToolUnavailableException(
                $"tool '{manifest.Id}' failed to start {options.MaxStartAttempts} times within {options.RestartWindow}; this node has stopped retrying it.");
        }

        if (backoff > TimeSpan.Zero)
        {
            await Task.Delay(backoff, time, cancellationToken);
        }

        try
        {
            var worker = await ToolWorkerProcess.StartAsync(manifest, options.WorkerEnvironment(modelBudgetMiB), logger, cancellationToken);

            // v3.14.1. A worker may report a *different* set later than it did at handshake; the
            // narrowing clamp is applied to it exactly as it is at start, so this cannot widen
            // anything.
            worker.CapabilitiesRedeclared += () => RefreshCapabilities(worker);

            OnStartSucceeded(worker);
            return worker;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Tool '{ToolId}' failed to start (attempt {Attempt} of {Max} in this {Window} window).",
                manifest.Id,
                attempt,
                options.MaxStartAttempts,
                options.RestartWindow);

            RecordError(ex.Message);

            throw;
        }
    }

    /// <summary>
    /// One start attempt that deliberately does <em>not</em> consume the budget — it is the probe,
    /// and a pool that consumed budget to check whether it could stop being out of budget would
    /// never recover.
    /// </summary>
    private async Task ProbeRecoveryAsync(CancellationToken cancellationToken)
    {
        if (!await slots.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return;
        }

        try
        {
            var worker = await ToolWorkerProcess.StartAsync(manifest, options.WorkerEnvironment(modelBudgetMiB), logger, cancellationToken);

            logger.LogInformation(
                "Tool '{ToolId}' started again after giving up; its capabilities are back on this node.",
                manifest.Id);

            OnStartSucceeded(worker);
            ReturnToIdle(worker);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Recovery probe for tool '{ToolId}' failed", manifest.Id);
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>
    /// The cross-check (phase 48, D1): the operator declared a budget and the worker measured the
    /// card. Logged when they disagree materially, and <b>never</b> acted on.
    /// </summary>
    /// <remarks>
    /// Adopting the worker's number would be detecting VRAM after all, which is wrong under WSL2,
    /// wrong on a shared card and wrong the moment somebody else's process is on the GPU. What is
    /// worth having is the sentence an operator reads when a model they expected to fit does not:
    /// the two numbers, side by side, in one line.
    /// </remarks>
    private void CheckDeclaredVram(ToolWorkerProcess worker)
    {
        if (worker.ReportedVramTotalMiB is not { } measured || measured <= 0)
        {
            return;
        }

        ReportedVramTotalMiB = measured;

        if (declaredVramMiB <= 0)
        {
            return;
        }

        // A tenth is wide on purpose: reserved regions, ECC and the driver's own overhead mean a
        // card advertised as 24 GB reports about 23.5, and an operator who typed the number on the
        // box should not get a warning for being right.
        if (Math.Abs(declaredVramMiB - measured) * 10 <= measured)
        {
            return;
        }

        logger.LogWarning(
            "Node:Vram:BudgetMiB is {Declared} MiB but tool '{ToolId}' reports the card has {Measured} MiB. The declared figure is what this node budgets against — it is not overridden — so check which one is wrong.",
            declaredVramMiB,
            manifest.Id,
            measured);
    }

    private void OnStartSucceeded(ToolWorkerProcess worker)
    {
        lock (gate)
        {
            startFailures.Clear();
        }

        CheckDeclaredVram(worker);

        // Cleared, not accumulated: `lastError` means "the most recent thing that happened to this
        // pool was a failure". A pool that failed once at 3am and has served every request since
        // would otherwise carry a permanent warning on the console, which is how an operator learns
        // to ignore the column.
        lastError = null;
        Interlocked.Exchange(ref lastErrorAtTicks, 0);

        var resolved = Narrow(manifest.Capabilities, worker.ReportedCapabilities);
        var changed = Interlocked.Exchange(ref gaveUpFlag, 0) == 1 || !SameAs(Capabilities, resolved);

        gaveUpLogged = false;
        Capabilities = resolved;

        if (changed)
        {
            RaiseCapabilitiesChanged();
        }
    }

    /// <summary>
    /// Re-apply the narrowing clamp after a worker re-declared (v3.14.1), and tell the node only
    /// when the answer actually moved.
    /// </summary>
    private void RefreshCapabilities(ToolWorkerProcess worker)
    {
        var resolved = Narrow(manifest.Capabilities, worker.ReportedCapabilities);

        if (SameAs(Capabilities, resolved))
        {
            return;
        }

        Capabilities = resolved;
        RaiseCapabilitiesChanged();
    }

    private void GiveUp()
    {
        if (Interlocked.Exchange(ref gaveUpFlag, 1) == 1)
        {
            return;
        }

        Capabilities = Array.Empty<NodeCapability>();
        RecordError(
            $"failed to start {options.MaxStartAttempts} times in {options.RestartWindow}; not starting it again, still probing every {options.RecoveryProbeInterval}");

        if (!gaveUpLogged)
        {
            gaveUpLogged = true;
            logger.LogError(
                "Tool '{ToolId}' failed to start {Max} times in {Window}; not starting it again. Its capabilities are withdrawn from this node's registration, so the coordinator stops routing that work here. Probing continues every {Probe}, so a fix is noticed without a restart.",
                manifest.Id,
                options.MaxStartAttempts,
                options.RestartWindow,
                options.RecoveryProbeInterval);
        }

        RaiseCapabilitiesChanged();
    }

    private bool TryConsumeStartBudget(out int attempt, out TimeSpan backoff)
    {
        var now = time.GetUtcNow();

        lock (gate)
        {
            while (startFailures.Count > 0 && now - startFailures.Peek() > options.RestartWindow)
            {
                startFailures.Dequeue();
            }

            if (startFailures.Count >= options.MaxStartAttempts)
            {
                attempt = 0;
                backoff = TimeSpan.Zero;
                return false;
            }

            startFailures.Enqueue(now);
            attempt = startFailures.Count;
        }

        // Widen the gap between attempts: 0s, 10s, 20s at the defaults. A pool that respawns a
        // crashing worker every second is a machine that never gets to finish loading a model.
        backoff = attempt <= 1
            ? TimeSpan.Zero
            : options.RestartBackoff * Math.Pow(2, attempt - 2);

        return true;
    }

    private void RaiseCapabilitiesChanged()
    {
        try
        {
            CapabilitiesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A tool capability subscriber threw");
        }
    }

    /// <summary>
    /// A worker may <b>narrow</b> what its manifest claims and never widen it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Narrowing is real and needed — a Whisper worker finds one of the two model files the
    /// manifest names, and declaring the missing one would route requests at an error. Widening is
    /// refused for the same reason <c>Tools:Allowed</c> is a ceiling the hub cannot raise: the
    /// operator's file on the box is the authority on what this node may be asked to do, and a
    /// script that could add capabilities to its own node is a script that decides what traffic the
    /// fleet sends it.
    /// </para>
    /// <para>
    /// <b>Amended in phase 42: a declared capability with an <em>empty</em> model list is open on
    /// models and still closed on kind.</b> The TTS worker's models are voice files an operator
    /// dropped into a directory, and there is no list anybody could write in advance that would not
    /// be wrong the first time somebody added a voice — the drift phase-40 D2 refuses for backend
    /// models. What the ceiling was protecting survives intact: the manifest still decides that this
    /// tool may <c>speak</c> at all, and every name it reports corresponds to a file the operator
    /// put on the box. A worker can no more invent a voice than it could invent a manifest.
    /// A capability the manifest does not name is still refused, and this is the only widening
    /// anywhere in the runtime.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<NodeCapability> Narrow(
        IReadOnlyList<NodeCapability> declared,
        IReadOnlyList<NodeCapability>? reported)
    {
        if (reported is null || reported.Count == 0)
        {
            // Nothing reported: the manifest stands, minus any open-ended entry — an empty model
            // list means "ask the worker", and a worker that did not answer has not offered one.
            return declared.Where(capability => capability.Models.Count > 0).ToArray();
        }

        var narrowed = new List<NodeCapability>();

        foreach (var capability in declared)
        {
            var match = reported.FirstOrDefault(r =>
                string.Equals(r.Kind, capability.Kind, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                continue;
            }

            var models = capability.Models.Count == 0
                ? match.Models.ToArray()
                : capability.Models
                    .Where(model => match.Models.Any(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();

            if (models.Length > 0)
            {
                narrowed.Add(new NodeCapability(capability.Kind, models));
            }
        }

        return narrowed;
    }

    private static bool SameAs(IReadOnlyList<NodeCapability> a, IReadOnlyList<NodeCapability> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Kind, b[i].Kind, StringComparison.OrdinalIgnoreCase)
                || !a[i].Models.SequenceEqual(b[i].Models, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>An exclusive hold on one worker. Disposing it returns the worker or retires it.</summary>
public sealed class ToolWorkerLease : IAsyncDisposable
{
    private readonly ToolWorkerPool pool;
    private readonly ToolWorkerProcess worker;
    private readonly ToolManifest manifest;
    private bool healthy = true;

    internal ToolWorkerLease(ToolWorkerPool pool, ToolWorkerProcess worker, ToolManifest manifest)
    {
        this.pool = pool;
        this.worker = worker;
        this.manifest = manifest;
    }

    public string ToolId => manifest.Id;

    internal ToolManifest Manifest => manifest;

    public IAsyncEnumerable<ToolFrame> ExecuteAsync(ToolFrame request, CancellationToken cancellationToken)
        => worker.ExecuteAsync(request, cancellationToken);

    /// <summary>
    /// Asks the worker to stop, cooperatively (phase 47, D3). The worker stays leased and stays
    /// warm — whatever it answers arrives on the enumeration the caller is already reading.
    /// </summary>
    public Task<bool> CancelAsync(string requestId, CancellationToken cancellationToken)
        => worker.CancelAsync(requestId, cancellationToken);

    /// <summary>How long this tool's worker gets to honour a cancel before it is terminated.</summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Run when the lease is released, whatever became of the request (phase 48). The one consumer
    /// is the VRAM residency map: a model stops being <em>busy</em> the moment its request ends,
    /// even though its weights stay on the card.
    /// </summary>
    internal Action? Released { get; set; }

    /// <summary>Marks the worker as not fit to serve the next request; it is retired on release.</summary>
    public void MarkUnhealthy() => healthy = false;

    public ValueTask DisposeAsync()
    {
        // Before the pool release, and outside it: a residency map that stayed marked busy because
        // the pool threw would refuse every later request on this box, forever.
        Released?.Invoke();
        Released = null;

        pool.ReleaseLease(worker, healthy);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Every worker for a tool is busy and the queue budget expired.</summary>
internal sealed class ToolBusyException(string toolId, int maxWorkers, int waitedSeconds)
    : InvalidOperationException(
        $"tool '{toolId}' is at its worker limit ({maxWorkers}) and no worker freed up within {waitedSeconds}s")
{
    public string ToolId { get; } = toolId;
}

/// <summary>The tool exists on this node but is not currently runnable.</summary>
internal sealed class ToolUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// The model would fit this box but not beside what is running on it right now (phase 48, D2).
/// </summary>
/// <remarks>
/// Rendered as a <c>503</c> + <c>Retry-After</c>, the same status and header as every other
/// saturation refusal in the project — deliberately, so a client's retry logic behaves identically
/// whichever limit it hit. It is not a <c>ToolBusyException</c> because the pool had a free worker:
/// the card did not.
/// </remarks>
internal sealed class ToolVramExhaustedException(string message) : InvalidOperationException(message);

/// <summary>No tool on this node provides the requested (capability, model) pair.</summary>
internal sealed class ToolNotProvidedException(string capability, string model)
    : InvalidOperationException($"this node does not provide '{capability}' for model '{model}'")
{
    public string Capability { get; } = capability;

    public string Model { get; } = model;
}
