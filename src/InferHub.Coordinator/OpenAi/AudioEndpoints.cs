using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Audio;
using InferHub.Shared.Contracts;
using InferHub.Shared.OpenAi;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.OpenAi;

/// <summary>
/// Speech in and speech out, on OpenAI's audio API (phase 42): <c>POST /v1/audio/transcriptions</c>
/// and <c>POST /v1/audio/speech</c>.
/// </summary>
/// <remarks>
/// <para>
/// The client surface is OpenAI's, exactly — <b>D1</b>, and it is phase 21's argument again. Every
/// SDK already speaks it, so pointing an existing app at your own GPU is a base-URL change.
/// Inventing <c>/api/tts</c> would be a second dialect whose only merit is that we designed it, and
/// unlike chat there is no <em>Ollama</em> audio dialect to be the other side of a translation:
/// there is exactly one client shape here, and the node-facing side is a <c>ToolJob</c> (phase-40
/// D3).
/// </para>
/// <para>
/// These routes sit under <c>/v1</c>, which <c>BearerApiKeyMiddleware</c> guards
/// (phase-21 D2) — <c>AudioEndpointTests.TheAudioRoutesAreBehindTheBearerGuard</c> fails if that
/// ever stops being true, because a client-facing route under an unguarded prefix is an
/// unauthenticated inference API and this one spends GPU time on somebody's uploaded file.
/// </para>
/// <para>
/// <b>The uploaded bytes are held for the dispatch and dropped</b> (D5). No temp file, no cache, and
/// no log line containing the audio or the transcript at any level: a transcription request is a
/// recording of somebody's voice and the answer is what they said. The log line carries the model,
/// the duration and the outcome. <c>AudioPrivacyTests</c> asserts it against a capturing logger.
/// </para>
/// </remarks>
public static class AudioEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAudioEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/audio/transcriptions", HandleTranscriptionAsync);
        app.MapPost("/v1/audio/speech", HandleSpeechAsync);
        return app;
    }

    private static async Task<IResult> HandleTranscriptionAsync(
        HttpContext httpContext,
        Services.IRouter router,
        INodeRegistry registry,
        IToolDispatcher tools,
        Metrics metrics,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.OpenAi.Audio");
        metrics.RecordOpenAiRequest();

        if (!httpContext.Request.HasFormContentType)
        {
            return Error(400, "this endpoint takes multipart/form-data with a 'file' part", OpenAiErrorTypes.InvalidRequest);
        }

        var edge = UploadPath.OptionsFrom(httpContext);
        var maxBytes = edge.MaxAttachmentBytes;

        // Every ceiling this request will meet, moved together (phase-53 D6). Before the body is
        // touched, because Kestrel's is read-only once the read has started.
        UploadLimits.Prepare(httpContext, edge.MaxAttachmentBytes, edge.MaxStreamedBytes);

        if (UploadPath.TooLargeUpFront(httpContext, edge) is { } declaredTooLarge)
        {
            return Error(413, declaredTooLarge, OpenAiErrorTypes.InvalidRequest, param: "file");
        }

        if (UploadPath.ShouldStream(httpContext, edge))
        {
            return await HandleStreamedTranscriptionAsync(
                httpContext, router, registry, tools, metrics, logger, edge, cancellationToken);
        }

        IFormCollection form;

        try
        {
            form = await httpContext.Request.ReadFormAsync(cancellationToken);
        }
        catch (BadHttpRequestException ex)
        {
            return Error(400, ex.Message, OpenAiErrorTypes.InvalidRequest);
        }

        var file = form.Files["file"] ?? form.Files.FirstOrDefault();

        var request = TranscriptionRequest.TryCreate(
            form["model"].FirstOrDefault(),
            form["response_format"].FirstOrDefault(),
            form["language"].FirstOrDefault(),
            form["prompt"].FirstOrDefault(),
            form["temperature"].FirstOrDefault(),
            file is not null,
            out var invalid);

        if (request is null)
        {
            return Error(400, invalid, OpenAiErrorTypes.InvalidRequest, param: "model");
        }

        if (file!.Length > maxBytes)
        {
            // Refused before anything is buffered onward, with the limit in the sentence
            // (phase-40 D4). A caller told only "too large" finds the ceiling by bisection.
            return Error(
                413,
                ToolAttachmentLimits.TooLarge(file.FileName ?? "file", file.Length, maxBytes),
                OpenAiErrorTypes.InvalidRequest,
                param: "file");
        }

        var context = InferenceCore.ClientContext.From(httpContext);
        var admission = context.Admission.TryAdmit(context.Client, request.Model, UsageUnits.AudioSeconds);

        if (!admission.Allowed)
        {
            logger.LogInformation(
                "Rejected transcription for client {ClientId}: {Status} {Message}",
                context.Client.Id,
                admission.Status,
                admission.Message);

            return Rejected(httpContext, admission);
        }

        using var lease = admission.Lease;

        var node = router.Route(request.Model, capability: CapabilityKinds.Transcribe);

        if (node is null)
        {
            return NoNode(httpContext, registry, CapabilityKinds.Transcribe, request.Model);
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);

        var job = new ToolJob(
            Guid.NewGuid(),
            CapabilityKinds.Transcribe,
            request.Model,
            request.ToToolPayload(),
            [new ToolAttachment(
                file.FileName ?? "audio",
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                buffer.ToArray())]);

        var result = await tools.DispatchToolAsync(node, job, cancellationToken);
        var outcome = AudioRenderer.Transcription(result, request);

        // Model, seconds, outcome. Not the filename the caller chose, and never the text.
        logger.LogInformation(
            "Transcription {JobId} ({Model}) on node {NodeId}: {Status}, {Seconds:F1}s of audio",
            job.JobId,
            request.Model,
            node.NodeId,
            outcome.Status,
            outcome.Units);

        httpContext.Response.Headers[InferenceCore.ServedByHeader] = "node";
        Meter(context, metrics, outcome, CapabilityKinds.Transcribe, request.Model);

        return Render(httpContext, outcome);
    }

    /// <summary>
    /// The same transcription, with the file streamed through the hub instead of buffered in it
    /// (phase 53). Everything before the bytes — validation, admission, routing — happens on the
    /// leading form fields, which is what D3's ordering requirement exists to make possible.
    /// </summary>
    private static async Task<IResult> HandleStreamedTranscriptionAsync(
        HttpContext httpContext,
        Services.IRouter router,
        INodeRegistry registry,
        IToolDispatcher tools,
        Metrics metrics,
        ILogger logger,
        ToolEdgeOptions edge,
        CancellationToken cancellationToken)
    {
        StreamedUploadStart start;

        try
        {
            start = await UploadPath.BeginAsync(httpContext, edge, cancellationToken);
        }
        catch (BadHttpRequestException ex)
        {
            return Error(400, ex.Message, OpenAiErrorTypes.InvalidRequest);
        }

        var request = TranscriptionRequest.TryCreate(
            start.Field("model"),
            start.Field("response_format"),
            start.Field("language"),
            start.Field("prompt"),
            start.Field("temperature"),
            start.Upload.HasFile,
            out var invalid);

        if (request is null)
        {
            return Error(400, invalid, OpenAiErrorTypes.InvalidRequest, param: "model");
        }

        var context = InferenceCore.ClientContext.From(httpContext);
        var admission = context.Admission.TryAdmit(context.Client, request.Model, UsageUnits.AudioSeconds);

        if (!admission.Allowed)
        {
            logger.LogInformation(
                "Rejected transcription for client {ClientId}: {Status} {Message}",
                context.Client.Id,
                admission.Status,
                admission.Message);

            return Rejected(httpContext, admission);
        }

        using var lease = admission.Lease;

        // D5. A fleet with capable nodes but none that can pull a stream is fleet state, so it gets
        // the phase-40 D4 shape — never a silent fall back to the buffered path, which would work
        // right up to the 25 MB it cannot do.
        var node = router.Route(
            request.Model,
            capability: CapabilityKinds.Transcribe,
            requireStreamedAttachments: true);

        if (node is null)
        {
            return NoStreamingNode(httpContext, registry, CapabilityKinds.Transcribe, request.Model);
        }

        var job = new ToolJob(
            Guid.NewGuid(),
            CapabilityKinds.Transcribe,
            request.Model,
            request.ToToolPayload(),
            Attachments: null,
            HasStreamedAttachments: true);

        using var upload = tools.RegisterUpload(job.JobId, start.Upload);

        ToolResult result;

        try
        {
            result = await tools.DispatchToolAsync(node, job, cancellationToken);
        }
        catch (NodeDisconnectedException)
        {
            // D4. There is no failover here and there cannot be: the body is consumed, past the hub
            // and into a node that just died, and the client's socket cannot be rewound. Phase-47
            // D7's shape — say what happened and let the caller decide, rather than silently asking
            // for the upload again without telling them that is what this is.
            logger.LogWarning(
                "Streamed transcription {JobId} lost node {NodeId} mid-job after {Bytes} bytes",
                job.JobId,
                node.NodeId,
                start.Upload.BytesStreamed);

            return Error(
                502,
                "the node handling this request disconnected while the upload was in flight; " +
                "a streamed upload is not retried on another node, because the body has already been read",
                OpenAiErrorTypes.ApiError,
                code: "node_lost");
        }

        // The upload's own verdict outranks the node's: a worker told "the stream ended" reports a
        // failed job, and answering with that instead of the 413 would send the caller looking at
        // the model.
        if (start.Upload.Failure is { } failure)
        {
            return UploadRefused(failure);
        }

        var outcome = AudioRenderer.Transcription(result, request);

        // Model, seconds, bytes, outcome. Not the filename, and never the text.
        logger.LogInformation(
            "Streamed transcription {JobId} ({Model}) on node {NodeId}: {Status}, {Seconds:F1}s of audio, {Bytes} bytes streamed",
            job.JobId,
            request.Model,
            node.NodeId,
            outcome.Status,
            outcome.Units,
            start.Upload.BytesStreamed);

        httpContext.Response.Headers[InferenceCore.ServedByHeader] = "node";
        Meter(context, metrics, outcome, CapabilityKinds.Transcribe, request.Model);

        return Render(httpContext, outcome);
    }

    /// <summary>Each upload failure kind renders as itself — "it failed" is not a status.</summary>
    private static IResult UploadRefused(UploadFailure failure) => failure.Kind switch
    {
        UploadFailureKind.TooLarge => Error(413, failure.Message, OpenAiErrorTypes.InvalidRequest, param: "file"),
        UploadFailureKind.FieldAfterFile => Error(400, failure.Message, OpenAiErrorTypes.InvalidRequest),
        _ => Error(400, failure.Message, OpenAiErrorTypes.InvalidRequest)
    };

    /// <summary>
    /// <see cref="NoNode"/>'s sibling for a streamed job (phase-53 D5). It names the reason
    /// separately, because "no node provides transcribe" is wrong and misleading on a fleet that
    /// provides it perfectly well and simply cannot take a 300 MB upload.
    /// </summary>
    private static IResult NoStreamingNode(
        HttpContext httpContext,
        INodeRegistry registry,
        string capability,
        string model)
    {
        if (registry.FindNodesWithModel(model, capability).Count > 0)
        {
            httpContext.Response.Headers.RetryAfter = ToolEndpoints.CapabilityRetryAfterSeconds.ToString();

            return Error(
                503,
                $"no node currently serving '{capability}' for model '{model}' accepts a streamed upload; " +
                "an upload this large needs a node running v3.21 or later with Tools:MaxStreamedBytes set",
                OpenAiErrorTypes.ApiError,
                code: "capability_unavailable");
        }

        return NoNode(httpContext, registry, capability, model);
    }

    private static async Task<IResult> HandleSpeechAsync(
        HttpContext httpContext,
        Services.IRouter router,
        INodeRegistry registry,
        IToolDispatcher tools,
        Metrics metrics,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.OpenAi.Audio");
        metrics.RecordOpenAiRequest();

        using var reader = new StreamReader(httpContext.Request.Body);
        var raw = await reader.ReadToEndAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return Error(400, "request body is required", OpenAiErrorTypes.InvalidRequest);
        }

        var request = SpeechRequest.TryParse(raw, out var invalid);

        if (request is null)
        {
            return Error(400, invalid, OpenAiErrorTypes.InvalidRequest, param: "input");
        }

        var context = InferenceCore.ClientContext.From(httpContext);
        var admission = context.Admission.TryAdmit(context.Client, request.Model, UsageUnits.Characters);

        if (!admission.Allowed)
        {
            logger.LogInformation(
                "Rejected speech for client {ClientId}: {Status} {Message}",
                context.Client.Id,
                admission.Status,
                admission.Message);

            return Rejected(httpContext, admission);
        }

        using var lease = admission.Lease;

        var node = router.Route(request.Model, capability: CapabilityKinds.Speak);

        if (node is null)
        {
            return NoNode(httpContext, registry, CapabilityKinds.Speak, request.Model);
        }

        var job = new ToolJob(Guid.NewGuid(), CapabilityKinds.Speak, request.Model, request.ToToolPayload());
        var result = await tools.DispatchToolAsync(node, job, cancellationToken);
        var outcome = AudioRenderer.Speech(result, request);

        // The character count, never the characters.
        logger.LogInformation(
            "Speech {JobId} ({Model}) on node {NodeId}: {Status}, {Characters} characters",
            job.JobId,
            request.Model,
            node.NodeId,
            outcome.Status,
            request.Characters);

        httpContext.Response.Headers[InferenceCore.ServedByHeader] = "node";
        Meter(context, metrics, outcome, CapabilityKinds.Speak, request.Model);

        return Render(httpContext, outcome);
    }

    /// <summary>A failed job is not billed; a succeeded one is billed in its own unit (D7).</summary>
    /// <remarks>
    /// Phase 45 added the scrape counter here rather than a second call site, so the number on a
    /// dashboard and the number on a bill can never come from two different definitions of "done".
    /// </remarks>
    private static void Meter(
        InferenceCore.ClientContext context,
        Metrics metrics,
        AudioOutcome outcome,
        string kind,
        string model)
    {
        if (!outcome.IsError)
        {
            context.Usage.RecordUnits(context.Client, kind, model, outcome.Units, outcome.UnitKind);
            metrics.RecordToolUnits(kind, model, outcome.Units, outcome.UnitKind);
        }
    }

    /// <summary>
    /// Phase-40 D5, unchanged and reused rather than re-argued: a capability nobody provides is
    /// fleet state and gets the saturation shape; a model nobody holds at all is still the 404 that
    /// is byte-identical to the one a scoped-out model produces (phase-25 D4).
    /// </summary>
    private static IResult NoNode(HttpContext httpContext, INodeRegistry registry, string capability, string model)
    {
        if (ToolEndpoints.KnownToTheFleet(registry, model))
        {
            httpContext.Response.Headers.RetryAfter = ToolEndpoints.CapabilityRetryAfterSeconds.ToString();

            return Error(
                503,
                $"no node currently provides '{capability}' for model '{model}'",
                OpenAiErrorTypes.ApiError,
                code: "capability_unavailable");
        }

        return Error(404, $"model '{model}' not found", OpenAiErrorTypes.NotFound, param: "model", code: "model_not_found");
    }

    private static IResult Rejected(HttpContext httpContext, AdmissionDecision admission)
    {
        if (admission.RetryAfterSeconds is { } retryAfter)
        {
            httpContext.Response.Headers.RetryAfter = retryAfter.ToString();
        }

        var (type, code) = admission.Status switch
        {
            404 => (OpenAiErrorTypes.NotFound, "model_not_found"),
            429 => (OpenAiErrorTypes.RateLimit, "rate_limit_exceeded"),
            402 => (OpenAiErrorTypes.RateLimit, "insufficient_quota"),
            _ => (OpenAiErrorTypes.ApiError, (string?)null)
        };

        return Error(admission.Status, admission.Message!, type, code: code);
    }

    /// <summary>
    /// The ten lines phase-37 D6 keeps per host. Everything above them — the status, the content
    /// type, the bytes — was decided by <see cref="AudioRenderer"/>, which the solo node calls too.
    /// </summary>
    private static IResult Render(HttpContext httpContext, AudioOutcome outcome)
    {
        if (outcome.IsError)
        {
            if (outcome.RetryAfterSeconds is { } retryAfter)
            {
                httpContext.Response.Headers.RetryAfter = retryAfter.ToString();
            }

            return Error(outcome.Status, outcome.Error!, outcome.ErrorType, code: outcome.ErrorCode);
        }

        return outcome.Bytes is { } bytes
            ? Results.Bytes(bytes, outcome.ContentType!, outcome.FileName)
            : Results.Text(outcome.Text ?? string.Empty, outcome.ContentType!);
    }

    private static IResult Error(int status, string message, string type, string? param = null, string? code = null)
        => Results.Json(
            OpenAiErrorEnvelope.Create(message, type, code, param),
            JsonOptions,
            statusCode: status);
}
