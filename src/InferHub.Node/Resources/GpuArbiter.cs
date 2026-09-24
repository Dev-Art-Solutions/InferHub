using InferHub.Node.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Resources;

/// <summary>
/// <c>Node:OnDemand</c> (phase 85): the card belongs to <b>one service at a time</b>, is taken when a
/// request for that service arrives, and is given back — weights unloaded, worker processes gone —
/// once that service has been quiet for <see cref="OnDemandOptions.ReleaseAfterSeconds"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A "tenant" is a thing that holds VRAM between requests</b>: the inference backend (Ollama keeps
/// a model resident after answering) and each tool pool (a warm Whisper, Piper or diffusion worker).
/// Requests for the tenant that owns the card share it freely — two chats run side by side, bounded
/// by the concurrency limits that already exist. A request for a <em>different</em> tenant waits
/// until the owner's in-flight work drains, then the owner is released and the newcomer takes the
/// card. Nothing in flight is ever evicted: the swap happens between jobs, never inside one.
/// </para>
/// <para>
/// <b>First come, first served across tenants.</b> Once somebody is waiting for the card, a new
/// request for the current owner queues behind them rather than joining — otherwise a steady trickle
/// of chat would starve an image job forever. Waiters for the same tenant are granted together.
/// </para>
/// <para>
/// <b>Unset is byte-identical to v3.49.</b> With <see cref="OnDemandOptions.Enabled"/> false every
/// acquire returns at once and nothing is ever released by this class; the existing idle hint
/// (phase-48 D3) and Ollama's own <c>keep_alive</c> remain the only residency policy.
/// </para>
/// </remarks>
public sealed class GpuArbiter : IAsyncDisposable
{
    private readonly OnDemandOptions options;
    private readonly TimeProvider time;
    private readonly ILogger<GpuArbiter> logger;

    private readonly object gate = new();
    private readonly Dictionary<string, Func<CancellationToken, Task>> releasers = new(StringComparer.Ordinal);
    private readonly LinkedList<Waiter> waiters = new();
    private readonly CancellationTokenSource lifetime = new();

    private string? owner;
    private int active;
    private bool switching;
    private CancellationTokenSource? linger;
    private Task pending = Task.CompletedTask;

    public GpuArbiter(IOptions<NodeOptions> nodeOptions, TimeProvider time, ILogger<GpuArbiter> logger)
    {
        options = nodeOptions.Value.OnDemand;
        this.time = time;
        this.logger = logger;
    }

    public bool Enabled => options.Enabled;

    /// <summary>Who holds the card right now, or null when nothing does. For status and tests.</summary>
    public string? Owner
    {
        get
        {
            lock (gate)
            {
                return owner;
            }
        }
    }

    /// <summary>
    /// How a tenant gives the card back: unload its weights, stop its processes. Called with no
    /// request of that tenant in flight, and awaited before the next tenant is admitted, so the peak
    /// is never both at once.
    /// </summary>
    public void RegisterReleaser(string tenant, Func<CancellationToken, Task> release)
    {
        lock (gate)
        {
            releasers[tenant] = release;
        }
    }

    /// <summary>
    /// Takes the card for <paramref name="tenant"/>, waiting up to
    /// <see cref="OnDemandOptions.SwitchWaitSeconds"/> for another tenant's work to finish.
    /// Disposing the lease is the only way to give it back.
    /// </summary>
    /// <exception cref="GpuBusyException">Another service held the card for the whole wait.</exception>
    public async Task<IAsyncDisposable> AcquireAsync(string tenant, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return NoLease.Instance;
        }

        Waiter waiter;

        lock (gate)
        {
            if (!switching && waiters.Count == 0 && (owner is null || owner == tenant))
            {
                if (owner is null)
                {
                    logger.LogInformation("On-demand: '{Tenant}' takes the GPU.", tenant);
                }

                owner = tenant;
                active++;
                CancelLinger();
                return new Lease(this, tenant);
            }

            waiter = new Waiter(tenant);
            waiters.AddLast(waiter);

            // The owner is lingering with nothing in flight: nobody else is going to start the swap.
            if (!switching && active == 0)
            {
                BeginSwitch();
            }
        }

