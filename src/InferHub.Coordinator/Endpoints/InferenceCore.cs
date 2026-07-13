using System.Threading.Channels;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Endpoints;

/// <summary>
/// Routing + pre-stream failover + metrics, shared by both client-facing dialects. The
/// outcome is deliberately format-neutral: <see cref="InferenceEndpoints"/> renders it as
/// Ollama NDJSON and <see cref="OpenAi.OpenAiEndpoints"/> renders it as OpenAI SSE.
///
/// Two copies of failover logic is how failover quietly rots — there is exactly one here.
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
        string ServedBy = ServedByNode)
    {
        public static DispatchOutcome Streaming(ChannelReader<InferenceChunk> stream)
            => new(stream, null, null, null);

        public static DispatchOutcome Blocking(string responseJson)
            => new(null, responseJson, null, null);

        public static DispatchOutcome Failure(int status, string message)
            => new(null, null, status, message);

        public static DispatchOutcome Fallback(FallbackResult result)
            => new(result.Stream, result.ResponseJson, null, null, ServedByFallback);

        public bool IsError => ErrorStatus is not null;
    }

    public static async Task<DispatchOutcome> DispatchAsync(
        string kind,
        string rawJson,
        string? model,
        bool? stream,
        string? conversationKey,
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

        var node = router.Route(model, conversationKey);

        // Cloud burst. Off by default, and when off this is a single false — the 404 below is
        // byte-for-byte what every release since 1.0 has returned.
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
            return await DispatchWithFailoverAsync(
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
                    result.Error ?? "node failed to run inference");
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
                    result.Error ?? "node failed to run inference");
            }

            metrics.RecordFailoverSucceeded();
            return DispatchOutcome.Blocking(result.ResponseJson ?? "{}");
        }
    }
}
