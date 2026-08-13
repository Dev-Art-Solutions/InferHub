namespace InferHub.Tests;

/// <summary>
/// The mesh suites that move real megabytes, run one at a time (phase 53).
/// </summary>
/// <remarks>
/// <para>
/// <c>ImageJobTests</c> asserts on <em>queue position</em> and on a full queue answering 503 — a
/// claim about what happens within a bounded wait. Phase 53's upload tests push tens of megabytes
/// through a real hub, a real SignalR wire and a real child process, and two of those running beside
/// it were enough to turn an <c>Accepted</c> into a <c>ServiceUnavailable</c>: the queue genuinely
/// was full, because the box was busy.
/// </para>
/// <para>
/// <b>Serialising them is the honest fix, and loosening the timing would not be.</b> The queue test
/// is asserting the thing it exists to assert; what was wrong is that the machine had nothing left.
/// Everything else in this assembly still runs in parallel — this collection holds only the suites
/// whose cost is measured in megabytes.
/// </para>
/// </remarks>
[CollectionDefinition("heavy-mesh", DisableParallelization = true)]
public sealed class HeavyMeshCollection;
