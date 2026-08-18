using System.Collections.Concurrent;

namespace InferHub.Coordinator.Observability;

public sealed class Metrics : InferHub.Shared.Vector.IRetrievalMetrics
{
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;

    private long requestsTotal;
    private long requestsInFlight;
    private long requestsCompleted;
    private long requestsFailed;
    private long failoversAttempted;
    private long failoversSucceeded;
    private long nodesEvicted;
    private long openAiRequestsTotal;
    private long fallbackDispatched;
    private long vectorReplicasHealed;
    private long vectorRebuildsFromRaw;
    private long vectorUnderReplicated;

    private volatile string? lastFallbackModel;
    private long lastFallbackAtTicks;

    private readonly ConcurrentDictionary<string, NodeCounter> perNode = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, VectorCollectionCounter> perCollection = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Kind, string Model), ToolUnitCounter> perAudio = new();

    /// <summary>Image jobs per recipe (phase 51): outcomes, and how long they took.</summary>
    private readonly ConcurrentDictionary<string, ImageJobCounter> perImageRecipe = new(StringComparer.Ordinal);

    public void RecordRequestStart(string nodeId)
    {
        Interlocked.Increment(ref requestsTotal);
        Interlocked.Increment(ref requestsInFlight);

        var counter = perNode.GetOrAdd(nodeId, _ => new NodeCounter());
        Interlocked.Increment(ref counter.Total);
        Interlocked.Increment(ref counter.InFlight);
    }

    public void RecordRequestComplete(string nodeId)
    {
        Interlocked.Increment(ref requestsCompleted);
        DecrementInFlight();

        if (perNode.TryGetValue(nodeId, out var counter))
        {
            Interlocked.Increment(ref counter.Completed);
            DecrementInFlight(counter);
        }
    }

    public void RecordRequestFail(string nodeId)
    {
        Interlocked.Increment(ref requestsFailed);
        DecrementInFlight();

        if (perNode.TryGetValue(nodeId, out var counter))
        {
            Interlocked.Increment(ref counter.Failed);
            DecrementInFlight(counter);
        }
    }

    public void RecordFailoverAttempted() => Interlocked.Increment(ref failoversAttempted);

    public void RecordFailoverSucceeded() => Interlocked.Increment(ref failoversSucceeded);

    public void RecordNodeEvicted() => Interlocked.Increment(ref nodesEvicted);

    // How much of the traffic arrives over the OpenAI dialect. One number — the per-node and
    // per-collection trees already exist and a third would be a metrics system, not a metric.
    public void RecordOpenAiRequest() => Interlocked.Increment(ref openAiRequestsTotal);

    /// <summary>
    /// A request left the fleet. The model name is recorded; the prompt and the answer are not,
    /// and never will be (rule 7). This counter is the thing that makes cloud burst visible
    /// rather than quiet, so it is surfaced on /api/status and the status page.
    /// </summary>
    public void RecordFallbackDispatched(string model)
    {
        Interlocked.Increment(ref fallbackDispatched);
        lastFallbackModel = model;
        Interlocked.Exchange(ref lastFallbackAtTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>
    /// Tool work that succeeded, in the unit it is actually in (phase-42 D7): seconds for a
    /// transcription, characters for a synthesis, megapixel-steps for a generated image (phase 46).
    /// The model name and a number — never the recording, never the transcript and never the prompt,
    /// which is rule 7 in its most literal form (phase-42 D5).
    /// </summary>
    /// <remarks>
    /// Each unit gets its own counter rather than one <c>units</c> sum, because a single sum would
    /// add seconds to characters to megapixel-steps and produce a number wrong in a way no reader
    /// can detect — the same reasoning <c>UsageAggregate</c> already applies to the ledger.
    /// </remarks>
    public void RecordToolUnits(string kind, string model, double units, string unitKind)
    {
        if (units <= 0)
        {
            return;
        }

        var counter = perAudio.GetOrAdd((kind, model), _ => new ToolUnitCounter());

        switch (unitKind)
        {
            case InferHub.Shared.Contracts.UsageUnitKinds.AudioSeconds:
                counter.Add(ref counter.Seconds, units);
                break;

            case InferHub.Shared.Contracts.UsageUnitKinds.Characters:
                counter.Add(ref counter.Characters, units);
                break;

            case InferHub.Shared.Contracts.UsageUnitKinds.MegapixelSteps:
                counter.Add(ref counter.MegapixelSteps, units);
                break;

            case InferHub.Shared.Contracts.UsageUnitKinds.VideoSeconds:
                counter.Add(ref counter.VideoSeconds, units);
                break;
        }
    }

    /// <summary>
    /// An image job that reached a terminal state (phase 51, D2): which recipe, how it ended, and
    /// how long it took from submission.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called from the one place a job finishes — <c>ImageJobRegistry</c> — for the same reason the
    /// unit counter is called from the one place a job succeeds: the number on a dashboard and the
    /// number on a bill must not come from two definitions of "done".
    /// </para>
    /// <para>
    /// <b>Every outcome is counted, not just success.</b> A fleet whose <c>failed</c> and
    /// <c>cancelled</c> counters were absent would look identical whether it was healthy or
    /// dropping every third render — and "how many of my jobs fail" is the first question anyone
    /// asks of a queue.
    /// </para>
    /// <para>
    /// <b><paramref name="media"/> is a label rather than a second series</b> (phase 59, D2). Video
    /// jobs have been in this counter since v3.25 with nothing to tell them apart, and a four-minute
    /// clip sharing a histogram with a nine-second picture makes both unreadable. The series keeps
    /// its name because these names are in other people's dashboards.
    /// </para>
    /// </remarks>
    public void RecordImageJob(string recipe, string outcome, double seconds, string? media = null)
    {
        var counter = perImageRecipe.GetOrAdd(recipe, _ => new ImageJobCounter());
        counter.Record(outcome, seconds, media);
    }

    public void RecordVectorReplicaHealed() => Interlocked.Increment(ref vectorReplicasHealed);

    public void RecordVectorRebuildFromRaw() => Interlocked.Increment(ref vectorRebuildsFromRaw);

    public void SetVectorUnderReplicated(long count) => Interlocked.Exchange(ref vectorUnderReplicated, Math.Max(0, count));

    public void RecordVectorQuery(string collection, TimeSpan elapsed)
    {
        var counter = perCollection.GetOrAdd(collection, _ => new VectorCollectionCounter());
        Interlocked.Increment(ref counter.Queries);
        var micros = (long)Math.Round(elapsed.TotalMilliseconds * 1000.0);
        Interlocked.Add(ref counter.QueryLatencyMicrosTotal, Math.Max(0, micros));
    }

    /// <summary>
    /// One document finished ingesting. Like everything else in <see cref="Metrics"/> these are
    /// **since-start** counters, not a census — they are named so, and the authoritative document
    /// count is whatever <c>GET /api/collections/{c}/documents</c> reads back out of the store.
    /// </summary>
    public void RecordDocumentIngested(string collection, string embeddingModel)
    {
        var counter = perCollection.GetOrAdd(collection, _ => new VectorCollectionCounter());
        Interlocked.Increment(ref counter.DocumentsIngested);
        counter.LastEmbeddingModel = embeddingModel;
        Interlocked.Exchange(ref counter.LastIngestAtTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void RecordChunksEmbedded(string collection, int count)
    {
        var counter = perCollection.GetOrAdd(collection, _ => new VectorCollectionCounter());
        Interlocked.Add(ref counter.ChunksEmbedded, Math.Max(0, count));
    }

    public void RecordIngestionFailure(string collection)
    {
        var counter = perCollection.GetOrAdd(collection, _ => new VectorCollectionCounter());
        Interlocked.Increment(ref counter.IngestionFailures);
    }

    public VectorCollectionMetricsSnapshot GetVectorCollectionSnapshot(string collection)
    {
        if (!perCollection.TryGetValue(collection, out var counter))
        {
            return new VectorCollectionMetricsSnapshot(collection, 0, 0);
        }
        return SnapshotOf(collection, counter);
    }

    public MetricsSnapshot Snapshot(DateTimeOffset now)
    {
        var perNodeSnapshot = perNode
            .Select(pair => new NodeMetricsSnapshot(
                pair.Key,
                Interlocked.Read(ref pair.Value.Total),
                Math.Max(0, Interlocked.Read(ref pair.Value.InFlight)),
                Interlocked.Read(ref pair.Value.Completed),
                Interlocked.Read(ref pair.Value.Failed)))
            .OrderBy(snapshot => snapshot.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var perCollectionSnapshot = perCollection
            .Select(pair => SnapshotOf(pair.Key, pair.Value))
            .OrderBy(snapshot => snapshot.Collection, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var perAudioSnapshot = perAudio
            .Select(pair =>
            {
                var (seconds, characters, megapixelSteps, videoSeconds) = pair.Value.Read();

                return new ToolUnitsSnapshot(
                    pair.Key.Kind, pair.Key.Model, seconds, characters, megapixelSteps, videoSeconds);
            })
            .OrderBy(snapshot => snapshot.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(snapshot => snapshot.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var lastFallbackTicks = Interlocked.Read(ref lastFallbackAtTicks);

        return new MetricsSnapshot(
            (now - StartedAtUtc).TotalSeconds,
            Interlocked.Read(ref requestsTotal),
            Math.Max(0, Interlocked.Read(ref requestsInFlight)),
            Interlocked.Read(ref requestsCompleted),
            Interlocked.Read(ref requestsFailed),
            Interlocked.Read(ref failoversAttempted),
            Interlocked.Read(ref failoversSucceeded),
            Interlocked.Read(ref nodesEvicted),
            Interlocked.Read(ref openAiRequestsTotal),
            Interlocked.Read(ref fallbackDispatched),
            lastFallbackModel,
            lastFallbackTicks == 0 ? null : new DateTimeOffset(lastFallbackTicks, TimeSpan.Zero),
            Interlocked.Read(ref vectorReplicasHealed),
            Interlocked.Read(ref vectorRebuildsFromRaw),
            Interlocked.Read(ref vectorUnderReplicated),
            perNodeSnapshot,
            perCollectionSnapshot,
            perAudioSnapshot,
            perImageRecipe
                .Select(pair => pair.Value.Read(pair.Key))
                .OrderBy(snapshot => snapshot.Recipe, StringComparer.Ordinal)
                .ToArray());
    }

    private static VectorCollectionMetricsSnapshot SnapshotOf(string collection, VectorCollectionCounter counter)
    {
        var queries = Interlocked.Read(ref counter.Queries);
        var micros = Interlocked.Read(ref counter.QueryLatencyMicrosTotal);
        var avgMs = queries == 0 ? 0.0 : micros / (double)queries / 1000.0;
        var lastIngestTicks = Interlocked.Read(ref counter.LastIngestAtTicks);

        return new VectorCollectionMetricsSnapshot(
            collection,
            queries,
            avgMs,
            Interlocked.Read(ref counter.DocumentsIngested),
            Interlocked.Read(ref counter.ChunksEmbedded),
            Interlocked.Read(ref counter.IngestionFailures),
            lastIngestTicks == 0 ? null : new DateTimeOffset(lastIngestTicks, TimeSpan.Zero),
            counter.LastEmbeddingModel);
    }

    private void DecrementInFlight()
    {
        if (Interlocked.Decrement(ref requestsInFlight) < 0)
        {
            Interlocked.Exchange(ref requestsInFlight, 0);
        }
    }

    private static void DecrementInFlight(NodeCounter counter)
    {
        if (Interlocked.Decrement(ref counter.InFlight) < 0)
        {
            Interlocked.Exchange(ref counter.InFlight, 0);
        }
    }

    private sealed class NodeCounter
    {
        public long Total;
        public long InFlight;
        public long Completed;
        public long Failed;
    }

    /// <summary>
    /// Two doubles under one lock. <c>Interlocked</c> has no <c>double</c> add, and a lock on a
    /// per-(kind, model) object is contended by exactly the audio requests for that pair — which are
    /// bounded by the tool runtime's worker count, i.e. one on almost every deployment.
    /// </summary>
    private sealed class ToolUnitCounter
    {
        private readonly object gate = new();

        public double Seconds;
        public double Characters;
        public double MegapixelSteps;

        public double VideoSeconds;

        public void Add(ref double field, double units)
        {
            lock (gate)
            {
                field += units;
            }
        }

        public (double Seconds, double Characters, double MegapixelSteps, double VideoSeconds) Read()
        {
            lock (gate)
            {
                return (Seconds, Characters, MegapixelSteps, VideoSeconds);
            }
        }
    }

    /// <summary>
    /// One recipe's image jobs: a count per outcome, and the duration total plus a small set of
    /// cumulative buckets (phase 51, D2).
    /// </summary>
    /// <remarks>
    /// <b>Buckets rather than an average</b>, because an average render time over a fleet that runs
    /// both <c>sdxl-turbo</c> at one step and <c>qwen-360</c> at twenty-five is a number describing
    /// nothing. The bucket bounds are fixed and few — a diffusion job's interesting range is
    /// seconds-to-minutes and nobody needs a histogram with thirty buckets to see it — and they are
    /// written out cumulatively because that is what the exposition format's <c>_bucket</c> series
    /// means.
    /// </remarks>
    private sealed class ImageJobCounter
    {
        /// <summary>Seconds. A 1-step turbo render lands in the first, a 20B panorama in the last.</summary>
        public static IReadOnlyList<double> Bounds => ImageJobBuckets.Bounds;

        private readonly object gate = new();
        private readonly Dictionary<string, long> outcomes = new(StringComparer.Ordinal);
        private readonly long[] buckets = new long[Bounds.Count];

        private long count;
        private double secondsTotal;

        /// <summary>
        /// What this recipe produces. A recipe is one medium for its whole life — a second geometry
        /// or a second quantization is a second id (58 D1) — so the first job to name it settles it,
        /// and a caller that names nothing leaves it at the default the label formats as
        /// <c>image</c>.
        /// </summary>
        private string? media;

        public void Record(string outcome, double seconds, string? recipeMedia)
        {
            lock (gate)
            {
                media ??= recipeMedia;
                outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
                count++;
                secondsTotal += Math.Max(0, seconds);

                for (var i = 0; i < Bounds.Count; i++)
                {
                    if (seconds <= Bounds[i])
                    {
                        buckets[i]++;
                    }
                }
            }
        }

        public ImageJobSnapshot Read(string recipe)
        {
            lock (gate)
            {
                return new ImageJobSnapshot(
                    recipe,
                    outcomes.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => new ImageOutcomeCount(pair.Key, pair.Value))
                        .ToArray(),
                    count,
                    secondsTotal,
                    buckets.ToArray(),
                    media);
            }
        }
    }

    private sealed class VectorCollectionCounter
    {
        public long Queries;
        public long QueryLatencyMicrosTotal;
        public long DocumentsIngested;
        public long ChunksEmbedded;
        public long IngestionFailures;
        public long LastIngestAtTicks;
        public volatile string? LastEmbeddingModel;
    }
}

public sealed record MetricsSnapshot(
    double UptimeSeconds,
    long RequestsTotal,
    long RequestsInFlight,
    long RequestsCompleted,
    long RequestsFailed,
    long FailoversAttempted,
    long FailoversSucceeded,
    long NodesEvicted,
    long OpenAiRequestsTotal,
    long FallbackDispatched,
    string? LastFallbackModel,
    DateTimeOffset? LastFallbackAtUtc,
    long VectorReplicasHealed,
    long VectorRebuildsFromRaw,
    long VectorUnderReplicated,
    IReadOnlyList<NodeMetricsSnapshot> PerNode,
    IReadOnlyList<VectorCollectionMetricsSnapshot> PerCollection,
    // Appended in phase 45 with a default, so every existing constructor call and every test that
    // builds a snapshot by hand keeps compiling and keeps meaning what it meant.
    IReadOnlyList<ToolUnitsSnapshot>? PerAudio = null,
    // Appended in phase 51 with a default, for the same reason PerAudio was in 45.
    IReadOnlyList<ImageJobSnapshot>? PerImageRecipe = null);

/// <summary>
/// One recipe's image jobs (phase 51): how they ended, and how long they took.
/// </summary>
/// <remarks>
/// A recipe nobody has rendered with produces <b>no entry at all</b> — phase-28 D5 for the sixth
/// time. A zero here would put "sd35-medium: 0 jobs, 0 seconds" on a dashboard for a model the
/// operator has never accepted the licence of and may never run.
/// </remarks>
public sealed record ImageJobSnapshot(
    string Recipe,
    IReadOnlyList<ImageOutcomeCount> Outcomes,
    long Count,
    double SecondsTotal,
    /// <summary>Cumulative counts against <c>ImageJobBuckets.Bounds</c>, in that order.</summary>
    IReadOnlyList<long> Buckets,
    /// <summary>
    /// <c>image</c> or <c>video</c> (phase 59, D2). Null on a snapshot taken before any job named it,
    /// and formatted as <c>image</c> — which is what every job in this counter was until v3.25.
    /// </summary>
    string? Media = null);

public sealed record ImageOutcomeCount(string Outcome, long Count);

/// <summary>The duration buckets, exposed so the formatter and its test read the same list.</summary>
public static class ImageJobBuckets
{
    public static IReadOnlyList<double> Bounds { get; } = [1, 5, 15, 60, 300];
}

/// <summary>
/// Tool work per <c>(kind, model)</c>, in whichever unit that kind is measured in. A pair that has
/// only ever transcribed has <see cref="Characters"/> at zero and emits no character series —
/// absence stays absence (phase-45 D2, phase-28 D5).
/// </summary>
/// <remarks>
/// It was <c>AudioMetricsSnapshot</c> until phase 46, and generalising it was cheaper than a second
/// dictionary keyed the same way: image generation is metered per <c>(kind, model)</c> in exactly
/// the same shape, and two parallel structures would be two places to forget a unit.
/// </remarks>
public sealed record ToolUnitsSnapshot(
    string Kind,
    string Model,
    double Seconds,
    double Characters,
    double MegapixelSteps = 0,
    double VideoSeconds = 0);

public sealed record NodeMetricsSnapshot(
    string NodeId,
    long RequestsTotal,
    long RequestsInFlight,
    long RequestsCompleted,
    long RequestsFailed);

/// <summary>
/// Per-collection counters. <see cref="DocumentsIngested"/> and <see cref="ChunksEmbedded"/> count
/// what this coordinator has done **since it started** — they are not a census of what is in the
/// store, and a restart resets them to zero exactly like every other counter here. The store's own
/// count is on the documents endpoint, which reads it.
/// </summary>
public sealed record VectorCollectionMetricsSnapshot(
    string Collection,
    long Queries,
    double QueryLatencyAvgMs,
    long DocumentsIngested = 0,
    long ChunksEmbedded = 0,
    long IngestionFailures = 0,
    DateTimeOffset? LastIngestAtUtc = null,
    string? LastEmbeddingModel = null);
