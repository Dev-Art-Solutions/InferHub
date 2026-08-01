using System.Runtime.CompilerServices;
using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Tools;

/// <summary>
/// <see cref="InferenceExecutor"/>'s sibling: a <see cref="ToolJob"/> in, a
/// <see cref="ToolResult"/> or a stream of <see cref="ToolChunk"/> out (phase 41).
/// </summary>
/// <remarks>
/// <para>
/// It is driven by <c>CoordinatorConnection</c> in a mesh and by <c>LocalApi/</c> in solo mode, and
/// neither knows about the other — phase-37 D2's framing a third time (<b>D8</b>). A solo bundled
/// node that transcribes with one <c>docker run</c> is where this track is heading, and splitting
/// the local path across releases would mean building it twice.
/// </para>
/// <para>
/// <b>Everything about a request lives in a scratch directory that is deleted in a
/// <c>finally</c>, always</b> — after success and after every failure. Audio is content in the most
/// literal sense design rule 7 has met so far: a transcription request is a recording of somebody's
/// voice. Nothing here retains a byte of it past the request.
/// </para>
/// </remarks>
public sealed class ToolExecutor(
    IToolRuntime runtime,
    IOptions<ToolOptions> toolOptions,
    ILogger<ToolExecutor> logger)
{
    private readonly ToolOptions options = toolOptions.Value;

    /// <summary>Whether this node can serve the pair at all — the question the edge asks first.</summary>
    public bool Provides(string capability, string model) =>
        runtime.Capabilities.Any(c =>
            string.Equals(c.Kind, capability, StringComparison.OrdinalIgnoreCase)
            && c.Models.Any(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase)));

    public async Task<ToolResult> RunAsync(ToolJob job, CancellationToken cancellationToken)
    {
        var scratch = CreateScratch(job.JobId);

        try
        {
            var request = BuildRequest(job, scratch);
            await using var lease = await runtime.AcquireAsync(job.Capability, job.Model, cancellationToken);

            logger.LogInformation(
                "Running {Capability} tool job {JobId} on '{ToolId}' with model {Model}",
                job.Capability,
                job.JobId,
                lease.ToolId,
                job.Model);

            try
            {
                await foreach (var frame in lease.ExecuteAsync(request, cancellationToken))
                {
                    if (frame.Type is ToolFrameTypes.Chunk)
                    {
                        // A blocking caller asked for one answer. Progress frames are the worker
                        // being helpful; they are not the answer, and they are not an error either.
                        continue;
                    }

                    if (frame.Type is ToolFrameTypes.Error)
                    {
                        logger.LogWarning(
                            "Tool job {JobId} on '{ToolId}' failed: {Error}",
                            job.JobId,
                            lease.ToolId,
                            frame.Message);

                        // The worker may say which *kind* of failure it was; the edge renders a
                        // status from that field and never from the message (phase-29 D6).
                        return ToolResult.Refused(
                            job.JobId,
                            frame.Message ?? "the tool reported an error",
                            frame.Code);
                    }

                    var attachments = ReadBack(frame, scratch, lease.ToolId);

                    logger.LogInformation(
                        "Completed {Capability} tool job {JobId} on '{ToolId}'",
                        job.Capability,
                        job.JobId,
                        lease.ToolId);

                    return ToolResult.Succeeded(job.JobId, frame.PayloadJson(), attachments);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lease.MarkUnhealthy();
                throw;
            }
            catch (Exception ex)
            {
                // The worker overran its deadline or died. It is retired rather than pooled — see
                // ToolWorkerPool.ReleaseLease — and this is a failed *job*: the node keeps serving
                // inference, which the acceptance suite asserts after every one of these.
                lease.MarkUnhealthy();
                logger.LogWarning(ex, "Tool job {JobId} on '{ToolId}' failed", job.JobId, lease.ToolId);
                return ToolResult.Failed(job.JobId, ex.Message);
            }

            return ToolResult.Failed(job.JobId, $"tool '{lease.ToolId}' ended without answering");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ToolResult.Failed(job.JobId, "tool job was canceled");
        }
        catch (ToolBusyException ex)
        {
            return ToolResult.Retry(job.JobId, ex.Message, options.QueueMaxWaitSeconds);
        }
        catch (ToolUnavailableException ex)
        {
            return ToolResult.Retry(job.JobId, ex.Message, CapabilityRetryAfterSeconds);
        }
        catch (ToolNotProvidedException ex)
        {
            return ToolResult.Retry(job.JobId, ex.Message, CapabilityRetryAfterSeconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tool job {JobId} failed", job.JobId);
            return ToolResult.Failed(job.JobId, ex.Message);
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    /// <summary>
    /// The streaming shape. The terminal chunk carries the result payload, exactly as an inference
    /// stream's terminal chunk carries <c>done: true</c>.
    /// </summary>
    /// <remarks>
    /// <b>A streaming tool response carries no attachments</b>, and a worker that produces files for
    /// one gets a failed job saying so rather than a silently dropped output. Chunked binary needs a
    /// concatenable format and a contract on the client side; that is a phase, not a footnote.
    /// </remarks>
    public async IAsyncEnumerable<ToolChunk> StreamAsync(
        ToolJob job,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var scratch = CreateScratch(job.JobId);
        ToolWorkerLease? lease = null;

        try
        {
            ToolFrame? request = null;
            string? refusal = null;

            // Assembling the request and taking a worker can both fail, and neither may throw past
            // an iterator that has already handed the caller a 200 — so the failure becomes the
            // stream's terminal frame, which is what a client can actually act on.
            try
            {
                request = BuildRequest(job, scratch);
                lease = await runtime.AcquireAsync(job.Capability, job.Model, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception ex)
            {
                refusal = ex.Message;
            }

            if (refusal is not null)
            {
                logger.LogWarning("Streaming tool job {JobId} was refused: {Error}", job.JobId, refusal);
                yield return Terminal(job.JobId, refusal);
                yield break;
            }

            var frames = lease!.ExecuteAsync(request!, cancellationToken).GetAsyncEnumerator(cancellationToken);

            try
            {
                while (true)
                {
                    ToolFrame? frame = null;
                    string? failure = null;
                    var hasNext = false;

                    try
                    {
                        hasNext = await frames.MoveNextAsync();

                        if (hasNext)
                        {
                            frame = frames.Current;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        lease.MarkUnhealthy();
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lease.MarkUnhealthy();
                        failure = ex.Message;
                    }

                    if (failure is not null)
                    {
                        logger.LogWarning("Streaming tool job {JobId} failed: {Error}", job.JobId, failure);
                        yield return Terminal(job.JobId, failure);
                        yield break;
                    }

                    if (!hasNext)
                    {
                        yield return Terminal(job.JobId, "the tool ended without answering");
                        yield break;
                    }

                    switch (frame!.Type)
                    {
                        case ToolFrameTypes.Chunk:
                            yield return new ToolChunk(job.JobId, frame.PayloadJson() ?? "{}", false);
                            continue;

                        case ToolFrameTypes.Error:
                            yield return Terminal(job.JobId, frame.Message ?? "the tool reported an error");
                            yield break;

                        default:
                            if (frame.Files is { Count: > 0 })
                            {
                                yield return Terminal(
                                    job.JobId,
                                    $"tool '{lease!.ToolId}' returned {frame.Files.Count} file(s) for a streaming request, which this protocol cannot carry. Call it without stream=true.");

                                yield break;
                            }

                            yield return new ToolChunk(job.JobId, frame.PayloadJson() ?? "{}", true);
                            yield break;
                    }
                }
            }
            finally
            {
                await frames.DisposeAsync();
            }
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            DeleteScratch(scratch);
        }
    }

    /// <summary>Matches the hub's capability refusal (phase-40 D5), so backoff is the same everywhere.</summary>
    internal const int CapabilityRetryAfterSeconds = 30;

    private static ToolChunk Terminal(Guid jobId, string error) =>
        new(jobId, JsonSerializer.Serialize(new { error, done = true }, ToolProtocol.Json), true);

    private string CreateScratch(Guid jobId)
    {
        var path = Path.Combine(options.ResolvedScratchDirectory(), jobId.ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private void DeleteScratch(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // Worth a line: a scratch directory that survives holds request bytes, and on a busy
            // node it also fills a volume.
            logger.LogWarning(ex, "Could not delete the scratch directory {Path}", path);
        }
    }

    private ToolFrame BuildRequest(ToolJob job, string scratch)
    {
        var files = new List<ToolFile>();

        var incoming = job.Attachments ?? (IReadOnlyList<ToolAttachment>)Array.Empty<ToolAttachment>();

        foreach (var (attachment, index) in incoming.Select((a, i) => (a, i)))
        {
            if (attachment.Bytes.LongLength > options.MaxAttachmentBytes)
            {
                throw new InvalidOperationException(
                    ToolAttachmentLimits.TooLarge(attachment.Name, attachment.Bytes.LongLength, options.MaxAttachmentBytes));
            }

            // The name comes from a client, so it names nothing but itself: the index is what makes
            // the path unique and Path.GetFileName is what keeps "../../etc/authorized_keys" inside
            // the scratch directory.
            var safe = SafeFileName(attachment.Name, index);
            var path = Path.Combine(scratch, safe);
            File.WriteAllBytes(path, attachment.Bytes);
            files.Add(new ToolFile(attachment.Name, attachment.MediaType, path));
        }

        return new ToolFrame
        {
            Type = ToolFrameTypes.Request,
            Id = job.JobId.ToString("N"),
            Capability = job.Capability,
            Model = job.Model,
            Payload = ParsePayload(job.Payload),
            Files = files.Count == 0 ? null : files,
            Scratch = scratch
        };
    }

    private static JsonElement? ParsePayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(payload).RootElement.Clone();
        }
        catch (JsonException)
        {
            // The payload is the client's dialect and the node does not interpret it — but it does
            // have to put it in a JSON frame, so a body that is not JSON cannot travel.
            throw new InvalidOperationException("the tool payload is not valid JSON");
        }
    }

    private static string SafeFileName(string name, int index)
    {
        var trimmed = Path.GetFileName(name ?? string.Empty);

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(trimmed) ? $"input-{index}" : $"{index}-{trimmed}";
    }

    private IReadOnlyList<ToolAttachment>? ReadBack(ToolFrame frame, string scratch, string toolId)
    {
        if (frame.Files is not { Count: > 0 })
        {
            return null;
        }

        var root = Path.GetFullPath(scratch) + Path.DirectorySeparatorChar;
        var attachments = new List<ToolAttachment>();

        foreach (var file in frame.Files)
        {
            var full = Path.GetFullPath(file.Path);

            // A worker naming a path outside its own scratch directory is either confused or
            // hostile, and the difference does not matter: reading it would turn "a tool ran" into
            // "a tool exfiltrated a file through the client-facing API".
            if (!full.StartsWith(root, StringComparison.Ordinal))
            {
                logger.LogError(
                    "Tool '{ToolId}' returned a file outside its scratch directory ({Path}); refusing to read it.",
                    toolId,
                    file.Path);

                throw new InvalidOperationException(
                    $"tool '{toolId}' returned a file outside its scratch directory");
            }

            var info = new FileInfo(full);

            if (!info.Exists)
            {
                throw new InvalidOperationException($"tool '{toolId}' named an output file that does not exist");
            }

            if (info.Length > options.MaxAttachmentBytes)
            {
                throw new InvalidOperationException(
                    ToolAttachmentLimits.TooLarge(file.Name, info.Length, options.MaxAttachmentBytes));
            }

            attachments.Add(new ToolAttachment(file.Name, file.MediaType, File.ReadAllBytes(full)));
        }

        return attachments;
    }
}
