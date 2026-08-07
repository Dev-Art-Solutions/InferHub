using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using InferHub.Node.Retrieval;
using InferHub.Node.Tools;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Profiles;

/// <summary>
/// Applies a clamped profile to the running node (phase 43): capability narrowing folded into the
/// declaration, tool pools started and stopped, the concurrency cap the node registers at, and model
/// commands handed back to the caller to run down the existing phase-26 channel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here decides what is allowed</b> — <see cref="NodeProfileClamp"/> already did, purely,
/// and this only carries out what survived it. Keeping the two apart is what lets the adversarial
/// suite be a table of inputs instead of a host with a fake coordinator attached.
/// </para>
/// <para>
/// <b>A profile is never a startup dependency and never restarts the node</b> (D6). A node that
/// reboots on a hub instruction is a node an operator cannot keep up, and in-flight jobs would die
/// for a config change. Everything here is a live adjustment to a process that keeps serving.
/// </para>
/// </remarks>
public sealed class NodeProfileApplier(
    IOptions<NodeOptions> nodeOptions,
    IOptions<ToolOptions> toolOptions,
    IInferenceBackend backend,
    IToolRuntime toolRuntime,
    RetrievalHost retrieval,
    ILogger<NodeProfileApplier> logger)
{
    private readonly NodeOptions node = nodeOptions.Value;
    private readonly ToolOptions tools = toolOptions.Value;
    private readonly SemaphoreSlim gate = new(1, 1);

    private EffectiveProfile effective = new(
        nodeOptions.Value.Capabilities.Disabled
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Select(kind => kind.Trim())
            .ToArray(),
        Array.Empty<string>(),
        nodeOptions.Value.MaxConcurrency,
        Array.Empty<string>());

    /// <summary>What the node is currently running. Read by the capability declaration and by registration.</summary>
    public EffectiveProfile Effective => Volatile.Read(ref effective);

    /// <summary>The last state reported to the hub, or null if no profile has ever been applied.</summary>
    public NodeProfileState? Current { get; private set; }

    /// <summary>
    /// Clamp, apply, and report. Returns the state to send back plus the model commands the caller
    /// should run — <b>not</b> executed here, because the node already has one path that runs a
    /// <see cref="ModelCommand"/> and streams its progress to the hub, and a second one would drift.
    /// </summary>
    public async Task<ProfileApplication> ApplyAsync(
        string nodeId,
        NodeProfile? profile,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            // Idempotent by revision, which is also what makes the reconnect path safe to run
            // unconditionally: a node that comes back from a reboot re-asks, gets the same number,
            // and converges to a no-op rather than re-pulling forty gigabytes of weights (D2).
            if (profile is not null
                && Current is { } current
                && string.Equals(current.ProfileName, profile.Name, StringComparison.OrdinalIgnoreCase)
                && current.Revision == profile.Revision)
            {
                logger.LogDebug(
                    "Profile '{Profile}' revision {Revision} is already applied; nothing to do",
                    profile.Name,
                    profile.Revision);

                return new ProfileApplication(current, Array.Empty<ModelCommand>(), Changed: false);
            }

            var ceiling = Ceiling();
            var result = NodeProfileClamp.Apply(ceiling, profile);
            var previous = Volatile.Read(ref effective);

            await toolRuntime.SetDisabledToolsAsync(result.Effective.DisabledTools, cancellationToken);

            // Phase 48. A recipe is narrowed rather than stopped: the pool keeps running and simply
            // stops declaring that model, so a profile that switches `sdxl-turbo` off on one box
            // does not take its `sdxl` down with it. There is nothing to restart and nothing to
            // reload — the same in-place shape phase-43 D6 requires of a suspended tool.
            toolRuntime.SetDisabledModels(result.Effective.DisabledImageRecipes);

            Volatile.Write(ref effective, result.Effective);

            // Phase 44. The corpus is the one part of a profile whose application can fail against the
            // world rather than against the clamp — an engine that is not there, a credential this box
            // does not have — so its refusal joins the others rather than throwing. Refusals are per
            // item (phase-43 D6): a profile that also switched two tools off still switched them off.
            var refusals = result.Refusals;

            if (await ApplyRetrievalAsync(result.Retrieval, cancellationToken) is { } refusal)
            {
                refusals = [.. refusals, refusal];
            }

            var commands = result.EnsureModels
                .Select(model => new ModelCommand(Guid.NewGuid(), ModelCommand.KindPull, model))
                .Concat(result.RemoveModels
                    .Select(model => new ModelCommand(Guid.NewGuid(), ModelCommand.KindDelete, model)))
                .ToArray();

            var pending = result.EnsureModels
                .Select(model => $"pull '{model}'")
                .Concat(result.RemoveModels.Select(model => $"delete '{model}'"))
                .ToArray();

            var state = new NodeProfileState(
                nodeId,
                profile?.Name,
                profile?.Revision ?? 0,
                result.Applied,
                refusals,
                pending,
                result.Effective.MaxConcurrency,
                DateTimeOffset.UtcNow);

            Current = state;
            Log(profile, state);

            return new ProfileApplication(
                state,
                commands,
                Changed: !SameCapabilityNarrowing(previous, result.Effective) || commands.Length > 0);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Starts, stops or leaves the corpus alone, and turns a failed start into one refusal.
    /// </summary>
    /// <remarks>
    /// <b>It never restarts the node</b> (phase-43 D6). Switching retrieval on brings a corpus up
    /// under a process that keeps answering chat; switching it off drains the retrievals in flight
    /// and puts the routes back to 501. A start that fails leaves the node with no corpus at all
    /// rather than a half-started one that answers some queries (phase-44 D3).
    /// </remarks>
    private async Task<NodeProfileRefusal?> ApplyRetrievalAsync(
        RetrievalIntent? intent,
        CancellationToken cancellationToken)
    {
        if (intent is null)
        {
            return null;
        }

        if (!intent.Enabled)
        {
            await retrieval.StopCorpusAsync(cancellationToken);
            return null;
        }

        var result = await retrieval.StartCorpusAsync(
            new CorpusRequest(
                intent.Provider,
                intent.Url,
                intent.CredentialRef,
                intent.Collections,
                intent.EmbeddingModel,
                CorpusRequest.Profile),
            cancellationToken);

        return result.Started
            ? null
            : new NodeProfileRefusal("retrieval", result.Error ?? "the corpus could not be started");
    }

    /// <summary>
    /// The box's own configuration, which is the ceiling. Read fresh each time so an options reload
    /// is honoured, and read from <em>this</em> process rather than from anything the hub sent.
    /// </summary>
    private LocalCeiling Ceiling() => new(
        node.Capabilities.Disabled
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Select(kind => kind.Trim())
            .ToArray(),
        tools.Enabled,
        tools.Allowed
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToArray(),
        node.MaxConcurrency,
        backend.SupportsModelManagement,
        toolRuntime.ImageRecipes,
        tools.Image.AcceptedLicenses
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToArray(),
        node.Vram.BudgetMiB,
        node.Vram.ReserveMiB);

    private void Log(NodeProfile? profile, NodeProfileState state)
    {
        if (profile is null)
        {
            logger.LogInformation(
                "No coordinator profile applies to this node; it is running its own configuration.");

            return;
        }

        logger.LogInformation(
            "Applied profile '{Profile}' revision {Revision}: {Applied}{Pending}",
            profile.Name,
            profile.Revision,
            state.Applied.Count == 0 ? "nothing to change" : string.Join(", ", state.Applied),
            state.Pending.Count == 0 ? string.Empty : $"; started {string.Join(", ", state.Pending)}");

        // Every refusal at Warning and by name. A narrowing that silently did not happen is the one
        // failure mode where an operator believes the fleet is in a state it is not.
        foreach (var refusal in state.Refusals)
        {
            logger.LogWarning(
                "Profile '{Profile}' asked for {Item} and this node refused: {Reason}",
                profile.Name,
                refusal.Item,
                refusal.Reason);
        }
    }

    private static bool SameCapabilityNarrowing(EffectiveProfile a, EffectiveProfile b) =>
        a.MaxConcurrency == b.MaxConcurrency
        && a.DisabledCapabilities.SequenceEqual(b.DisabledCapabilities, StringComparer.OrdinalIgnoreCase)
        && a.DisabledTools.SequenceEqual(b.DisabledTools, StringComparer.OrdinalIgnoreCase)
        && a.DisabledImageRecipes.SequenceEqual(b.DisabledImageRecipes, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The outcome of applying a profile: what to report, what to run, and whether anything the
/// coordinator can see actually changed.
/// </summary>
public sealed record ProfileApplication(
    NodeProfileState State,
    IReadOnlyList<ModelCommand> Commands,
    bool Changed);
