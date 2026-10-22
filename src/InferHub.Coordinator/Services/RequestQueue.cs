using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

public sealed class QueueOptions
{
    public const string SectionName = "Queue";

    /// <summary>How long a request may wait for a slot before 503. The bound is real (D5).</summary>
    public int MaxWaitSeconds { get; set; } = 30;

    /// <summary>How many requests may wait at once. Past it, an immediate 503.</summary>
    public int MaxDepth { get; set; } = 64;

    /// <summary>
    /// Phase 110 D1. How long a request may wait at the hub while every node holding its model runs as
    /// many of its jobs as it declared slots for. <c>0</c> (the default) sends it straight on, as v3.73
    /// did. Past the bound it is sent anyway — never a 503 (D2).
    /// </summary>
    public int SlotWaitSeconds { get; set; }
}

public enum QueueOutcome
{
    /// <summary>A slot freed; re-route and dispatch.</summary>
    Admitted,

    /// <summary>Waited the full bound; 503 with Retry-After.</summary>
    TimedOut,

    /// <summary>The queue itself is full; immediate 503.</summary>
    QueueFull
}

public sealed record QueueSnapshot(
    int Depth,
    long Queued,
    long Admitted,
    long TimedOut,
    long Rejected,
    double? MedianWaitMs,
    SlotWaitSnapshot? SlotWait = null);

/// <summary>Phase 110. Null when <c>Queue:SlotWaitSeconds</c> is 0, so a status consumer sees no new key.</summary>
public sealed record SlotWaitSnapshot(int Depth, long Waited, long Admitted, long SentAnyway);

/// <summary>
/// Where saturation is *defined*, once. Every node advertising the model is at its <b>declared</b>
/// <c>MaxConcurrency</c>. A node that declared no cap is never saturated — there is no number to
/// compare against, and inventing one would queue (or burst to a paid upstream) on a guess.
/// Cloud burst and the request queue must agree on this, so they share it.
/// </summary>
public static class FleetSaturation
{
    /// <summary>
    /// Zero capable nodes count as saturated — this is cloud burst's question ("can the fleet
    /// serve this at all?"). The queue asks <see cref="HasSaturatedFleet"/> instead: waiting
    /// for a slot only makes sense when there are nodes to free one.
    /// </summary>
    public static bool IsSaturated(INodeRegistry registry, string model)
        => registry.FindNodesWithModel(model).Count == 0 || HasSaturatedFleet(registry, model);

