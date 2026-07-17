using System.Runtime.CompilerServices;
using InferHub.Node.Backends;
using InferHub.Shared.Contracts;

namespace InferHub.Node;

/// <summary>
/// Runs a hub-issued <see cref="ModelCommand"/> against the node's backend and turns it into a
/// stream of <see cref="ModelCommandProgress"/> frames. A pull streams progress as bytes arrive;
/// delete and warm emit a start frame and a terminal one. Every path ends with exactly one frame
/// whose <see cref="ModelCommandProgress.Done"/> is set — with <see cref="ModelCommandProgress.Error"/>
/// populated iff it failed — so the coordinator always learns the outcome.
/// </summary>
public sealed class ModelCommandExecutor(IInferenceBackend backend, ILogger<ModelCommandExecutor> logger)
{
    public async IAsyncEnumerable<ModelCommandProgress> ExecuteAsync(
        ModelCommand command,
        string nodeId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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

        if (command.Kind == ModelCommand.KindPull)
        {
            await foreach (var frame in RunPullAsync(command, nodeId, cancellationToken))
            {
                yield return frame;
            }

            yield break;
        }

        // delete / warm: a start frame, the (quick) op, then a terminal frame.
        yield return Progress(command, nodeId, command.Kind == ModelCommand.KindDelete ? "deleting" : "warming", null);

        string? error = null;
        try
        {
            if (command.Kind == ModelCommand.KindDelete)
            {
                await backend.DeleteAsync(command.ModelName, cancellationToken);
            }
            else
            {
                await backend.WarmAsync(command.ModelName, cancellationToken);
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
            error is null ? (command.Kind == ModelCommand.KindDelete ? "deleted" : "warmed") : "error",
            error);
    }

    private async IAsyncEnumerable<ModelCommandProgress> RunPullAsync(
        ModelCommand command,
        string nodeId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var frames = backend.PullAsync(command.ModelName, cancellationToken).GetAsyncEnumerator(cancellationToken);

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
        new(c.CommandId, nodeId, c.Kind, c.ModelName, status, percent, Done: false, Error: null);

    private static ModelCommandProgress Terminal(ModelCommand c, string nodeId, string status, string? error) =>
        new(c.CommandId, nodeId, c.Kind, c.ModelName, status, error is null ? 100 : null, Done: true, Error: error);
}
