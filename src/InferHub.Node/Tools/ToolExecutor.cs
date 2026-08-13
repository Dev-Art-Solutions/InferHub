using System.Runtime.CompilerServices;
using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Tools;

/// <summary>
/// One step of a tool-model command: what the worker says it is doing, and whether that was the
/// last word (phase 48). Deliberately not a <c>ModelCommandProgress</c> — that record carries a
/// command id and a node id this class has no business knowing.
/// </summary>
public sealed record ToolModelProgress(string Model, string Status, string? Error, bool Done);

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

    /// <param name="progress">
    /// Where per-step <c>progress</c> frames go (phase 47, D2). Null is the pre-3.15 behaviour and
    /// is what every caller that does not watch a job passes — progress costs a callback and
    /// nothing else, but a caller that discards it should not pay for the allocation either.
    /// </param>
    public Task<ToolResult> RunAsync(
        ToolJob job,
        IProgress<ToolChunk>? progress,
        CancellationToken cancellationToken)
        => RunAsync(job, progress, upload: null, cancellationToken);

    /// <param name="upload">
    /// Where a streamed attachment's bytes come from (phase 53). Null is every job that carries its
    /// bytes on itself, which is every job before v3.21 and every one at or under
    /// <c>Tools:MaxAttachmentBytes</c> since.
    /// </param>
    public async Task<ToolResult> RunAsync(
        ToolJob job,
        IProgress<ToolChunk>? progress,
        IStreamedAttachmentSource? upload,
        CancellationToken cancellationToken)
    {
        var scratch = CreateScratch(job.JobId);

        // The read loop is NOT driven by the caller's token. Cancellation is cooperative first
        // (D3): the caller's token sends a `cancel` frame and starts the grace clock, and only when
        // that clock runs out does this one fire and take the worker down with it.
        using var hard = new CancellationTokenSource();

        try
        {
            // The bytes land on disk before the worker is asked for, and before the pool's slot is
            // taken: an upload that is still arriving must not hold a GPU worker idle while it does
            // (phase-41 D4's slot is the scarcest thing on this box).
            var streamed = job.HasStreamedAttachments && upload is not null
                ? await WriteStreamedAsync(job, scratch, upload, cancellationToken)
                : Array.Empty<ToolFile>();

            var request = BuildRequest(job, scratch, streamed);
            await using var lease = await runtime.AcquireAsync(job.Capability, job.Model, cancellationToken);

            logger.LogInformation(
                "Running {Capability} tool job {JobId} on '{ToolId}' with model {Model}",
                job.Capability,
                job.JobId,
                lease.ToolId,
                job.Model);

            var requestId = job.JobId.ToString("N");
            var grace = lease.CancelGrace;
            var cancelAsked = 0;

            await using var registration = cancellationToken.Register(() =>
            {
                if (Interlocked.Exchange(ref cancelAsked, 1) == 1)
                {
                    return;
                }

                _ = lease.CancelAsync(requestId, CancellationToken.None);

                // The grace is armed whether or not the frame was written: a worker that cannot be
                // asked will not be answering either, and the alternative is a request that hangs
                // until its own deadline for a client who already walked away.
                hard.CancelAfter(grace);
            });

            try
            {
                await foreach (var frame in lease.ExecuteAsync(request, hard.Token))
                {
                    if (frame.Type is ToolFrameTypes.Progress)
                    {
                        progress?.Report(ProgressChunk(job.JobId, frame));
                        continue;
                    }

                    if (frame.Type is ToolFrameTypes.Chunk)
                    {
                        // A blocking caller asked for one answer. A partial answer is the worker
                        // being helpful; it is not the answer, and it is not an error either.
                        continue;
                    }

                    if (frame.Type is ToolFrameTypes.Error)
                    {
                        // A worker that stopped because it was told to is not a failure, and the
                        // worker that did it is still warm — which is the whole reason cancel is a
                        // frame rather than a kill. It is NOT marked unhealthy.
                        if (string.Equals(frame.Code, ToolErrorCodes.Cancelled, StringComparison.Ordinal))
                        {
                            logger.LogInformation(
                                "Tool job {JobId} on '{ToolId}' was cancelled; the worker stays warm.",
                                job.JobId,
                                lease.ToolId);

                            return ToolResult.Refused(
                                job.JobId,
                                frame.Message ?? "the job was cancelled",
                                ToolErrorCodes.Cancelled);
                        }

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

                    // A job cancelled at step 27 of 28 may still succeed, and that is legal rather
                    // than a race to paper over: discarding a finished image to honour a state name
                    // would be worse than telling the caller what actually happened.
                    return ToolResult.Succeeded(job.JobId, frame.PayloadJson(), attachments);
                }
            }
            catch (OperationCanceledException) when (hard.IsCancellationRequested)
            {
                // The grace ran out, or the request was cancelled before a cooperative worker
                // existed to ask. Either way this worker is not cooperating and is retired.
                lease.MarkUnhealthy();

                logger.LogWarning(
                    "Tool job {JobId} on '{ToolId}' did not stop within the {Grace} cancel grace; terminating the worker.",
                    job.JobId,
                    lease.ToolId,
                    grace);

                return ToolResult.Refused(
                    job.JobId,
                    $"the job was cancelled and tool '{lease.ToolId}' did not stop within {grace.TotalSeconds:F0}s; its worker was terminated",
                    ToolErrorCodes.Cancelled);
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
            // Cancelled before a worker was ever leased — nothing to ask, nothing spent.
            return ToolResult.Refused(job.JobId, "the job was cancelled", ToolErrorCodes.Cancelled);
        }
        catch (ToolBusyException ex)
        {
            return ToolResult.Retry(job.JobId, ex.Message, options.QueueMaxWaitSeconds);
        }
        catch (ToolVramExhaustedException ex)
        {
            // Phase 48. The same 503 + Retry-After a busy pool gets: from a client's side "no worker
            // free" and "no room on the card" are the same fact — come back shortly — and giving
            // them different statuses would make a retry loop behave differently for no reason it
            // could act on.
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
                // Streaming answers carry no attachment either way (phase-41's StreamAsync refuses
                // them), so a streamed *upload* never reaches this path — the edge dispatches it
                // blocking. Empty rather than a parameter, so nobody has to wonder.
                request = BuildRequest(job, scratch, Array.Empty<ToolFile>());
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

                        // Not a partial answer, so it carries its own shape rather than the
                        // worker's payload — and it must be named here, because the default branch
                        // below is the terminal one and a progress frame falling into it would end
                        // the stream at step one.
                        case ToolFrameTypes.Progress:
                            yield return ProgressChunk(job.JobId, frame);
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

    /// <summary>The 3.14 signature, kept so nothing that never watches a job had to change.</summary>
    public Task<ToolResult> RunAsync(ToolJob job, CancellationToken cancellationToken)
        => RunAsync(job, progress: null, cancellationToken);

    /// <summary>
    /// Runs a model-management op against a tool and reports what it says, frame by frame
    /// (phase 48, D4). <c>status</c> is the worker's own word for what it is doing;
    /// <c>error</c> is set on the terminal item iff it failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It reuses the <em>ordinary</em> request path — one worker, one slot, one deadline — rather
    /// than inventing a management channel. A pull therefore queues behind a generation and a
    /// generation queues behind a pull, which is the honest answer for a resource there is one of.
    /// </para>
    /// <para>
    /// <b>There is no scratch directory.</b> Nothing about a pull moves bytes through the node; the
    /// weights land in the worker's own cache, on the volume, where the next process finds them.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<ToolModelProgress> ManageModelAsync(
        string toolId,
        string op,
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ToolWorkerLease? lease = null;
        string? refusal = null;

        try
        {
            lease = await runtime.AcquireToolAsync(toolId, cancellationToken);
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
            yield return new ToolModelProgress(model, "error", refusal, Done: true);
            yield break;
        }

        var request = new ToolFrame
        {
            Type = ToolFrameTypes.Request,
            Id = Guid.NewGuid().ToString("N"),
            Capability = lease!.Manifest.Capabilities.FirstOrDefault()?.Kind ?? string.Empty,
            Model = model,
            Payload = JsonSerializer.SerializeToElement(new { op }, ToolProtocol.Json)
        };

        logger.LogInformation("Running a '{Op}' model command for '{Model}' on tool '{ToolId}'", op, model, toolId);

        var frames = lease.ExecuteAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);

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
                catch (Exception ex)
                {
                    lease.MarkUnhealthy();
                    failure = ex.Message;
                }

                if (failure is not null)
                {
                    yield return new ToolModelProgress(model, "error", failure, Done: true);
                    yield break;
                }

                if (!hasNext)
                {
                    yield return new ToolModelProgress(model, "error", "the tool ended without answering", Done: true);
                    yield break;
                }

                switch (frame!.Type)
                {
                    case ToolFrameTypes.Chunk:
                        yield return new ToolModelProgress(model, StatusOf(frame) ?? op, null, Done: false);
                        continue;

                    case ToolFrameTypes.Progress:
                        continue;

                    case ToolFrameTypes.Error:
                        yield return new ToolModelProgress(
                            model,
                            "error",
                            frame.Message ?? "the tool reported an error",
                            Done: true);

                        yield break;

                    default:
                        yield return new ToolModelProgress(model, StatusOf(frame) ?? "success", null, Done: true);
                        yield break;
                }
            }
        }
        finally
        {
            await frames.DisposeAsync();
            await lease.DisposeAsync();
        }
    }

    /// <summary>
    /// The worker's own <c>status</c> word, if it sent one. Read out of the payload rather than
    /// invented here: "downloading (2140 MiB)" is a sentence only the process doing it can write,
    /// and the alternative is a progress bar that says the same thing for four minutes.
    /// </summary>
    private static string? StatusOf(ToolFrame frame)
    {
        if (frame.Payload is not { } payload || payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return payload.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
            ? status.GetString()
            : null;
    }

    /// <summary>Matches the hub's capability refusal (phase-40 D5), so backoff is the same everywhere.</summary>
    internal const int CapabilityRetryAfterSeconds = 30;

    /// <summary>
    /// A <c>progress</c> frame as it travels to the hub: a <see cref="ToolChunk"/> on the transport
    /// phase 41 already built, with the step in its payload. There is no new wire type, which is
    /// D2's "there is no new transport" made concrete.
    /// </summary>
    internal static ToolChunk ProgressChunk(Guid jobId, ToolFrame frame) => new(
        jobId,
        JsonSerializer.Serialize(
            new { type = ToolFrameTypes.Progress, step = frame.Step, totalSteps = frame.TotalSteps },
            ToolProtocol.Json),
        Done: false);

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

    /// <summary>
    /// Pulls a streamed attachment onto disk, one frame at a time (phase 53, D1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the payoff, and it is why the node side of the phase is small: phase-41 D5 already
    /// hands the worker a <em>path</em>, so writing that file from a socket instead of from a
    /// <c>byte[]</c> the node was handed changes nothing above it. The node's memory no longer
    /// grows with the upload at all — the frames are 64 KB and the file is appended to.
    /// </para>
    /// <para>
    /// The file is named from the part name and the index, never from anything the caller chose,
    /// and it goes into the same scratch directory the <c>finally</c> deletes — including when the
    /// upload dies half-written, which is D8.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ToolFile>> WriteStreamedAsync(
        ToolJob job,
        string scratch,
        IStreamedAttachmentSource source,
        CancellationToken cancellationToken)
    {
        var files = new List<ToolFile>();
        var ceiling = options.MaxStreamedBytes > 0 ? options.MaxStreamedBytes : options.MaxAttachmentBytes;

        FileStream? current = null;
        string? currentName = null;
        string? currentMedia = null;
        string? currentPath = null;
        long total = 0;

        try
        {
            await foreach (var chunk in source.ReadAsync(job.JobId, cancellationToken))
            {
                switch (chunk.Kind)
                {
                    case AttachmentChunkKinds.Start:
                        currentName = chunk.Name ?? $"file{chunk.Index}";
                        currentMedia = chunk.MediaType ?? "application/octet-stream";
                        currentPath = Path.Combine(scratch, SafeFileName(currentName, chunk.Index));
                        current = new FileStream(
                            currentPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: ToolAttachmentLimits.DefaultStreamChunkBytes,
                            useAsync: true);
                        break;

                    case AttachmentChunkKinds.Data when current is not null && chunk.Bytes is { } bytes:
                        total += bytes.LongLength;

                        // The node's own ceiling, not the hub's (phase-41 D2): the box that accepts
                        // an upload is not the box that has to write it down.
                        if (total > ceiling)
                        {
                            throw new InvalidOperationException(ToolAttachmentLimits.TooLarge(
                                currentName ?? "file",
                                total,
                                ceiling,
                                $"{ToolOptions.SectionName}:{nameof(ToolOptions.MaxStreamedBytes)}"));
                        }

                        await current.WriteAsync(bytes, cancellationToken);
                        break;

                    case AttachmentChunkKinds.End when current is not null:
                        await current.FlushAsync(cancellationToken);
                        await current.DisposeAsync();
                        current = null;
                        files.Add(new ToolFile(currentName!, currentMedia!, currentPath!));
                        break;
                }
            }
        }
        finally
        {
            if (current is not null)
            {
                await current.DisposeAsync();
            }
        }

        if (files.Count == 0)
        {
            // The enumeration ended without a complete attachment: the client went away, the hub
            // forgot the job, or the upload was refused mid-flight. Whichever it was, running the
            // tool on a file that is not there would produce a worker error nobody can read.
            throw new InvalidOperationException(
                "the streamed upload for this job ended before any attachment was complete");
        }

        logger.LogInformation(
            "Streamed {Count} attachment(s), {Bytes} bytes, into the scratch directory for job {JobId}",
            files.Count,
            total,
            job.JobId);

        return files;
    }

    private ToolFrame BuildRequest(ToolJob job, string scratch, IReadOnlyList<ToolFile> streamed)
    {
        var files = new List<ToolFile>(streamed);

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
