using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Vector.Replication;
using Microsoft.AspNetCore.SignalR;

namespace InferHub.Coordinator.Vector;

/// <summary>
/// Phase 77. Gives a node-owned collection a second home, so its owning node's permanent loss does
/// not lose the collection. <b>The hub relays; it never stores</b> (D1) — the sentence
/// <see cref="CollectionOwnership.RefusalFor"/> already states ("this hub deliberately holds no copy
/// of it") is not worked around here, it is kept literally true: every method below either forwards a
/// frame it just received to another connection, or writes a profile through the exact same path an
/// admin's own edit takes (D2). Nothing here buffers a node's vectors to disk or in memory beyond the
/// single message in flight.
/// </summary>
public sealed class NodeCorpusReplicator(
    CollectionOwnership ownership,
    IHubContext<NodeHub> hub,
    INodeRegistry registry,
    IProfileRegistry profiles,
    NodeProfileCoordinator coordinator,
    IAuditLog audit,
    ILogger<NodeCorpusReplicator> logger)
{
    /// <summary>
    /// Assigns a standby for a node-owned collection and kicks off the first snapshot. The admin
    /// endpoint calls this; so does nothing else — there is exactly one way in.
    /// </summary>
    public async Task<(bool Ok, string? Error)> AssignStandbyAsync(string collection, string standbyNodeId, string by, CancellationToken cancellationToken)
    {
        var primaryNodeId = ownership.NodeOwning(collection);

        if (primaryNodeId is null)
        {
            return (false, $"collection '{collection}' is owned by {CollectionOwnership.Hub}; a standby only makes sense for a node-owned collection");
        }

        var standby = registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.NodeId, standbyNodeId, StringComparison.OrdinalIgnoreCase));

        if (standby is null)
        {
            return (false, $"standby node '{standbyNodeId}' is not connected");
        }

        var primary = registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.NodeId, primaryNodeId, StringComparison.OrdinalIgnoreCase));

        if (primary is null)
        {
            return (false, $"'{collection}' is owned by node '{primaryNodeId}', which is not connected right now; connect it before assigning a standby");
        }

        var refusal = ownership.AssignStandby(collection, standbyNodeId);

        if (refusal is not null)
        {
            return (false, refusal);
        }

        audit.Record(standbyNodeId, $"corpus.standby.assign:{collection}@{primaryNodeId}", by, DateTimeOffset.UtcNow);
        logger.LogInformation(
            "Standby assigned: '{Collection}' owned by {Primary} will be relayed to {Standby}",
            collection, primaryNodeId, standbyNodeId);

        await RequestSnapshotAsync(primary.ConnectionId, collection);
        return (true, null);
    }

    public void ClearStandby(string collection, string by)
    {
        var standbyNodeId = ownership.StandbyNodeOf(collection);
        ownership.ClearStandby(collection);

        if (standbyNodeId is null)
        {
            return;
        }

        audit.Record(standbyNodeId, $"corpus.standby.clear:{collection}", by, DateTimeOffset.UtcNow);
        logger.LogInformation("Standby cleared for '{Collection}' (was {Standby})", collection, standbyNodeId);

        var standby = registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.NodeId, standbyNodeId, StringComparison.OrdinalIgnoreCase));

        if (standby is not null)
        {
            // Best-effort: tell the (former) standby to drop what it was holding. If it is not
            // connected right now it still has a stale copy on disk; that is a leftover to clean up
            // by hand, not a correctness problem — nothing will ever route reads to it.
            _ = hub.Clients.Client(standby.ConnectionId).SendAsync("DropVectorReplica", collection);
        }
    }

    /// <summary>
    /// A primary (re)connected. If it owns a collection with a standby assigned, ask it to push a
    /// fresh snapshot — the in-memory relay wiring this class implies does not survive a hub restart
    /// (rule 4), so re-deriving it from "who owns what, who is the standby" on every reconnect is the
    /// whole recovery story rather than a special case of it.
    /// </summary>
    public async Task OnPrimaryReconnectedAsync(string nodeId, string connectionId)
    {
        var owned = ownership.NodeOwned()
            .Where(pair => string.Equals(pair.Value, $"node:{nodeId}", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var collection in owned)
        {
            if (ownership.StandbyNodeOf(collection) is not null)
            {
                await RequestSnapshotAsync(connectionId, collection);
            }
        }
    }

    /// <summary>A primary pushed its full snapshot (in response to <see cref="RequestSnapshotAsync"/>, or unprompted on reconnect). Relayed to the standby via the same <c>AssignVectorReplica</c> a hub-owned replica already uses — the standby's <c>ReplicaStore</c> needs no new code to receive this.</summary>
    public async Task HandleSnapshotAsync(string primaryNodeId, VectorReplicaAssignment snapshot)
    {
        if (!VerifyPrimary(primaryNodeId, snapshot.Collection, out var standbyConnectionId))
        {
            return;
        }

        await hub.Clients.Client(standbyConnectionId!).SendAsync("AssignVectorReplica", snapshot);
        logger.LogInformation(
            "Relayed snapshot of '{Collection}' ({Records} records) from {Primary} to its standby",
            snapshot.Collection, snapshot.Records.Count, primaryNodeId);
    }

    /// <summary>One live write, forwarded the moment the primary reports it. Reused wire shape: the standby applies it through the exact same <c>ApplyVectorOp</c> handler a hub-owned replica's tail already runs.</summary>
    public async Task HandleOpAsync(string primaryNodeId, VectorReplicaOp op)
    {
        if (!VerifyPrimary(primaryNodeId, op.Collection, out var standbyConnectionId))
        {
            return;
        }

        await hub.Clients.Client(standbyConnectionId!).SendAsync("ApplyVectorOp", op);
    }

    public async Task HandleDropAsync(string primaryNodeId, string collection)
    {
        if (!VerifyPrimary(primaryNodeId, collection, out var standbyConnectionId))
        {
            return;
        }

        await hub.Clients.Client(standbyConnectionId!).SendAsync("DropVectorReplica", collection);
        ownership.ClearStandby(collection);
        logger.LogInformation("'{Collection}' was dropped on its primary {Primary}; standby relay stopped", collection, primaryNodeId);
    }

    /// <summary>
    /// Told by <c>CorpusFailoverService</c> that the primary has been gone past the grace period.
    /// Asks the standby to detach its held replica into its own corpus directory — the hub does not
    /// do this step itself, because it is a filesystem move on the standby's own disk (D1: the hub
    /// never held the bytes to move).
    /// </summary>
    public async Task PromoteAsync(string collection, CancellationToken cancellationToken)
    {
        var standbyNodeId = ownership.StandbyNodeOf(collection);

        if (standbyNodeId is null)
        {
            return;
        }

        var standby = registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.NodeId, standbyNodeId, StringComparison.OrdinalIgnoreCase));

        if (standby is null)
        {
            logger.LogWarning(
                "Cannot promote standby for '{Collection}': node {Standby} is not connected either",
                collection, standbyNodeId);
            return;
        }

        logger.LogWarning(
            "Promoting standby {Standby} for '{Collection}' — its owner has been unreachable past the grace period",
            standbyNodeId, collection);

        await hub.Clients.Client(standby.ConnectionId).SendAsync("PromoteCorpusReplica", collection, cancellationToken);
    }

    /// <summary>
    /// The standby reports what happened to its promotion attempt. On success the collection becomes
    /// a real, profile-recorded assignment on the standby — through <see cref="IProfileRegistry.Put"/>
    /// exactly as an admin's own <c>.../collections/{c}/assign</c> would (D2) — rather than a second,
    /// undocumented way for a node to end up owning a name. The dead primary's own profile document
    /// is best-effort scrubbed of the same collection so a later reconnect cannot reclaim a name its
    /// data no longer backs (the exact hazard phase-77's research found in <c>Rebuild</c>).
    /// </summary>
    public async Task OnPromotedAsync(string standbyNodeId, string collection, bool success, string? error, CancellationToken cancellationToken)
    {
        if (!success)
        {
            logger.LogWarning("Promotion of '{Collection}' on {Standby} failed: {Error}", collection, standbyNodeId, error);
            return;
        }

        var oldPrimaryNodeId = ownership.NodeOwning(collection);

        var standby = registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.NodeId, standbyNodeId, StringComparison.OrdinalIgnoreCase));

        if (standby is null)
        {
            logger.LogWarning("Standby {Standby} reported a successful promotion of '{Collection}' but has since disconnected; its profile was not updated", standbyNodeId, collection);
            return;
        }

        var (resolveError, name, existing) = ResolveProfileForNode(standby);

        if (resolveError is not null)
        {
            logger.LogWarning("Could not record promotion of '{Collection}' on {Standby}: {Error}", collection, standbyNodeId, resolveError);
            return;
        }

        var collections = (existing.Retrieval?.Collections ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c) && !string.Equals(c.Trim(), collection, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Trim())
            .Append(collection)
            .ToArray();

        var updatedRetrieval = existing.Retrieval is { } r
            ? r with { Enabled = true, Provider = string.IsNullOrWhiteSpace(r.Provider) ? "local" : r.Provider, Collections = collections }
            : new RetrievalProfile(Enabled: true, Provider: "local", Collections: collections);

        var stored = profiles.Put(name, existing with { Retrieval = updatedRetrieval });

        // Best-effort: strip the collection from the dead primary's own profile too, so it cannot
        // reclaim a name its data no longer backs if it ever reconnects (non-goal §4: no automatic
        // failback, but a *silent* reclaim would be worse than the stray-copy case that section names).
        if (oldPrimaryNodeId is not null && !string.Equals(oldPrimaryNodeId, standbyNodeId, StringComparison.OrdinalIgnoreCase))
        {
            var oldAssignment = profiles.MatchFor(oldPrimaryNodeId, null);

            if (!oldAssignment.IsConflict && oldAssignment.Profile is { Retrieval.Collections: { } oldCollections } oldProfile
                && oldCollections.Any(c => string.Equals(c?.Trim(), collection, StringComparison.OrdinalIgnoreCase)))
            {
                var trimmed = oldCollections
                    .Where(c => !string.IsNullOrWhiteSpace(c) && !string.Equals(c.Trim(), collection, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                profiles.Put(oldProfile.Name, oldProfile with { Retrieval = oldProfile.Retrieval! with { Collections = trimmed } });
                logger.LogInformation("Removed '{Collection}' from the old owner {Primary}'s profile '{Profile}'", collection, oldPrimaryNodeId, oldProfile.Name);
            }
        }

        await coordinator.ReassertAsync(cancellationToken);
        ownership.ClearStandby(collection);

        audit.Record(standbyNodeId, $"corpus.promoted:{collection}", "corpus-failover", DateTimeOffset.UtcNow);
        logger.LogWarning(
            "'{Collection}' promoted: {Standby} is now the owner (was {OldPrimary}), via profile '{Profile}' revision {Revision}",
            collection, standbyNodeId, oldPrimaryNodeId ?? "(unknown)", stored.Name, stored.Revision);
    }

    private async Task RequestSnapshotAsync(string primaryConnectionId, string collection) =>
        await hub.Clients.Client(primaryConnectionId).SendAsync("RequestCorpusSnapshot", collection);

    /// <summary>
    /// Every relay call is checked against current ownership before it moves a byte: a stray or
    /// stale message from a node that is not (or is no longer) this collection's recorded owner is
    /// dropped rather than forwarded, so a mis-behaving or superseded node cannot overwrite a standby
    /// out of band.
    /// </summary>
    private bool VerifyPrimary(string primaryNodeId, string collection, out string? standbyConnectionId)
    {
        standbyConnectionId = null;

        if (!string.Equals(ownership.NodeOwning(collection), primaryNodeId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var standbyNodeId = ownership.StandbyNodeOf(collection);

        if (standbyNodeId is null)
        {
            return false;
        }

        var standby = registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.NodeId, standbyNodeId, StringComparison.OrdinalIgnoreCase));

        if (standby is null)
        {
            return false;
        }

        standbyConnectionId = standby.ConnectionId;
        return true;
    }

    /// <summary>Same shape as <c>NodeModelToggle.ResolveProfileForNode</c> and <c>AdminEndpoints</c>'s own copy — which named profile a per-node write should patch, creating a fresh <c>node:{id}</c> skeleton if none matches yet.</summary>
    private (string? Error, string Name, NodeProfile Profile) ResolveProfileForNode(NodeSnapshot node)
    {
        var assignment = profiles.MatchFor(node.NodeId, node.Labels);

        if (assignment.IsConflict)
        {
            return ($"node '{node.NodeId}' matches {assignment.Conflicts!.Count} profiles; resolve the conflict in the raw profile editor first", string.Empty, null!);
        }

        if (assignment.Profile is { } existing)
        {
            return (null, existing.Name, existing);
        }

        var name = $"node:{node.NodeId}";
        return (null, name, new NodeProfile(name, 0, new NodeProfileSelector(NodeId: node.NodeId)));
    }
}
