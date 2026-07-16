using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

public sealed class QueueOptions
{
    public const string SectionName = "Queue";

    /// <summary>How long a request may wait for a slot before 503. The bound is real (D5).</summary>
    public int MaxWaitSeconds { get; set; } = 30;

    /// <summary>How many requests may wait at once. Past it, an immediate 503.</summary>
    public int MaxDepth { get; set; } = 64;
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

public sealed record QueueSnapshot(int Depth, long Queued, long Admitted, long TimedOut, long Rejected, double? MedianWaitMs);

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
}

public interface IRequestQueue
{
    bool IsSaturated(string model);

    /// <summary>Waits for the model's fleet to free a slot, within the configured bounds.</summary>
    Task<QueueOutcome> WaitForCapacityAsync(string model, CancellationToken cancellationToken);

    int MaxWaitSeconds { get; }

    QueueSnapshot Snapshot();
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
            median);
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