    /// <summary>At least one node holds the model, and every one of them is at its declared cap.</summary>
    public static bool HasSaturatedFleet(INodeRegistry registry, string model)
    {
        var capable = registry.FindNodesWithModel(model);

        if (capable.Count == 0)
        {
            return false;
        }

        var snapshots = registry
            .Snapshot(DateTimeOffset.UtcNow)
            .ToDictionary(node => node.ConnectionId, StringComparer.Ordinal);

        foreach (var node in capable)
        {
            if (!snapshots.TryGetValue(node.ConnectionId, out var snapshot)
                || snapshot.MaxConcurrency is not { } cap
                || snapshot.LocalInFlight < cap)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Phase 110 D1, and phase 108 D1's "free" — the one definition both read. A node is free for a
    /// model unless it declared a slot count for it and runs that many of the hub's jobs for it; a node
    /// that declared nothing is always free. True only when somebody holds the model and nobody is free.
    /// </summary>
    public static bool HasNoFreeSlot(INodeRegistry registry, string model, string? capability = null)
    {
        var capable = registry.FindNodesWithModel(model, capability);
        return capable.Count > 0 && !capable.Any(node => HasFreeSlot(registry, node, model));
    }

    public static bool HasFreeSlot(INodeRegistry registry, RoutableNode node, string model)
        => registry.GetModelSlots(node.ConnectionId, model) is not { } slots
           || registry.GetModelInFlight(node.ConnectionId, model) < slots;
}

public interface IRequestQueue
{
    bool IsSaturated(string model);

    /// <summary>Waits for the model's fleet to free a slot, within the configured bounds.</summary>
    Task<QueueOutcome> WaitForCapacityAsync(string model, CancellationToken cancellationToken);

    int MaxWaitSeconds { get; }

    /// <summary>
    /// Phase 110. Waits, within <c>Queue:SlotWaitSeconds</c>, until a node has a free slot for the
    /// model. Never refuses: whatever happens, the caller dispatches next. The ticket must be disposed
    /// once the dispatch has been <em>started</em> — the next waiter for the model is let go then (D3).
    /// </summary>
    Task<IDisposable> WaitForFreeSlotAsync(string model, string? capability, CancellationToken cancellationToken)
        => Task.FromResult(SlotTicket.None);

    QueueSnapshot Snapshot();
}

/// <summary>What <see cref="IRequestQueue.WaitForFreeSlotAsync"/> hands back: the model's turnstile, or nothing.</summary>
public sealed class SlotTicket(SemaphoreSlim? turnstile) : IDisposable
{
    public static readonly IDisposable None = new SlotTicket(null);

    private SemaphoreSlim? turnstile = turnstile;

    public void Dispose() => Interlocked.Exchange(ref turnstile, null)?.Release();
}

/// <summary>
/// A bounded wait for a saturated fleet (phase 25, D5). When every capable node is at its
/// declared cap, a request waits here — up to <c>Queue:MaxWaitSeconds</c>, no more than
/// <c>Queue:MaxDepth</c> at a time — instead of failing instantly. Past either bound it is a
/// 503 with Retry-After. An unbounded queue is a memory leak that presents as a latency
/// problem three hours later.
///
/// The wait polls the registry's in-flight counters rather than subscribing to completion
/// events: the counters are already the source of truth, and 200 ms of extra latency on a
/// request that was about to be rejected outright is not the part that needs optimising.
/// </summary>
public sealed class RequestQueue(
    INodeRegistry registry,
    IOptions<QueueOptions> options,
    ILogger<RequestQueue> logger) : IRequestQueue
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    // Only the head of a model's line polls (D3), so a shorter interval costs one registry read per
    // model, not one per waiter — and it is latency on a request that will be answered.
    private static readonly TimeSpan SlotPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> turnstiles = new(StringComparer.Ordinal);
    private int slotDepth;
    private long slotWaited;
    private long slotAdmitted;
    private long slotSentAnyway;

    private int depth;
    private long queued;
    private long admitted;
    private long timedOut;
    private long rejected;

    // Last N wait durations, for the status page's median. A ring buffer, not a histogram —
    // one number an operator can read is worth more than a distribution nobody asked for.
    private readonly object waitsGate = new();
    private readonly double[] waitSamplesMs = new double[128];
    private int waitSampleCount;
    private int waitSampleNext;

    public bool IsSaturated(string model) => FleetSaturation.HasSaturatedFleet(registry, model);

    public int MaxWaitSeconds => Math.Max(0, options.Value.MaxWaitSeconds);

    public async Task<QueueOutcome> WaitForCapacityAsync(string model, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var maxDepth = Math.Max(1, settings.MaxDepth);

        if (Interlocked.Increment(ref depth) > maxDepth)
        {
            Interlocked.Decrement(ref depth);
            Interlocked.Increment(ref rejected);
            logger.LogWarning("Request queue is full ({MaxDepth}); rejecting request for model {Model}", maxDepth, model);
            return QueueOutcome.QueueFull;
        }

        Interlocked.Increment(ref queued);
        var started = DateTimeOffset.UtcNow;
        var deadline = started.AddSeconds(Math.Max(0, settings.MaxWaitSeconds));

        try
        {
            while (true)
            {
                if (!FleetSaturation.HasSaturatedFleet(registry, model))
                {
                    Interlocked.Increment(ref admitted);
                    RecordWait(started);
                    return QueueOutcome.Admitted;
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    Interlocked.Increment(ref timedOut);
                    RecordWait(started);
                    logger.LogWarning(
                        "Request for model {Model} waited {Seconds}s for fleet capacity and timed out",
                        model,
                        settings.MaxWaitSeconds);
                    return QueueOutcome.TimedOut;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        finally
        {
            Interlocked.Decrement(ref depth);
        }
    }

    public async Task<IDisposable> WaitForFreeSlotAsync(string model, string? capability, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        if (settings.SlotWaitSeconds <= 0 || !FleetSaturation.HasNoFreeSlot(registry, model, capability))
        {
            return SlotTicket.None;
        }

        // D2: over the depth bound the request goes on as it would have before, not to a 503.
        if (Interlocked.Increment(ref slotDepth) > Math.Max(1, settings.MaxDepth))
        {
            Interlocked.Decrement(ref slotDepth);
            Interlocked.Increment(ref slotSentAnyway);
            return SlotTicket.None;
        }

        Interlocked.Increment(ref slotWaited);
        var turnstile = turnstiles.GetOrAdd(NodeRegistry.ModelKey(model) ?? model, _ => new SemaphoreSlim(1, 1));
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(settings.SlotWaitSeconds));
        var holding = false;

        try
        {
            await turnstile.WaitAsync(bound.Token);
            holding = true;

            while (FleetSaturation.HasNoFreeSlot(registry, model, capability))
            {
                await Task.Delay(SlotPollInterval, bound.Token);
            }

            Interlocked.Increment(ref slotAdmitted);
            holding = false;
            return new SlotTicket(turnstile);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref slotSentAnyway);
            logger.LogInformation(
                "No node freed a slot for model {Model} within {Seconds}s; sending the request on to queue in an engine",
                model,
                settings.SlotWaitSeconds);
            return SlotTicket.None;
        }
        finally
        {
            if (holding)
            {
                turnstile.Release();
            }

            Interlocked.Decrement(ref slotDepth);
        }
    }

    public QueueSnapshot Snapshot()
    {
        double? median = null;

        lock (waitsGate)
        {
            if (waitSampleCount > 0)
            {
                var samples = waitSamplesMs.Take(waitSampleCount).OrderBy(v => v).ToArray();
                median = samples[samples.Length / 2];
            }
        }

        return new QueueSnapshot(
            Math.Max(0, Volatile.Read(ref depth)),
            Interlocked.Read(ref queued),
            Interlocked.Read(ref admitted),
            Interlocked.Read(ref timedOut),
            Interlocked.Read(ref rejected),
            median,
            options.Value.SlotWaitSeconds > 0
                ? new SlotWaitSnapshot(
                    Math.Max(0, Volatile.Read(ref slotDepth)),
                    Interlocked.Read(ref slotWaited),
                    Interlocked.Read(ref slotAdmitted),
                    Interlocked.Read(ref slotSentAnyway))
                : null);
    }

    private void RecordWait(DateTimeOffset started)
    {
        var waited = (DateTimeOffset.UtcNow - started).TotalMilliseconds;

        lock (waitsGate)
        {
            waitSamplesMs[waitSampleNext] = waited;
            waitSampleNext = (waitSampleNext + 1) % waitSamplesMs.Length;
            waitSampleCount = Math.Min(waitSampleCount + 1, waitSamplesMs.Length);
        }
    }
}
