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
    ToolExecutor? tools = null)
{
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
