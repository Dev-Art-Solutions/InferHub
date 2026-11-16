using System.Collections.Concurrent;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Control;

/// <summary>
/// Phase 150: the hub's jobs this node is running that somebody follows — what the node reports to its hub every
/// few seconds, and what <c>/api/control/jobs</c> shows the node's owner. A stage and a count; never the text.
/// </summary>
public sealed class NodeJobBoard(TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, FollowedJob> running = new();

    public FollowedJob Begin(Guid jobId, Guid followedId, string kind, string? model)
    {
        var job = new FollowedJob(jobId, followedId, kind, model, clock.GetUtcNow());
        running[jobId] = job;
        return job;
    }

    public void End(Guid jobId) => running.TryRemove(jobId, out _);

    /// <summary>The followed jobs running now, oldest first.</summary>
    public IReadOnlyList<FollowedJobView> Running
    {
        get
        {
            var now = clock.GetUtcNow();
            return [.. running.Values.OrderBy(j => j.StartedAt).Select(j => j.View(now))];
        }
    }
}

public sealed class FollowedJob(Guid jobId, Guid job, string kind, string? model, DateTimeOffset startedAt)
{
    private int pieces;

    /// <summary>This dispatch's id on the wire.</summary>
    public Guid JobId { get; } = jobId;

    /// <summary>The id the job's submitter holds at the hub.</summary>
    public Guid Job { get; } = job;

    public string Kind { get; } = kind;

    public string? Model { get; } = model;

    public DateTimeOffset StartedAt { get; } = startedAt;

    public int Pieces => Volatile.Read(ref pieces);

    public string Stage => Pieces == 0 ? InferenceJobProgress.Preparing : InferenceJobProgress.Generating;

    public void Took() => Interlocked.Increment(ref pieces);

    public FollowedJobView View(DateTimeOffset now) =>
        new(Job, Kind, Model, Stage, Pieces, StartedAt, (long)(now - StartedAt).TotalMilliseconds);
}

public sealed record FollowedJobView(Guid Job, string Kind, string? Model, string Stage, int Pieces, DateTimeOffset StartedAt, long ElapsedMs);