        logger.LogInformation(
            "On-demand: '{Tenant}' is waiting for the GPU, which '{Owner}' holds.",
            tenant,
            Owner ?? "nothing");

        try
        {
            await waiter.Granted.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(0, options.SwitchWaitSeconds)), time, cancellationToken);
            return new Lease(this, tenant);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            bool granted;

            lock (gate)
            {
                granted = !waiters.Remove(waiter);
            }

            if (granted)
            {
                // Granted in the same instant it gave up: the count was already taken for it.
                Release(tenant);
            }

            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw new GpuBusyException(
                $"this node runs its GPU on demand (Node:OnDemand) and '{Owner ?? "another service"}' held it for the whole {options.SwitchWaitSeconds}s wait; '{tenant}' was not started");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();

        Task last;

        lock (gate)
        {
            CancelLinger();
            last = pending;
        }

        try
        {
            await last.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "On-demand release did not finish during shutdown");
        }

        lifetime.Dispose();
    }

    private void Release(string tenant)
    {
        lock (gate)
        {
            active--;

            if (active > 0)
            {
                return;
            }

            if (waiters.Count > 0)
            {
                BeginSwitch();
                return;
            }

            var delay = TimeSpan.FromSeconds(Math.Max(0, options.ReleaseAfterSeconds));

            if (delay == TimeSpan.Zero)
            {
                BeginSwitch();
                return;
            }

            CancelLinger();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            linger = cts;
            _ = LingerAsync(tenant, delay, cts);
        }
    }

    private async Task LingerAsync(string tenant, TimeSpan delay, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(delay, time, cts.Token);

            lock (gate)
            {
                if (ReferenceEquals(linger, cts) && active == 0 && !switching && owner == tenant)
                {
                    linger = null;
                    BeginSwitch();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(linger, cts))
                {
                    linger = null;
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>Caller holds <see cref="gate"/>, and nothing of the owner's is in flight.</summary>
    private void BeginSwitch()
    {
        CancelLinger();
        switching = true;
        pending = SwitchAsync(owner);
    }

    private async Task SwitchAsync(string? previous)
    {
        // Off the caller's stack: a release is an HTTP call to Ollama or a process exit, and the
        // lease that triggered it is being disposed inside somebody's request.
        await Task.Yield();

        if (previous is not null)
        {
            Func<CancellationToken, Task>? release;

            lock (gate)
            {
                releasers.TryGetValue(previous, out release);
            }

            if (release is not null)
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(60));
                    await release(deadline.Token);
                    logger.LogInformation("On-demand: '{Tenant}' released the GPU.", previous);
                }
                catch (Exception ex)
                {
                    // Logged and moved past: a release that failed must not wedge the card for
                    // every other service. The next tenant may meet an OOM, which names itself.
                    logger.LogWarning(ex, "On-demand: '{Tenant}' did not release the GPU cleanly.", previous);
                }
            }
        }

        lock (gate)
        {
            switching = false;
            owner = null;

            if (waiters.First is not { } first)
            {
                return;
            }

            var next = first.Value.Tenant;
            owner = next;

            var node = waiters.First;

            while (node is not null)
            {
                var following = node.Next;

                if (node.Value.Tenant == next)
                {
                    waiters.Remove(node);
                    active++;
                    node.Value.Granted.TrySetResult();
                }

                node = following;
            }

            logger.LogInformation("On-demand: '{Tenant}' takes the GPU.", next);
        }
    }

    private void CancelLinger()
    {
        if (linger is null)
        {
            return;
        }

        linger.Cancel();
        linger = null;
    }

    private sealed class Waiter(string tenant)
    {
        public string Tenant { get; } = tenant;

        public TaskCompletionSource Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease(GpuArbiter arbiter, string tenant) : IAsyncDisposable
    {
        private int disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                arbiter.Release(tenant);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoLease : IAsyncDisposable
    {
        public static readonly NoLease Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Another service held the card for the whole on-demand wait. Rendered as the same <c>503</c> +
/// <c>Retry-After</c> as every other saturation refusal on this node.
/// </summary>
public sealed class GpuBusyException(string message) : InvalidOperationException(message);
