using System.Runtime.CompilerServices;
using InferHub.Node.Backends;
using InferHub.Node.Tools;
using InferHub.Shared.Contracts;

namespace InferHub.Node;

/// <summary>
/// Runs a hub-issued <see cref="ModelCommand"/> against the node's backend and turns it into a
/// stream of <see cref="ModelCommandProgress"/> frames. A pull streams progress as bytes arrive;
/// delete and warm emit a start frame and a terminal one. Every path ends with exactly one frame
/// whose <see cref="ModelCommandProgress.Done"/> is set — with <see cref="ModelCommandProgress.Error"/>
/// populated iff it failed — so the coordinator always learns the outcome.
/// </summary>
public sealed class ModelCommandExecutor(
    IInferenceBackend backend,
    ILogger<ModelCommandExecutor> logger,
    ToolExecutor? tools = null,
    Backends.HuggingFace.HuggingFaceStore? huggingFace = null,
    Backends.Strata.StrataInstaller? strata = null)
{
    /// <summary>Phase 98: this node downloads from Hugging Face, so it manages models even when no engine does.</summary>
    public bool ManagesModels => backend.SupportsModelManagement || huggingFace is not null;

    public async IAsyncEnumerable<ModelCommandProgress> ExecuteAsync(
        ModelCommand command,
        string nodeId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Phase 48. A command that names a tool is about that tool's models, not the backend's —
        // and it deliberately reuses this whole path rather than growing a second one, so the
        // coalescing, the progress relay and the "no persistent state" property come with it.
        if (command.IsToolCommand)
        {
            await foreach (var frame in RunToolCommandAsync(command, nodeId, cancellationToken))
            {
                yield return frame;
            }

            yield break;
        }

        // Phase 98: the engine "huggingface" is this node's store, not one of its engines.
        if (string.Equals(command.Engine, ModelCommand.EngineHuggingFace, StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var frame in RunHuggingFaceAsync(command, nodeId, cancellationToken))
            {
                yield return frame;
            }

            yield break;
        }

        if (!backend.SupportsModelManagement)
        {
            yield return Terminal(command, nodeId, "unsupported",
                $"the {backend.Name} backend cannot manage models");
            yield break;
        }

        if (!ModelCommand.IsKnownKind(command.Kind))
        {
            yield return Terminal(command, nodeId, "unknown-kind",
                $"unknown model command kind '{command.Kind}'");
            yield break;
        }

        logger.LogInformation(
            "Running {Kind} model command {CommandId} for '{Model}'",
            command.Kind, command.CommandId, command.ModelName);

        // 96 D3: a command that names an engine is only for a node that runs several.
        if (!string.IsNullOrWhiteSpace(command.Engine) && backend is not MultiBackend)
        {
            yield return Terminal(command, nodeId, "unsupported",
                $"this node runs one backend ({backend.Name}), not engines; send the command without an engine");
            yield break;
        }

        if (command.Kind == ModelCommand.KindPull)
        {
            await foreach (var frame in RunPullAsync(command, nodeId, cancellationToken))
            {
                yield return frame;
            }

            yield break;
        }

        // delete / warm / unload: a start frame, the (quick) op, then a terminal frame.
        yield return Progress(command, nodeId, command.Kind switch
        {
            ModelCommand.KindDelete => "deleting",
            ModelCommand.KindUnload => "unloading",
            _ => "warming"
        }, null);

        string? error = null;
        try
        {
            var engines = backend as MultiBackend;

            switch (command.Kind)
            {
                case ModelCommand.KindDelete when engines is not null:
                    await engines.DeleteAsync(command.ModelName, command.Engine, cancellationToken);
                    break;
                case ModelCommand.KindDelete:
                    await backend.DeleteAsync(command.ModelName, cancellationToken);
                    break;
                case ModelCommand.KindUnload when engines is not null:
                    await engines.UnloadAsync(command.ModelName, command.Engine, cancellationToken);
                    break;
                case ModelCommand.KindUnload:
                    await backend.UnloadAsync(command.ModelName, cancellationToken);
                    break;
                case ModelCommand.KindWarm when engines is not null:
                    await engines.WarmAsync(command.ModelName, command.Engine, cancellationToken);
                    break;
                default:
                    await backend.WarmAsync(command.ModelName, cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Model command {CommandId} ({Kind}) failed", command.CommandId, command.Kind);
            error = ex.Message;
        }

        yield return Terminal(command, nodeId,
            error is null
                ? command.Kind switch
                {
                    ModelCommand.KindDelete => "deleted",
                    ModelCommand.KindUnload => "unloaded",
                    _ => "warmed"
                }
                : "error",
            error);
    }

    /// <summary>
    /// A pull (a Hugging Face link) or a delete (a model the store downloaded) — phase 98. Same
    /// frames as a backend pull, so the hub's progress relay and coalescing need nothing new.
    /// </summary>
    private async IAsyncEnumerable<ModelCommandProgress> RunHuggingFaceAsync(
        ModelCommand command,
        string nodeId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (huggingFace is null)
        {
            yield return Terminal(command, nodeId, "unsupported",
                "this node does not download from Hugging Face; set HuggingFace:Enabled=true on it");
            yield break;
        }

        // Phase 99 D4: a link to one of Strata's repos is an install by Strata's own setup, not a GGUF
        // for llama.cpp — Strata's quants need its pack step, and llama.cpp cannot serve its model.
        InferHub.Shared.HuggingFace.HfReference? reference = null;
        var strataLink = command.Kind == ModelCommand.KindPull
                         && InferHub.Shared.HuggingFace.HfReference.TryParse(command.ModelName, null, out reference, out _)
                         && InferHub.Shared.Strata.StrataModels.IsStrataRepo(reference!);

        if (strataLink)
        {
            if (strata is null)
            {
                yield return Terminal(command, nodeId, "error",
                    $"'{command.ModelName}' is a Strata model, and this node serves no Strata install; set Strata:Root and a strata backend or engine to install it here");
                yield break;
            }

            logger.LogInformation("Installing '{Model}' with Strata's setup (command {CommandId})", command.ModelName, command.CommandId);

            await foreach (var frame in RelayAsync(command, nodeId, InstallStrataAsync(strata, reference!, command.Vision, cancellationToken), cancellationToken))
            {
                yield return frame;
            }

            yield break;
        }

        if (command.Kind == ModelCommand.KindDelete && strata is not null && strata.Has(command.ModelName))
        {
            yield return Terminal(command, nodeId, "error",
                $"'{command.ModelName}' is a Strata install; Strata's sizes share files and only its setup knows which, so it is removed on the box, not from the hub");
            yield break;
        }

        if (command.Kind == ModelCommand.KindDelete)
        {
            yield return Progress(command, nodeId, "deleting", null);
            string? failure = null;

            try
            {
                await huggingFace.DeleteAsync(command.ModelName, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = ex.Message;
            }

            yield return Terminal(command, nodeId, failure is null ? "deleted" : "error", failure);
            yield break;
        }

        if (command.Kind != ModelCommand.KindPull)
        {
            yield return Terminal(command, nodeId, "unsupported",
                $"'{command.Kind}' is not something a Hugging Face download does; pull and delete are — warm and unload go to the engine that serves the model");
            yield break;
        }

        logger.LogInformation("Pulling '{Model}' from Hugging Face (command {CommandId})", command.ModelName, command.CommandId);

        await foreach (var frame in RelayAsync(command, nodeId, huggingFace.PullAsync(command.ModelName, cancellationToken), cancellationToken))
        {
            yield return frame;
        }
    }

    /// <summary>A refusal of the link (a size Strata does not have) is a terminal frame, as a GGUF refusal is.</summary>
    private static async IAsyncEnumerable<ModelPullProgress> InstallStrataAsync(
        Backends.Strata.StrataInstaller strata,
        InferHub.Shared.HuggingFace.HfReference reference,
        string? vision,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in strata.InstallAsync(reference, vision, cancellationToken))
        {
            yield return frame;
        }
    }

    /// <summary>
    /// A pull or a delete against a tool's model catalogue (phase 48, D4).
    /// </summary>
    /// <remarks>
    /// Every failure is a terminal frame with <see cref="ModelCommandProgress.Error"/> set — a tool
    /// this node does not have, a licence nobody accepted, a download that died — because the
    /// coordinator's only contract is that exactly one frame arrives with <c>Done</c> on it. A
    /// throw here would leave an operator watching a progress bar that simply stops.
    /// </remarks>
    private async IAsyncEnumerable<ModelCommandProgress> RunToolCommandAsync(
        ModelCommand command,
        string nodeId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (tools is null)
        {
            yield return Terminal(command, nodeId, "unsupported", "this node has no tool runtime");
            yield break;
        }

        if (!ModelCommand.IsKnownToolKind(command.Kind))
        {
            yield return Terminal(command, nodeId, "unsupported",
                $"'{command.Kind}' is not something a tool's models can do; pull and delete are");

            yield break;
        }

        logger.LogInformation(
            "Running {Kind} model command {CommandId} for '{Model}' on tool '{Tool}'",
            command.Kind, command.CommandId, command.ModelName, command.Tool);

        yield return Progress(command, nodeId, command.Kind == ModelCommand.KindPull ? "queued" : "deleting", null);

        await foreach (var step in tools.ManageModelAsync(
                           command.Tool!, command.Kind, command.ModelName, cancellationToken))
        {
            yield return step.Done
                ? Terminal(command, nodeId, step.Error is null ? step.Status : "error", step.Error)
                : Progress(command, nodeId, step.Status, null);
        }
    }

    private async IAsyncEnumerable<ModelCommandProgress> RunPullAsync(
        ModelCommand command,
        string nodeId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerator<ModelPullProgress> frames;
        string? refused = null;

        try
        {
            frames = (backend is MultiBackend engines
                    ? engines.PullAsync(command.ModelName, command.Engine, cancellationToken)
                    : backend.PullAsync(command.ModelName, cancellationToken))
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // Which engine (96 D3) is decided before the first frame, and a refusal is a terminal frame.
            frames = AsyncEnumerable.Empty<ModelPullProgress>().GetAsyncEnumerator(cancellationToken);
            refused = ex.Message;
        }

        if (refused is not null)
        {
            logger.LogWarning("Pull of '{Model}' refused: {Reason}", command.ModelName, refused);
            yield return Terminal(command, nodeId, "error", refused);
            yield break;
        }

        await foreach (var frame in RelayAsync(command, nodeId, frames, cancellationToken))
        {
            yield return frame;
        }
    }

    private IAsyncEnumerable<ModelCommandProgress> RelayAsync(
        ModelCommand command,
        string nodeId,
        IAsyncEnumerable<ModelPullProgress> source,
        CancellationToken cancellationToken)
        => RelayAsync(command, nodeId, source.GetAsyncEnumerator(cancellationToken), cancellationToken);

    /// <summary>Every frame relayed; any throw — before the first frame or after the last — a terminal error frame.</summary>
    private async IAsyncEnumerable<ModelCommandProgress> RelayAsync(
        ModelCommand command,
        string nodeId,
        IAsyncEnumerator<ModelPullProgress> frames,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                ModelPullProgress? current = null;
                Exception? error = null;
                var hasNext = false;

                try
                {
                    hasNext = await frames.MoveNextAsync();
                    if (hasNext)
                    {
                        current = frames.Current;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                if (error is not null)
                {
                    logger.LogWarning(error, "Pull of '{Model}' failed", command.ModelName);
                    yield return Terminal(command, nodeId, "error", error.Message);
                    yield break;
                }

                if (!hasNext)
                {
                    break;
                }

                yield return Progress(command, nodeId, current!.Status, Percent(current));
            }
        }
        finally
        {
            await frames.DisposeAsync();
        }

        yield return Terminal(command, nodeId, "success", null);
    }

    private static double? Percent(ModelPullProgress p) =>
        p is { Total: > 0, Completed: >= 0 } ? Math.Clamp(100.0 * p.Completed.Value / p.Total.Value, 0, 100) : null;

    private static ModelCommandProgress Progress(ModelCommand c, string nodeId, string status, double? percent) =>
        new(c.CommandId, nodeId, c.Kind, c.ModelName, status, percent, Done: false, Error: null, Tool: c.Tool);

    private static ModelCommandProgress Terminal(ModelCommand c, string nodeId, string status, string? error) =>
        new(c.CommandId, nodeId, c.Kind, c.ModelName, status, error is null ? 100 : null, Done: true, Error: error, Tool: c.Tool);
}
