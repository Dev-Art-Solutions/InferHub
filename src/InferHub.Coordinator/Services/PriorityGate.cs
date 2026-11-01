using System.Collections.Concurrent;
using InferHub.Coordinator.Auth;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Phase 126. The three classes of work, highest first. A client's class is set by the operator (<c>Auth:Clients[].Priority</c>);
/// a request may ask for a lower one with <see cref="PriorityGate.Header"/>, never a higher one (126 D2).
/// </summary>
public static class PriorityClasses
{
    public const string Essential = "essential";

    public const string Normal = "normal";

    public const string Background = "background";

    public static readonly IReadOnlyList<string> All = [Essential, Normal, Background];

    /// <summary>2, 1, 0. Unknown or empty is <see cref="Normal"/> — an unset class is the behaviour of every release before this one.</summary>
    public static int Rank(string? priority) => Normalize(priority) switch
    {
        Essential => 2,
        Background => 0,
        _ => 1
    };

    public static string Normalize(string? priority) => priority?.Trim().ToLowerInvariant() switch
    {
        Essential => Essential,
        Background => Background,
        _ => Normal
    };

    public static bool IsKnown(string? priority)
        => priority is not null && All.Contains(priority.Trim().ToLowerInvariant());
}

/// <summary>Phase 126. <c>Priority:</c> — off by default; with it off every request is <c>normal</c> and nothing waits that did not before.</summary>
public sealed class PriorityOptions
{
    public const string SectionName = "Priority";

    public bool Enabled { get; set; }

    /// <summary>
    /// Free places for a model that background work may not take (126 D3). With the default 1, background work never takes
    /// the last free place: when nodes fail or fill up, it waits and the place is there for essential and normal work.
    /// </summary>
    public int ReserveSlots { get; set; } = 1;

    /// <summary>How long background work may wait for a place before a 503. Long on purpose: it is paused, not refused.</summary>
    public int BackgroundMaxWaitSeconds { get; set; } = 600;
}

