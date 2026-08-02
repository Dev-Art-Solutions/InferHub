using System.Collections.Concurrent;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

public interface IProfileRegistry
{
    IReadOnlyCollection<NodeProfile> All();

    NodeProfile? Get(string name);

    /// <summary>Writes a profile and bumps its revision. Returns what was stored.</summary>
    NodeProfile Put(string name, NodeProfile definition);

    bool Delete(string name);

    /// <summary>
    /// Which profile applies to a node, or the conflict that stops one from applying (D4).
    /// </summary>
    NodeProfileAssignment MatchFor(string nodeId, IReadOnlyDictionary<string, string>? labels);

    /// <summary>What a node last reported doing with its profile. Keyed on the stable node id.</summary>
    void ReportState(NodeProfileState state);

    NodeProfileState? StateOf(string nodeId);

    void Forget(string nodeId);
}

/// <summary>
/// The coordinator's profile book (phase 43): CRUD, monotonic revisions, exact-match selectors,
/// conflict detection, and the last state each node reported back.
/// </summary>
/// <remarks>
/// <para>
/// It holds no SignalR and dials nothing — sending a profile to a node is
/// <see cref="NodeProfileCoordinator"/>'s job, the way sending a model command is
/// <see cref="ModelCommandCoordinator"/>'s. Keeping the book separate from the wire is what lets the
/// convergence suite drive matching and conflict as plain functions.
/// </para>
/// <para>
/// <b>Revisions are monotonic per profile and never reused</b>, including across a delete and a
/// re-create under the same name: a node that had applied revision 4 of a deleted <c>gpu-boxes</c>
/// must not read a brand-new one as "already applied".
/// </para>
/// </remarks>
public sealed class ProfileRegistry : IProfileRegistry
{
    private readonly IProfileStore store;
    private readonly ILogger<ProfileRegistry> logger;
    private readonly ConcurrentDictionary<string, NodeProfile> profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> revisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, NodeProfileState> states = new(StringComparer.OrdinalIgnoreCase);

    public ProfileRegistry(IProfileStore store, ILogger<ProfileRegistry> logger)
    {
        this.store = store;
        this.logger = logger;

        foreach (var profile in store.Load())
        {
            profiles[profile.Name] = profile;
            revisions[profile.Name] = profile.Revision;
        }

        if (!profiles.IsEmpty)
        {
            logger.LogInformation(
                "Loaded {Count} node profile(s) from persistence: {Names}",
                profiles.Count,
                string.Join(", ", profiles.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)));
        }
    }

    public IReadOnlyCollection<NodeProfile> All() =>
        profiles.Values.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public NodeProfile? Get(string name) =>
        string.IsNullOrWhiteSpace(name) ? null : profiles.GetValueOrDefault(name.Trim());

    public NodeProfile Put(string name, NodeProfile definition)
    {
        var trimmed = name.Trim();
        var revision = revisions.AddOrUpdate(trimmed, 1, (_, current) => current + 1);

        var stored = definition with
        {
            Name = trimmed,
            Revision = revision,
            Selector = definition.Selector ?? new NodeProfileSelector()
        };

        profiles[trimmed] = stored;
        store.Save(stored);

        logger.LogInformation("Stored node profile '{Profile}' revision {Revision}", trimmed, revision);
        return stored;
    }

    public bool Delete(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();

        if (!profiles.TryRemove(trimmed, out _))
        {
            return false;
        }

        // The revision counter deliberately survives — see the remarks.
        store.Delete(trimmed);
        logger.LogInformation("Deleted node profile '{Profile}'", trimmed);
        return true;
    }

    public NodeProfileAssignment MatchFor(string nodeId, IReadOnlyDictionary<string, string>? labels)
    {
        var matches = profiles.Values
            .Where(profile => profile.Selector.Matches(nodeId, labels))
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return matches.Length switch
        {
            0 => NodeProfileAssignment.None,
            1 => new NodeProfileAssignment(matches[0]),

            // Not merged, and not resolved by creation order: silent precedence is how somebody's
            // node ends up in a state no single document explains. The operator fixes the selectors;
            // the node keeps what it last applied in the meantime.
            _ => NodeProfileAssignment.Conflicted(matches.Select(profile => profile.Name).ToArray())
        };
    }

    public void ReportState(NodeProfileState state)
    {
        if (string.IsNullOrWhiteSpace(state.NodeId))
        {
            return;
        }

        states[state.NodeId.Trim()] = state;
    }

    public NodeProfileState? StateOf(string nodeId) =>
        string.IsNullOrWhiteSpace(nodeId) ? null : states.GetValueOrDefault(nodeId.Trim());

    public void Forget(string nodeId)
    {
        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            states.TryRemove(nodeId.Trim(), out _);
        }
    }
}
