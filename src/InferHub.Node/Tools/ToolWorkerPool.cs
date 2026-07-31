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
    private bool gaveUpLogged;
    private DateTimeOffset lastRecoveryProbe;

    public ToolWorkerPool(ToolManifest manifest, ToolOptions options, TimeProvider time, ILogger logger)
    {
        this.manifest = manifest;
        this.options = options;
        this.time = time;
        this.logger = logger;
        slots = new SemaphoreSlim(manifest.MaxWorkers, manifest.MaxWorkers);
        lastRecoveryProbe = time.GetUtcNow();
    }

    public ToolManifest Manifest => manifest;

    /// <summary>
    /// What this pool currently offers the mesh. Empty once the pool has given up, which is what
    /// unroutes the node for this capability — the same mechanism phase-36 D7 uses for a broken
    /// backend, reused rather than replaced by a health field.
    /// </summary>
    public IReadOnlyList<NodeCapability> Capabilities { get; private set; } = Array.Empty<NodeCapability>();

    /// <summary>Raised when <see cref="Capabilities"/> changes, so the node can re-report at once.</summary>
    public event Action? CapabilitiesChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Capabilities = manifest.Capabilities;

        for (var i = 0; i < manifest.MinWorkers; i++)
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

            return new ToolWorkerLease(this, worker, manifest);
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
        var now = time.GetUtcNow();
        var retire = new List<ToolWorkerProcess>();

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

                // A rarely-used tool must not hold VRAM forever; MinWorkers is the floor an
                // operator set precisely to avoid paying the load cost again.
                if (keep.Count >= manifest.MinWorkers && now - worker.LastUsed > manifest.IdleTimeout)
                {
                    retire.Add(worker);
                    continue;
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

        if (Volatile.Read(ref gaveUpFlag) == 1 && now - lastRecoveryProbe >= options.RecoveryProbeInterval)
        {
            lastRecoveryProbe = now;
            await ProbeRecoveryAsync(cancellationToken);
        }
    }

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
            var worker = await ToolWorkerProcess.StartAsync(manifest, logger, cancellationToken);
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
            var worker = await ToolWorkerProcess.StartAsync(manifest, logger, cancellationToken);

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

    private void OnStartSucceeded(ToolWorkerProcess worker)
    {
        lock (gate)
        {
            startFailures.Clear();
        }

        var resolved = Narrow(manifest.Capabilities, worker.ReportedCapabilities);
        var changed = Interlocked.Exchange(ref gaveUpFlag, 0) == 1 || !SameAs(Capabilities, resolved);

        gaveUpLogged = false;
        Capabilities = resolved;

        if (changed)
        {
            RaiseCapabilitiesChanged();
        }
    }

    private void GiveUp()
    {
        if (Interlocked.Exchange(ref gaveUpFlag, 1) == 1)
        {
            return;
        }

        Capabilities = Array.Empty<NodeCapability>();

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
    /// Narrowing is real and needed — a Whisper worker finds one of the two model files the
    /// manifest names, and declaring the missing one would route requests at an error. Widening is
    /// refused for the same reason <c>Tools:Allowed</c> is a ceiling the hub cannot raise: the
    /// operator's file on the box is the authority on what this node may be asked to do, and a
    /// script that could add capabilities to its own node is a script that decides what traffic the
    /// fleet sends it.
    /// </remarks>
    internal static IReadOnlyList<NodeCapability> Narrow(
        IReadOnlyList<NodeCapability> declared,
        IReadOnlyList<NodeCapability>? reported)
    {
        if (reported is null || reported.Count == 0)
        {
            return declared;
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

            var models = capability.Models
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

    /// <summary>Marks the worker as not fit to serve the next request; it is retired on release.</summary>
    public void MarkUnhealthy() => healthy = false;

    public ValueTask DisposeAsync()
    {
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

/// <summary>No tool on this node provides the requested (capability, model) pair.</summary>
internal sealed class ToolNotProvidedException(string capability, string model)
    : InvalidOperationException($"this node does not provide '{capability}' for model '{model}'")
{
    public string Capability { get; } = capability;

    public string Model { get; } = model;
}
