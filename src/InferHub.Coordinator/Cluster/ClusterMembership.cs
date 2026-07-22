namespace InferHub.Coordinator.Cluster;

/// <summary>
/// The live role, driven by <see cref="ClusterLeaseService"/> and read by everything else. A
/// coordinator with <c>Cluster:Enabled=true</c> starts <b>standby</b> and is promoted only once it
/// actually holds the lease — starting active and demoting on the first failed acquire would give
/// every cold start a window in which two hubs believe they are primary, which is the one thing
/// this whole phase exists to prevent.
/// </summary>
internal sealed class ClusterMembership(string instanceId) : IClusterMembership
{
    private State state = new(ClusterRole.Standby, 0, null, "waiting for the coordinator lease");

    public bool Enabled => true;

    public ClusterRole Role => Volatile.Read(ref state).Role;

    public string InstanceId { get; } = instanceId;

    public long Fence => Volatile.Read(ref state).Fence;

    public DateTimeOffset? ActiveSinceUtc => Volatile.Read(ref state).ActiveSinceUtc;

    public string? Detail => Volatile.Read(ref state).Detail;

    /// <summary>Returns true when this was a transition, so the caller logs a promotion once.</summary>
    public bool MarkActive(long fence, DateTimeOffset nowUtc)
    {
        var current = Volatile.Read(ref state);
        var promoted = current.Role is not ClusterRole.Active;

        Volatile.Write(ref state, new State(
            ClusterRole.Active,
            fence,
            promoted ? nowUtc : current.ActiveSinceUtc,
            $"holding the lease (fence {fence})"));

        return promoted;
    }

    /// <summary>Returns true when this was a transition, so the caller logs a demotion once.</summary>
    public bool MarkStandby(string detail)
    {
        var current = Volatile.Read(ref state);
        var demoted = current.Role is ClusterRole.Active;

        Volatile.Write(ref state, new State(ClusterRole.Standby, current.Fence, null, detail));

        return demoted;
    }

    private sealed record State(
        ClusterRole Role,
        long Fence,
        DateTimeOffset? ActiveSinceUtc,
        string? Detail);
}
