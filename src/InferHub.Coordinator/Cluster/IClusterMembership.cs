namespace InferHub.Coordinator.Cluster;

public enum ClusterRole
{
    Active,
    Standby
}

/// <summary>
/// What role this coordinator is playing right now. Everything that must behave differently on a
/// standby — the role header, the inference 503, the node handshake, the status block — reads it
/// from here and nowhere else, so there is exactly one answer to "am I the active hub?".
/// </summary>
public interface IClusterMembership
{
    /// <summary>False for a single-coordinator deployment, where the role is not a concept.</summary>
    bool Enabled { get; }

    ClusterRole Role { get; }

    bool IsActive => Role is ClusterRole.Active;

    string InstanceId { get; }

    /// <summary>
    /// Monotonic per acquisition. It exists so a promotion is distinguishable from a renewal in
    /// logs and in the status block: an old primary that comes back and sees a higher fence knows
    /// it was superseded rather than merely slow.
    /// </summary>
    long Fence { get; }

    DateTimeOffset? ActiveSinceUtc { get; }

    /// <summary>One human sentence for the status block: who holds the lease, or why we do not.</summary>
    string? Detail { get; }
}

/// <summary>
/// The default. A lone coordinator is always active, holds no lease, and opens no connection —
/// <see cref="Enabled"/> is false so the role never surfaces anywhere a v2.13 client could see it.
/// </summary>
public sealed class SingleCoordinatorMembership : IClusterMembership
{
    public bool Enabled => false;

    public ClusterRole Role => ClusterRole.Active;

    public string InstanceId => Environment.MachineName;

    public long Fence => 0;

    public DateTimeOffset? ActiveSinceUtc => null;

    public string? Detail => null;
}
