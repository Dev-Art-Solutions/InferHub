namespace InferHub.Coordinator.Cluster;

/// <summary>
/// The outcome of one acquire-or-renew attempt.
/// </summary>
/// <param name="Held">True when <b>this</b> instance holds the lease as of this round-trip.</param>
/// <param name="Fence">The acquisition counter of the current holder, whoever that is.</param>
/// <param name="Holder">Who holds it — us when <paramref name="Held"/>, otherwise the other one.</param>
/// <param name="ExpiresAtUtc">When the current holder's claim lapses, by the database clock.</param>
internal readonly record struct LeaseResult(
    bool Held,
    long Fence,
    string? Holder,
    DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// Mutual exclusion between coordinators, expressed as a lease one of them holds and renews.
/// The seam exists so <see cref="ClusterLeaseService"/>'s promotion, demotion and fencing logic
/// is testable without a database — the part that must never be wrong is the state machine, not
/// the SQL.
/// </summary>
internal interface IClusterLease
{
    /// <summary>
    /// Take the lease if it is free or already ours, and extend it. Returns
    /// <c>Held: false</c> when someone else holds an unexpired claim; throws only when the
    /// database could not be reached, which the caller treats as "unknown", not as "lost".
    /// </summary>
    Task<LeaseResult> TryAcquireOrRenewAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Give the lease up on a clean shutdown so the standby promotes in milliseconds instead of
    /// waiting out the TTL. Best-effort: a crash simply falls back to expiry, which is the case
    /// the TTL exists for.
    /// </summary>
    Task ReleaseAsync(CancellationToken cancellationToken);
}
