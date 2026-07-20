using System.Threading.Channels;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Endpoints;

/// <summary>
/// Admission control + routing + queueing + pre-stream failover + metering, shared by both
/// client-facing dialects. The outcome is deliberately format-neutral: <see cref="InferenceEndpoints"/>
/// renders it as Ollama NDJSON and <see cref="OpenAi.OpenAiEndpoints"/> renders it as OpenAI SSE.
///
/// Two copies of failover logic is how failover quietly rots — there is exactly one here. The
/// same goes for the phase-25 admission check: it runs here, once, because the decision needs
/// the model name, which middleware does not have.
/// </summary>
internal static class InferenceCore
{
    /// <summary>Values of the <c>X-InferHub-Served-By</c> response header.</summary>
    public const string ServedByNode = "node";

    public const string ServedByFallback = "fallback";

    public const string ServedByHeader = "X-InferHub-Served-By";

    internal readonly record struct DispatchOutcome(
        ChannelReader<InferenceChunk>? Stream,
        string? ResponseJson,
        int? ErrorStatus,
        string? ErrorMessage,
        string ServedBy = ServedByNode,
        int? RetryAfterSeconds = null)
    {
        public static DispatchOutcome Streaming(ChannelReader<InferenceChunk> stream)
            => new(stream, null, null, null);

        public static DispatchOutcome Blocking(string responseJson)
            => new(null, responseJson, null, null);

        public static DispatchOutcome Failure(int status, string message, int? retryAfterSeconds = null)
            => new(null, null, status, message, RetryAfterSeconds: retryAfterSeconds);

        public static DispatchOutcome Fallback(FallbackResult result)
            => new(result.Stream, result.ResponseJson, null, null, ServedByFallback);

        public bool IsError => ErrorStatus is not null;
    }

