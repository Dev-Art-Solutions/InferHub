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

    /// <summary>
    /// The legacy value, still emitted for a deployment configured through the <c>Fallback:</c>
    /// section (61 D4). A named provider answers <c>provider:&lt;id&gt;</c>, which the dispatcher
    /// hands back rather than this file deciding.
    /// </summary>
    public const string ServedByFallback = "fallback";

    public const string ServedByHeader = "X-InferHub-Served-By";

    /// <summary>
    /// <c>Retry-After</c> on the phase-40 D5 "nobody provides this capability" 503. A hint, not a
    /// promise — a node that can do the work may connect at any time, or never. Thirty seconds is
    /// the same order as the queue's bound, so a client's backoff behaves the same against either
    /// 503 rather than needing to know which kind it got.
    /// </summary>
    private const int CapabilityRetryAfterSeconds = 30;

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

        public static DispatchOutcome Provider(ProviderResult result)
            => new(result.Stream, result.ResponseJson, null, null, result.ServedBy);

        public bool IsError => ErrorStatus is not null;
    }

    /// <summary>
    /// The innermost human sentence in a node's error, for the client-facing envelope.
    /// </summary>
    /// <remarks>
    /// The implementation moved to <see cref="NodeErrorText"/> in phase 37, because a solo node has
    /// to unwrap its own backend's errors too — and solo is the deployment most likely to surface a
    /// raw one, with no hub between the user and Ollama. This stays as the coordinator's name for
    /// it: one dispatch path, so both client dialects still get the same unwrapping (phase-29 D6).
    /// </remarks>
    internal static string ReadableNodeError(string? error) => NodeErrorText.Readable(error);

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
        Services.INodeRegistry registry,
        IDispatcher dispatcher,
        IProviderDispatcher providers,
        Metrics metrics,
        ILogger logger,
        CancellationToken cancellationToken,
        ProviderSteer steer = default)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return DispatchOutcome.Failure(StatusCodes.Status400BadRequest, "model is required");
        }

        // What kind of work this surface is asking for (phase 40). Derived from the job kind
        // rather than passed in, so the two client dialects cannot disagree about it.
        var capability = CapabilityKinds.ForJobKind(kind);

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
            var node = router.Route(model, conversationKey, capability: capability);

            // Where a provider gets a say (phase 65). Off by default, and when off this is a single
            // `No` — the 404 below is byte-for-byte what every release since 1.0 has returned. With
            // Policy=no-node-or-saturated a saturated fleet overflows to the upstream INSTEAD of
            // queueing (the upstream answers in seconds, the queue in tens of seconds); with
            // `prefer` or `only` the provider is asked before the fleet is consulted at all, and
            // with an X-InferHub-Provider header the caller has named one of the two directions.
            var decision = providers.Decide(model, hasCapableNode: node is not null, steer);

            if (decision.IsRefusal)
            {
                // A steer nobody can honour. Refused here, before anything leaves the hub, rather
                // than quietly served by whoever the config happened to pick.
                logger.LogInformation("Refused {Kind} for {Model}: {Message}", kind, model, decision.ErrorMessage);
                return DispatchOutcome.Failure(decision.ErrorStatus!.Value, decision.ErrorMessage!);
            }

            if (decision.Serve)
            {
                try
                {
                    var result = await providers.DispatchAsync(
                        kind,
                        rawJson,
                        model,
                        stream is not false,
                        cancellationToken);

                    if (result.Stream is { } providerStream)
                    {
                        leaseHandedOff = true;
                        return DispatchOutcome.Provider(result) with
                        {
                            Stream = context.Usage.WrapStream(providerStream, context.Client, kind, model, fallback: true, lease)
                        };
                    }

                    context.Usage.RecordResponse(context.Client, kind, model, result.ResponseJson ?? "{}", fallback: true);
                    return DispatchOutcome.Provider(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Provider dispatch failed for model {Model}", model);

                    if (node is null)
                    {
                        return DispatchOutcome.Failure(
                            StatusCodes.Status502BadGateway,
                            $"no node holds model '{model}' and the fallback upstream failed: {ex.Message}");
                    }

                    // A node exists, and whether it may catch this is the policy's answer, not the
                    // error's (65 D3). `prefer` and a saturation burst say yes — falling back to a
                    // local node is not a second disclosure. `only` and a steered request say no:
                    // answering from different weights than the caller asked for, silently, is the
                    // one failure that looks like a success.
                    if (!decision.NodeIsBackstop)
                    {
                        return DispatchOutcome.Failure(
                            StatusCodes.Status502BadGateway,
                            $"the provider serving model '{model}' failed and no other route was "
                            + $"permitted for this request: {ex.Message}");
                    }
                }
            }

            if (node is null)
            {
                // Phase-40 D5. "This model exists on the fleet but nobody will do *this* with it"
                // is a fleet-state answer, the same category as saturation, so it is a 503 with
                // Retry-After rather than the 404 that means "no such model". Authorization has
                // already run (admission, above), so this can never be used to probe for a model
                // a client is not allowed to see: it only ever reflects models it already reaches.
                // Phase 69 D3. Three questions in the order the fixes go in: does anybody hold this
                // name at all, can the ones that do actually answer, and is it the capability that
                // is missing. A 404 for a fleet whose only holder has a dead inference server is
                // the expensive lie — it sends an operator to pull a model already on the box.
                var holders = registry.FindNodesWithModel(model, includeUnserviceable: true).Count;

                if (holders > 0 && registry.FindNodesWithModel(model).Count == 0)
                {
                    logger.LogWarning(
                        "Model {Model} is held only by nodes whose inference backend is unhealthy",
                        model);

                    return DispatchOutcome.Failure(
                        StatusCodes.Status503ServiceUnavailable,
                        $"every node holding model '{model}' reports an unhealthy inference backend",
                        CapabilityRetryAfterSeconds);
                }

                if (capability is not null && holders > 0)
                {
                    logger.LogInformation(
                        "No node provides capability {Capability} for model {Model}",
                        capability,
                        model);

                    return DispatchOutcome.Failure(
                        StatusCodes.Status503ServiceUnavailable,
                        $"no node currently provides '{capability}' for model '{model}'",
                        CapabilityRetryAfterSeconds);
                }

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
                node = router.Route(model, conversationKey, capability: capability) ?? node;
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
                    capability,
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
        string? capability,
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

            var retryNode = router.Route(model, conversationKey, excludeConnectionId: ex.ConnectionId, capability: capability);

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
