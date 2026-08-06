namespace InferHub.Shared.Contracts;

/// <summary>
/// The states an image job may be in (phase 47), and the transitions between them <b>as data</b>.
/// </summary>
/// <remarks>
/// <para>
/// It is a table rather than a <c>switch</c> in each host because both hosts and every test have to
/// agree on what is legal, and three copies of a state machine is how two of them drift. The one
/// transition that looks like a bug and is not is <c>cancelling → succeeded</c>: a job cancelled at
/// step 27 of 28 may finish anyway, and discarding a real image to honour a state name would be
/// worse than telling the truth (D3).
/// </para>
/// </remarks>
public static class ImageJobStates
{
    /// <summary>Accepted, in line, nothing spent. A queued job whose node vanishes is re-routed.</summary>
    public const string Queued = "queued";

    /// <summary>On a node. From here nothing is ever retried automatically (D7).</summary>
    public const string Running = "running";

    /// <summary>A cancel has been asked for and the worker has not answered yet.</summary>
    public const string Cancelling = "cancelling";

    public const string Succeeded = "succeeded";

    public const string Failed = "failed";

    public const string Cancelled = "cancelled";

    /// <summary>
    /// The result is gone: retention lapsed, the byte ceiling evicted it, or it was read.
    /// Rendered as a <c>410</c> that says which, rather than a <c>404</c> that reads like a bug.
    /// </summary>
    public const string Expired = "expired";

    private static readonly Dictionary<string, string[]> Legal = new(StringComparer.Ordinal)
    {
        [Queued] = [Running, Cancelled, Failed, Expired],
        [Running] = [Cancelling, Succeeded, Failed, Expired],
        [Cancelling] = [Cancelled, Succeeded, Failed, Expired],
        [Succeeded] = [Expired],
        [Failed] = [],
        [Cancelled] = [],
        [Expired] = []
    };

    /// <summary>Every state, in the order a reader meets them. The docs' state diagram is this list.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Queued, Running, Cancelling, Succeeded, Failed, Cancelled, Expired];

    /// <summary>
    /// Terminal for the <em>work</em>. <c>succeeded</c> is terminal and can still become
    /// <c>expired</c>, which is about the bytes rather than about the job.
    /// </summary>
    public static bool IsTerminal(string? state) =>
        state is Succeeded or Failed or Cancelled or Expired;

    public static bool CanTransition(string? from, string? to) =>
        from is not null
        && to is not null
        && Legal.TryGetValue(from, out var allowed)
        && Array.IndexOf(allowed, to) >= 0;
}

/// <summary>
/// Why a job is not going to produce an image. A short list on purpose: each one exists because a
/// client does something different about it, and a reason nobody acts on is a reason that is wrong
/// by the time somebody reads it.
/// </summary>
public static class ImageJobReasons
{
    /// <summary>The node holding a running job disappeared. <b>Never retried</b> (D7).</summary>
    public const string NodeLost = "node_lost";

    /// <summary>The client asked for it.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The retention window lapsed. The record survives; the bytes do not.</summary>
    public const string RetentionLapsed = "retention_lapsed";

    /// <summary>The byte ceiling evicted the oldest completed result to admit a new one.</summary>
    public const string Evicted = "evicted";

    /// <summary>Read once, and dropped on delivery. The default.</summary>
    public const string Delivered = "delivered";

    /// <summary>The worker or the node said no. The message carries their sentence.</summary>
    public const string WorkerError = "worker_error";
}