public sealed class PriorityOptionsValidator : IValidateOptions<PriorityOptions>
{
    public ValidateOptionsResult Validate(string? name, PriorityOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (options.ReserveSlots is < 0 or > 1000)
        {
            failures.Add($"Priority:ReserveSlots {options.ReserveSlots} is out of range; use 0 to 1000.");
        }

        if (options.BackgroundMaxWaitSeconds is < 1 or > 86_400)
        {
            failures.Add($"Priority:BackgroundMaxWaitSeconds {options.BackgroundMaxWaitSeconds} is out of range; use 1 to 86400.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}

public enum PriorityOutcome
{
    Admitted,

    TimedOut
}

/// <summary>Per class: how many wait now, how many waited, were let through, and ran out of time. Absent unless <c>Priority:Enabled</c>.</summary>
public sealed record PriorityClassSnapshot(string Class, int Waiting, long Held, long Released, long TimedOut, long Requests);

public sealed record PrioritySnapshot(int ReserveSlots, int BackgroundMaxWaitSeconds, IReadOnlyList<PriorityClassSnapshot> Classes);

/// <summary>
/// Phase 126. Where a class of work is told to wait. Two rules, both only while <c>Priority:Enabled</c> (126 D1, D3, D4):
/// background work does not take a model's last <see cref="PriorityOptions.ReserveSlots"/> free places, and a request
/// waiting for capacity does not go ahead of a higher class waiting for the same model.
/// </summary>
public sealed class PriorityGate(INodeRegistry registry, IOptions<PriorityOptions> options, ILogger<PriorityGate> logger)
{
    /// <summary>A request may lower its client's class with this header; asking for a higher one is ignored (D2).</summary>
    public const string Header = "X-InferHub-Priority";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    // Waiters by model key and rank. Counts only — the queue that holds a request is still RequestQueue's or this gate's loop.
    private readonly ConcurrentDictionary<string, int[]> waiting = new(StringComparer.Ordinal);
    private readonly Counters[] counters = [new(), new(), new()];

    public bool Enabled => options.Value.Enabled;

    public PriorityOptions Options => options.Value;

    /// <summary>
    /// The class this request runs at: the client's, or a lower one the request asked for. <see cref="PriorityClasses.Normal"/>
    /// for everything while the feature is off, so nothing downstream can tell the difference.
    /// </summary>
    public string ClassOf(ResolvedClient client, string? requested)
    {
        if (!Enabled)
        {
            return PriorityClasses.Normal;
        }

        var own = PriorityClasses.Normalize(client.Priority);

        return PriorityClasses.IsKnown(requested) && PriorityClasses.Rank(requested) < PriorityClasses.Rank(own)
            ? PriorityClasses.Normalize(requested)
            : own;
    }

    /// <summary>Counted once per request that reached the gate, by class (the <c>requests</c> column).</summary>
    public void Counted(string priority) => Interlocked.Increment(ref counters[PriorityClasses.Rank(priority)].Requests);

    /// <summary>
    /// Free places for the model across the nodes that can take it now: per node the lesser of its declared
    /// <c>MaxConcurrency</c> left and the model's slots left (108). Null when some node declared neither — that node
    /// has no limit the hub can see, so there is nothing to reserve and background work is never held (D3).
    /// </summary>
    public int? FreePlaces(string model, string? capability)
    {
        var capable = registry.FindNodesWithModel(model, capability);
        var snapshots = registry.Snapshot(DateTimeOffset.UtcNow).ToDictionary(node => node.ConnectionId, StringComparer.Ordinal);
        var free = 0;

        foreach (var node in capable)
        {
            int? place = null;

            if (snapshots.TryGetValue(node.ConnectionId, out var snapshot) && snapshot.MaxConcurrency is { } cap)
            {
                place = Math.Max(0, cap - snapshot.LocalInFlight);
            }

            if (registry.GetModelSlots(node.ConnectionId, model) is { } slots)
            {
                var slotFree = Math.Max(0, slots - registry.GetModelInFlight(node.ConnectionId, model));
                place = place is { } capFree ? Math.Min(capFree, slotFree) : slotFree;
            }

            if (place is null)
            {
                return null;
            }

            free += place.Value;
        }

        return free;
    }

    /// <summary>Whether a request of a class above <paramref name="rank"/> is waiting for the same model.</summary>
    public bool HigherWaiting(string model, int rank)
    {
        if (!waiting.TryGetValue(Key(model), out var counts))
        {
            return false;
        }

        for (var r = rank + 1; r < counts.Length; r++)
        {
            if (Volatile.Read(ref counts[r]) > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Marks a request of this class as waiting for the model until disposed, so lower classes stand back (D4).</summary>
    public IDisposable Waiting(string model, string priority)
    {
        var counts = waiting.GetOrAdd(Key(model), _ => new int[3]);
        var rank = PriorityClasses.Rank(priority);
        Interlocked.Increment(ref counts[rank]);
        return new Leave(counts, rank);
    }

    /// <summary>
    /// Background work waits here while the model has no more than <see cref="PriorityOptions.ReserveSlots"/> free places,
    /// or while a higher class waits for the same model (D3). It never refuses early: the bound is
    /// <see cref="PriorityOptions.BackgroundMaxWaitSeconds"/>, and a caller who leaves is a cancellation.
    /// </summary>
    public async Task<PriorityOutcome> HoldBackgroundAsync(string model, string? capability, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        if (!settings.Enabled || MayGo(model, capability, settings))
        {
            return PriorityOutcome.Admitted;
        }

        var stats = counters[PriorityClasses.Rank(PriorityClasses.Background)];
        Interlocked.Increment(ref stats.Held);
        logger.LogInformation(
            "Holding background work for model {Model}: the last {Reserve} free place(s) are kept for essential and normal work",
            model,
            settings.ReserveSlots);

        using var _ = Waiting(model, PriorityClasses.Background);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(settings.BackgroundMaxWaitSeconds);

        while (true)
        {
            await Task.Delay(PollInterval, cancellationToken);

            if (MayGo(model, capability, settings))
            {
                Interlocked.Increment(ref stats.Released);
                return PriorityOutcome.Admitted;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                Interlocked.Increment(ref stats.TimedOut);
                logger.LogWarning(
                    "Background work for model {Model} waited {Seconds}s (Priority:BackgroundMaxWaitSeconds) and was not let through",
                    model,
                    settings.BackgroundMaxWaitSeconds);
                return PriorityOutcome.TimedOut;
            }
        }
    }

    /// <summary>A request of this class had to wait for capacity in <see cref="RequestQueue"/>.</summary>
    public void Queued(string priority) => Interlocked.Increment(ref counters[PriorityClasses.Rank(priority)].Held);

    /// <summary>A wait for capacity in <see cref="RequestQueue"/> that let the request through, by class.</summary>
    public void Released(string priority) => Interlocked.Increment(ref counters[PriorityClasses.Rank(priority)].Released);

    /// <summary>A wait for capacity in <see cref="RequestQueue"/> that ran out, by class.</summary>
    public void TimedOut(string priority) => Interlocked.Increment(ref counters[PriorityClasses.Rank(priority)].TimedOut);

    /// <summary>Null when off, so a hub without priority reports the payload and the scrape it reported before.</summary>
    public PrioritySnapshot? Snapshot()
    {
        if (!Enabled)
        {
            return null;
        }

        var now = new int[3];

        foreach (var counts in waiting.Values)
        {
            for (var r = 0; r < 3; r++)
            {
                now[r] += Math.Max(0, Volatile.Read(ref counts[r]));
            }
        }

        return new PrioritySnapshot(
            options.Value.ReserveSlots,
            options.Value.BackgroundMaxWaitSeconds,
            PriorityClasses.All.Select(name =>
            {
                var r = PriorityClasses.Rank(name);
                var c = counters[r];
                return new PriorityClassSnapshot(
                    name,
                    now[r],
                    Interlocked.Read(ref c.Held),
                    Interlocked.Read(ref c.Released),
                    Interlocked.Read(ref c.TimedOut),
                    Interlocked.Read(ref c.Requests));
            }).ToArray());
    }

    private bool MayGo(string model, string? capability, PriorityOptions settings)
        => !HigherWaiting(model, PriorityClasses.Rank(PriorityClasses.Background))
           && (FreePlaces(model, capability) is not { } free || free > settings.ReserveSlots);

    private static string Key(string model) => NodeRegistry.ModelKey(model) ?? model;

    private sealed class Counters
    {
        public long Held;
        public long Released;
        public long TimedOut;
        public long Requests;
    }

    private sealed class Leave(int[] counts, int rank) : IDisposable
    {
        private int done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 0)
            {
                Interlocked.Decrement(ref counts[rank]);
            }
        }
    }
}
