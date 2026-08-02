using InferHub.Shared.Contracts;

namespace InferHub.Node.Profiles;

/// <summary>
/// The ceiling, enforced. A pure function from (what this box allows, what the hub wants) to (what
/// this node will actually do, what it refused and why).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the decision the whole second half of the track turns on (D1), and it runs on the
/// node.</b> A clamp that runs on the hub is a clamp an attacker skips by not being the hub: the
/// point of the exercise is that a compromised or misconfigured coordinator cannot turn a fleet of
/// GPU boxes into arbitrary code execution. The hub may also validate, for a better error message;
/// this is the copy that is load-bearing, and <c>ProfileClampTests</c> drives it with hostile
/// profiles.
/// </para>
/// <para>
/// It is pure — no I/O, no logging, no options monitor, nothing to mock. Everything it needs about
/// the box arrives in <see cref="LocalCeiling"/>, which is why the adversarial suite can be a table
/// of inputs rather than a host.
/// </para>
/// <para>
/// Same shape as every other consent decision in the repo — phase-22 D5's model map, phase-31 D1's
/// collection scope, phase-36 D6's separate install key, phase-41 D2's <c>Tools:Allowed</c> — applied
/// to the one place where the consequence is code execution rather than data.
/// </para>
/// </remarks>
public static class NodeProfileClamp
{
    /// <summary>
    /// Applies <paramref name="desired"/> against <paramref name="local"/>. Never throws: a hostile
    /// profile produces refusals, because a node that fell over on a bad instruction would be a
    /// coordinator's denial of service against its own fleet.
    /// </summary>
    public static ClampResult Apply(LocalCeiling local, NodeProfile? desired)
    {
        if (desired is null)
        {
            // No profile: the box runs its own configuration, which is the state it boots in.
            return new ClampResult(
                new EffectiveProfile(local.DisabledCapabilities, Array.Empty<string>(), local.MaxConcurrency),
                Array.Empty<string>(),
                Array.Empty<NodeProfileRefusal>(),
                Array.Empty<string>(),
                Array.Empty<string>());
        }

        var applied = new List<string>();
        var refusals = new List<NodeProfileRefusal>();

        var disabledCapabilities = new HashSet<string>(local.DisabledCapabilities, StringComparer.OrdinalIgnoreCase);
        var disabledTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ClampCapabilities(local, desired, disabledCapabilities, applied, refusals);
        ClampTools(local, desired, disabledTools, applied, refusals);

        var concurrency = ClampConcurrency(local, desired, applied, refusals);
        var (ensure, remove) = ClampModels(local, desired, applied, refusals);

        return new ClampResult(
            new EffectiveProfile(
                disabledCapabilities.OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase).ToArray(),
                disabledTools.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
                concurrency),
            applied,
            refusals,
            ensure,
            remove);
    }

    /// <summary>
    /// <c>false</c> narrows and always works. <c>true</c> cannot re-open what the box closed — the
    /// operator wrote <c>Node:Capabilities:Disabled</c> on the machine, and a hub that could undo it
    /// would make that key a suggestion.
    /// </summary>
    private static void ClampCapabilities(
        LocalCeiling local,
        NodeProfile desired,
        HashSet<string> disabled,
        List<string> applied,
        List<NodeProfileRefusal> refusals)
    {
        if (desired.Capabilities is null)
        {
            return;
        }

        foreach (var pair in desired.Capabilities)
        {
            var kind = pair.Key?.Trim();

            if (string.IsNullOrEmpty(kind))
            {
                continue;
            }

            if (!pair.Value)
            {
                disabled.Add(kind);
                applied.Add($"capability '{kind}' off");
                continue;
            }

            if (local.DisabledCapabilities.Any(d => string.Equals(d, kind, StringComparison.OrdinalIgnoreCase)))
            {
                refusals.Add(new NodeProfileRefusal(
                    $"capability:{kind}",
                    $"Node:Capabilities:Disabled names '{kind}' on this node; a profile can narrow what a node serves, never widen it"));

                continue;
            }

            disabled.Remove(kind);
            applied.Add($"capability '{kind}' on");
        }
    }

    /// <summary>
    /// <c>Tools:Allowed</c> is the ceiling this phase can never raise, which is why phase-41 D2 made
    /// it a list rather than a second boolean. A tool id that is not on it — including one that is
    /// a path, an interpreter or a command line somebody hoped would be run — is refused by name.
    /// </summary>
    private static void ClampTools(
        LocalCeiling local,
        NodeProfile desired,
        HashSet<string> disabled,
        List<string> applied,
        List<NodeProfileRefusal> refusals)
    {
        if (desired.Tools is null)
        {
            return;
        }

        foreach (var pair in desired.Tools)
        {
            var toolId = pair.Key?.Trim();

            if (string.IsNullOrEmpty(toolId))
            {
                continue;
            }

            if (!pair.Value)
            {
                // Switching off is narrowing, so it is honoured even for an id this node has never
                // heard of: the answer to "stop running that" is never "I decline to not run it".
                disabled.Add(toolId);
                applied.Add($"tool '{toolId}' off");
                continue;
            }

            if (!local.ToolsEnabled)
            {
                refusals.Add(new NodeProfileRefusal(
                    $"tool:{toolId}",
                    "Tools:Enabled is false on this node; a profile cannot switch the tool runtime on"));

                continue;
            }

            if (!local.AllowedTools.Any(allowed => string.Equals(allowed, toolId, StringComparison.OrdinalIgnoreCase)))
            {
                refusals.Add(new NodeProfileRefusal(
                    $"tool:{toolId}",
                    $"Tools:Allowed on this node does not name '{toolId}'; that list is the operator's grant and a profile cannot add to it"));

                continue;
            }

            disabled.Remove(toolId);
            applied.Add($"tool '{toolId}' on");
        }
    }

    /// <summary>
    /// Lowering is a preference; raising is a claim about hardware the operator owns and this
    /// process does not.
    /// </summary>
    private static int? ClampConcurrency(
        LocalCeiling local,
        NodeProfile desired,
        List<string> applied,
        List<NodeProfileRefusal> refusals)
    {
        if (desired.MaxConcurrency is not { } wanted)
        {
            return local.MaxConcurrency;
        }

        if (wanted < 1)
        {
            refusals.Add(new NodeProfileRefusal(
                "maxConcurrency",
                $"maxConcurrency {wanted} is not a usable limit; it must be at least 1"));

            return local.MaxConcurrency;
        }

        if (local.MaxConcurrency is { } cap && wanted > cap)
        {
            refusals.Add(new NodeProfileRefusal(
                "maxConcurrency",
                $"Node:MaxConcurrency on this node is {cap}; a profile can lower it, never raise it to {wanted}"));

            return cap;
        }

        applied.Add($"maxConcurrency {wanted}");
        return wanted;
    }

    /// <summary>
    /// Model commands are the phase-26 channel, reused rather than rebuilt: a profile that names
    /// models on a backend that cannot manage them is refused here with the reason that endpoint
    /// already gives, instead of failing later inside the executor.
    /// </summary>
    private static (IReadOnlyList<string> Ensure, IReadOnlyList<string> Remove) ClampModels(
        LocalCeiling local,
        NodeProfile desired,
        List<string> applied,
        List<NodeProfileRefusal> refusals)
    {
        var ensure = Clean(desired.Models?.Ensure);
        var remove = Clean(desired.Models?.Remove);

        if (ensure.Count == 0 && remove.Count == 0)
        {
            return (ensure, remove);
        }

        if (!local.SupportsModelManagement)
        {
            foreach (var model in ensure.Concat(remove))
            {
                refusals.Add(new NodeProfileRefusal(
                    $"model:{model}",
                    "this node runs a backend that cannot manage models"));
            }

            return (Array.Empty<string>(), Array.Empty<string>());
        }

        // A model in both lists is a profile that cannot be satisfied, and guessing which half the
        // author meant is how a node ends up pulling and deleting the same weights in a loop.
        var contradictory = ensure
            .Where(model => remove.Any(other => string.Equals(other, model, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (contradictory.Length > 0)
        {
            foreach (var model in contradictory)
            {
                refusals.Add(new NodeProfileRefusal(
                    $"model:{model}",
                    "the profile names this model in both 'ensure' and 'remove'"));
            }

            ensure = ensure.Where(m => !contradictory.Contains(m, StringComparer.OrdinalIgnoreCase)).ToArray();
            remove = remove.Where(m => !contradictory.Contains(m, StringComparer.OrdinalIgnoreCase)).ToArray();
        }

        return (ensure, remove);
    }

    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? models) =>
        models is null
            ? Array.Empty<string>()
            : models
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
}

/// <summary>
/// Everything about this box that a profile is not allowed to exceed. Read once from the node's own
/// options and its backend; nothing here comes off the wire.
/// </summary>
public sealed record LocalCeiling(
    IReadOnlyList<string> DisabledCapabilities,
    bool ToolsEnabled,
    IReadOnlyList<string> AllowedTools,
    int? MaxConcurrency,
    bool SupportsModelManagement);

/// <summary>What the node will run after clamping.</summary>
public sealed record EffectiveProfile(
    IReadOnlyList<string> DisabledCapabilities,
    IReadOnlyList<string> DisabledTools,
    int? MaxConcurrency);

public sealed record ClampResult(
    EffectiveProfile Effective,
    IReadOnlyList<string> Applied,
    IReadOnlyList<NodeProfileRefusal> Refusals,
    IReadOnlyList<string> EnsureModels,
    IReadOnlyList<string> RemoveModels);
