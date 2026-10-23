using System.Text.Json;

namespace InferHub.Node.Control;

/// <summary>One job, as the control API shows it (phase 112, D4): never its content.</summary>
public sealed record ActivityEntry(
    Guid Id,
    string Source,
    string Kind,
    string? Model,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt = null,
    string? Outcome = null)
{
    public double? Seconds => EndedAt is { } end ? Math.Round((end - StartedAt).TotalSeconds, 3) : null;
}

/// <summary>Something the node did that a watcher wants to see as it happens (phase 112).</summary>
public sealed record NodeEvent(long Seq, DateTimeOffset At, string Type, object? Data);

/// <summary>
/// What this node is doing (phase 112, D4): the jobs running now and the last ones finished, from the hub and
/// from the local API alike, plus a short event log the control API streams. In memory, like every other
/// count on a node (rule 4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Told where both paths meet.</b> <see cref="InferenceExecutor"/> and <see cref="Tools.ToolExecutor"/> run every
/// job a node runs, whoever sent it, so they report here; the mesh connection marks its own with
/// <see cref="FromMesh"/>, an <see cref="AsyncLocal{T}"/>, so neither path has to know the other exists.
/// </para>
/// <para>
/// <b>Rule 7:</b> a kind, a model name, a source and the timing. Never a prompt, never a file name.
/// </para>
/// </remarks>
public sealed class NodeActivity(TimeProvider? time = null)
{
    public const string Mesh = "mesh";

    public const string Local = "local";

    private const int RecentCapacity = 200;

    private const int EventCapacity = 500;

    private static readonly AsyncLocal<string?> CurrentSource = new();

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<Guid, ActivityEntry> running = new();
    private readonly LinkedList<ActivityEntry> recent = new();
    private readonly LinkedList<NodeEvent> events = new();
    private long sequence;
    private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Raised when a job starts or ends, outside the lock — what sharing (113) watches.</summary>
    public event Action<ActivityEntry>? Changed;

    /// <summary>Marks the work started in this async flow as the hub's, until disposed.</summary>
    public static IDisposable FromMesh()
    {
        var previous = CurrentSource.Value;
        CurrentSource.Value = Mesh;
        return new Restore(previous);
    }

    /// <summary>The jobs running now, oldest first.</summary>
    public IReadOnlyList<ActivityEntry> Running
    {
        get
        {
            lock (gate)
            {
                return [.. running.Values.OrderBy(e => e.StartedAt)];
            }
        }
    }

    /// <summary>The last finished jobs, newest first.</summary>
    public IReadOnlyList<ActivityEntry> Recent
    {
        get
        {
            lock (gate)
            {
                return [.. recent];
            }
        }
    }

    public int RunningCount(string source)
    {
        lock (gate)
        {
            return running.Values.Count(e => e.Source == source);
        }
    }

    /// <summary>Starts counting a job; dispose the scope to end it, with <see cref="Scope.Fail"/> first if it did not succeed.</summary>
    public Scope Begin(string kind, string? model)
    {
        var entry = new ActivityEntry(Guid.NewGuid(), CurrentSource.Value ?? Local, kind, model, clock.GetUtcNow());

        lock (gate)
        {
            running[entry.Id] = entry;
        }

        Publish("job.started", entry);
        Changed?.Invoke(entry);
        return new Scope(this, entry);
    }

    /// <summary>Adds an event to the stream the control API serves.</summary>
    public void Publish(string type, object? data)
    {
        TaskCompletionSource wake;

        lock (gate)
        {
            events.AddLast(new NodeEvent(++sequence, clock.GetUtcNow(), type, data));

            while (events.Count > EventCapacity)
            {
                events.RemoveFirst();
            }

            wake = signal;
            signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        wake.TrySetResult();
    }

    /// <summary>The events after <paramref name="seq"/> still held, oldest first.</summary>
    public IReadOnlyList<NodeEvent> EventsAfter(long seq)
    {
        lock (gate)
        {
            return [.. events.Where(e => e.Seq > seq)];
        }
    }

    /// <summary>Completes when an event newer than <paramref name="seq"/> exists, or the token ends.</summary>
    public async Task WaitAsync(long seq, CancellationToken cancellationToken)
    {
        Task next;

        lock (gate)
        {
            if (sequence > seq)
            {
                return;
            }

            next = signal.Task;
        }

        await next.WaitAsync(cancellationToken);
    }

    /// <summary>The model a request names, read without deserialising the rest of it; null when it names none.</summary>
    public static string? ModelOf(string? requestJson)
    {
        if (string.IsNullOrEmpty(requestJson))
        {
            return null;
        }

        try
        {
            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(requestJson));

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.CurrentDepth >= 1)
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                {
                    var isModel = reader.ValueTextEquals("model");
                    reader.Read();

                    if (isModel)
                    {
                        return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    }

                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private void End(ActivityEntry entry, string outcome)
    {
        var ended = entry with { EndedAt = clock.GetUtcNow(), Outcome = outcome };

        lock (gate)
        {
            if (!running.Remove(entry.Id))
            {
                return;
            }

            recent.AddFirst(ended);

            while (recent.Count > RecentCapacity)
            {
                recent.RemoveLast();
            }
        }

        Publish("job.finished", ended);
        Changed?.Invoke(ended);
    }

    /// <summary>One running job. Disposing it ends it as <c>ok</c> unless <see cref="Fail"/> said otherwise.</summary>
    public sealed class Scope : IDisposable
    {
        private readonly NodeActivity owner;
        private readonly ActivityEntry entry;
        private string outcome = "ok";
        private int ended;

        internal Scope(NodeActivity owner, ActivityEntry entry)
        {
            this.owner = owner;
            this.entry = entry;
        }

        public ActivityEntry Entry => entry;

        public void Fail(string outcome) => this.outcome = outcome;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref ended, 1) == 0)
            {
                owner.End(entry, outcome);
            }
        }
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => CurrentSource.Value = previous;
    }
}