    /// <summary>
    /// The innermost human sentence in a node's error, for the client-facing envelope.
    /// </summary>
    /// <remarks>
    /// Ollama stuffs its own backend's JSON error into its <c>error</c> field as a *string*, so a
    /// llama.cpp refusal arrives already double-encoded and lands in our envelope triple-escaped:
    /// an SDK reads <c>error.message</c> and shows the user a wall of backslashes instead of the
    /// one sentence that says what to fix. Found live in phase 29, where "this model does not
    /// support multimodal requests" is the error a vision user hits first.
    ///
    /// This is **presentation only**: nothing is inferred from the text and the status code is
    /// untouched. Unwrapping is not the same as interpreting — do not grow this into a function
    /// that decides what an upstream error *means*.
    /// </remarks>
    internal static string ReadableNodeError(string? error)
    {
        const string fallback = "node failed to run inference";

        var message = error;

        // Bounded: Ollama + llama.cpp produce two levels, and an unbounded loop over
        // caller-influenced text is not something to leave lying around.
        for (var depth = 0; depth < 4; depth++)
        {
            if (message is null || message.TrimStart() is not ['{', ..] trimmed)
            {
                break;
            }

            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(trimmed);

                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("error", out var inner))
                {
                    break;
                }

                if (inner.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    message = inner.GetString();
                    continue;
                }

                if (inner.ValueKind == System.Text.Json.JsonValueKind.Object
                    && inner.TryGetProperty("message", out var sentence)
                    && sentence.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    message = sentence.GetString();
                    continue;
                }

                break;
            }
            catch (System.Text.Json.JsonException)
            {
                // Not JSON after all — what we already have is the best available text.
                break;
            }
        }

        return string.IsNullOrWhiteSpace(message) ? fallback : message;
    }

    /// <summary>The per-request services phase 25 added, bundled so the endpoint signatures stay sane.</summary>
    internal readonly record struct ClientContext(
        ResolvedClient Client,
        AdmissionControl Admission,
        UsageMeter Usage,
        IRequestQueue Queue)
    {
        public static ClientContext From(HttpContext httpContext)
        {
            var services = httpContext.RequestServices;
            return new ClientContext(
                BearerApiKeyMiddleware.ClientOf(httpContext),
                services.GetRequiredService<AdmissionControl>(),
                services.GetRequiredService<UsageMeter>(),
                services.GetRequiredService<IRequestQueue>());
        }
    }

    public static async Task<DispatchOutcome> DispatchAsync(
        string kind,
        string rawJson,
        string? model,
        bool? stream,
        string? conversationKey,
        ClientContext context,
        Services.IRouter router,
        IDispatcher dispatcher,
        IFallbackDispatcher fallback,
        Metrics metrics,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return DispatchOutcome.Failure(StatusCodes.Status400BadRequest, "model is required");
        }

        // Admission first: a client over its limits must not consume routing, queue capacity
        // or an upstream call. Everything after this point holds the client's concurrency
        // lease and must release it on every path out.
        var admission = context.Admission.TryAdmit(context.Client, model);

        if (!admission.Allowed)
        {
            logger.LogInformation(
                "Rejected {Kind} for client {ClientId}: {Status} {Message}",
                kind,
                context.Client.Id,
                admission.Status,
                admission.Message);
            return DispatchOutcome.Failure(admission.Status, admission.Message!, admission.RetryAfterSeconds);
        }

        var lease = admission.Lease;
        var leaseHandedOff = false;

        try
        {
            var node = router.Route(model, conversationKey);

            // Cloud burst. Off by default, and when off this is a single false — the 404 below is
            // byte-for-byte what every release since 1.0 has returned. With Trigger=no-node-or-saturated
            // a saturated fleet overflows to the upstream INSTEAD of queueing — the upstream answers
            // in seconds, the queue in tens of seconds, and a client who opted into burst asked for
            // the former (precedence recorded in CLAUDE.md, covered by QueueTests).
            if (fallback.ShouldServe(model, hasCapableNode: node is not null))
            {
                try
                {
                    var result = await fallback.DispatchAsync(
                        kind,
                        rawJson,
                        model,
                        stream is not false,
                        cancellationToken);

                    if (result.Stream is { } fallbackStream)
                    {
                        leaseHandedOff = true;
                        return DispatchOutcome.Fallback(result) with
                        {
                            Stream = context.Usage.WrapStream(fallbackStream, context.Client, kind, model, fallback: true, lease)
                        };
                    }

                    context.Usage.RecordResponse(context.Client, kind, model, result.ResponseJson ?? "{}", fallback: true);
                    return DispatchOutcome.Fallback(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Cloud burst failed for model {Model}", model);

                    if (node is null)
                    {
                        return DispatchOutcome.Failure(
                            StatusCodes.Status502BadGateway,
                            $"no node holds model '{model}' and the fallback upstream failed: {ex.Message}");
                    }

                    // Saturation burst is an optimisation, not a promise — fall through to the node.
                }
            }

            if (node is null)
            {
                return DispatchOutcome.Failure(StatusCodes.Status404NotFound, $"model '{model}' not found");
            }

            // Every capable node is at its declared cap: wait for a slot, briefly, then say so
            // (phase 25, D5). Nodes that declared no cap are never saturated and never queue.
            if (context.Queue.IsSaturated(model))
            {
                var queueOutcome = await context.Queue.WaitForCapacityAsync(model, cancellationToken);

                if (queueOutcome is not QueueOutcome.Admitted)
                {
                    var reason = queueOutcome == QueueOutcome.QueueFull
                        ? "the request queue is full"
                        : $"every node serving '{model}' stayed at capacity for {context.Queue.MaxWaitSeconds}s";
                    return DispatchOutcome.Failure(
                        StatusCodes.Status503ServiceUnavailable,
                        reason,
                        Math.Max(1, context.Queue.MaxWaitSeconds));
                }

                // A slot freed somewhere — route again so the request lands on the node that has it.
                node = router.Route(model, conversationKey) ?? node;
            }

            if (conversationKey is not null)
            {
                logger.LogInformation(
                    "Routing {Kind} for conversation {ConversationKey} to node {NodeId} ({NodeName})",
                    kind,
                    conversationKey,
                    node.NodeId,
                    node.Name);
            }

            var job = new InferenceJob(Guid.NewGuid(), kind, rawJson);

            try
            {
                var outcome = await DispatchWithFailoverAsync(
                    kind,
                    model,
                    stream,
                    conversationKey,
                    node,
                    job,
                    router,
                    dispatcher,
                    metrics,
                    logger,
                    cancellationToken);

                if (outcome.Stream is { } chunks)
                {
                    leaseHandedOff = true;
                    return outcome with
                    {
                        Stream = context.Usage.WrapStream(chunks, context.Client, kind, model, fallback: false, lease)
                    };
                }

                if (!outcome.IsError)
                {
                    context.Usage.RecordResponse(context.Client, kind, model, outcome.ResponseJson ?? "{}", fallback: false);
                }

                return outcome;
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Job {JobId} for model {Model} timed out", job.JobId, model);
                return DispatchOutcome.Failure(StatusCodes.Status504GatewayTimeout, "inference request timed out");
            }
            catch (NodeDisconnectedException ex)
            {
                // We're here only if failover also failed (or was impossible). Surface a clean
                // 502 — the caller hasn't received any content yet for either path because the
                // streaming dispatcher only returns its reader after the first chunk arrives.
                logger.LogWarning(ex, "Job {JobId} for model {Model} could not be dispatched", job.JobId, model);
                return DispatchOutcome.Failure(StatusCodes.Status502BadGateway, "no node was able to handle the request");
            }
        }
        finally
        {
            // Streams carry the lease with them; every other exit releases it here.
            if (!leaseHandedOff)
            {
                lease?.Dispose();
            }
        }
    }

    private static async Task<DispatchOutcome> DispatchWithFailoverAsync(
        string kind,
        string model,
        bool? stream,
        string? conversationKey,
        RoutableNode node,
        InferenceJob job,
        Services.IRouter router,
        IDispatcher dispatcher,
        Metrics metrics,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (stream is not false)
            {
                var chunks = await dispatcher.DispatchStreamAsync(node, job, cancellationToken);
                return DispatchOutcome.Streaming(chunks);
            }

            var result = await dispatcher.DispatchAsync(node, job, cancellationToken);

            if (!result.Success)
            {
                return DispatchOutcome.Failure(
                    StatusCodes.Status502BadGateway,
                    ReadableNodeError(result.Error));
            }

            return DispatchOutcome.Blocking(result.ResponseJson ?? "{}");
        }
        catch (NodeDisconnectedException ex)
        {
            metrics.RecordFailoverAttempted();
            logger.LogWarning(
                "Node {NodeId} dropped before the job started — attempting failover",
                node.NodeId);

            var retryNode = router.Route(model, conversationKey, excludeConnectionId: ex.ConnectionId);

            if (retryNode is null)
            {
                logger.LogWarning(
                    "No alternate node available for failover of job {JobId} (model {Model})",
                    job.JobId,
                    model);
                throw;
            }

            // Issue a fresh job id so the dispatcher's pending tables stay coherent.
            var retryJob = job with { JobId = Guid.NewGuid() };

            logger.LogInformation(
                "Failing over {Kind} job {JobId} -> {NewJobId} to node {NodeId} ({NodeName})",
                kind,
                job.JobId,
                retryJob.JobId,
                retryNode.NodeId,
                retryNode.Name);

            if (stream is not false)
            {
                var chunks = await dispatcher.DispatchStreamAsync(retryNode, retryJob, cancellationToken);
                metrics.RecordFailoverSucceeded();
                return DispatchOutcome.Streaming(chunks);
            }

            var result = await dispatcher.DispatchAsync(retryNode, retryJob, cancellationToken);

            if (!result.Success)
            {
                metrics.RecordFailoverSucceeded();
                return DispatchOutcome.Failure(
                    StatusCodes.Status502BadGateway,
                    ReadableNodeError(result.Error));
            }

            metrics.RecordFailoverSucceeded();
            return DispatchOutcome.Blocking(result.ResponseJson ?? "{}");
        }
    }
}
