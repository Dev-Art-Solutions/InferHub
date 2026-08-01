using InferHub.Node.Configuration;
using InferHub.Node.Tools;
using InferHub.Shared.Audio;
using InferHub.Shared.Contracts;
using InferHub.Shared.OpenAi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace InferHub.Node.LocalApi;

/// <summary>
/// <c>POST /v1/audio/transcriptions</c> and <c>POST /v1/audio/speech</c>, served by the node itself
/// (phase 42; phase-41 D8).
/// </summary>
/// <remarks>
/// <para>
/// Solo mode gets audio on the same day the mesh does, because it is the same executor with routing
/// deleted — phase-37 D2's framing, and it is what the whole track was heading for: one
/// <c>docker run</c>, no coordinator, and a box that transcribes.
/// </para>
/// <para>
/// Everything a client can observe is decided by <see cref="AudioRenderer"/> and the request
/// records in <c>InferHub.Shared</c>, which the coordinator calls too. What is written twice is the
/// form reading and the response writing — phase-37 D6's line — and <c>AudioParityTests</c> drives
/// the same requests at both hosts over real Kestrel because a difference in either is a difference
/// a caller sees.
/// </para>
/// </remarks>
internal static class LocalAudioEndpoints
{
    public static IEndpointRouteBuilder MapLocalAudioEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/audio/transcriptions", HandleTranscriptionAsync);
        app.MapPost("/v1/audio/speech", HandleSpeechAsync);
        return app;
    }

    private static async Task<IResult> HandleTranscriptionAsync(
        HttpContext httpContext,
        ToolExecutor executor,
        CancellationToken cancellationToken)
    {
        if (LocalApiEndpoints.CapabilityDisabled(httpContext, CapabilityKinds.Transcribe, out var refusal))
        {
            return Error(503, refusal, OpenAiErrorTypes.ApiError, code: "capability_disabled");
        }

        if (!httpContext.Request.HasFormContentType)
        {
            return Error(400, "this endpoint takes multipart/form-data with a 'file' part", OpenAiErrorTypes.InvalidRequest);
        }

        var maxBytes = httpContext.RequestServices.GetService<IOptions<ToolOptions>>()?.Value.MaxAttachmentBytes
            ?? ToolAttachmentLimits.DefaultMaxBytes;

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
            return Error(
                413,
                ToolAttachmentLimits.TooLarge(file.FileName ?? "file", file.Length, maxBytes),
                OpenAiErrorTypes.InvalidRequest,
                param: "file");
        }

        if (!executor.Provides(CapabilityKinds.Transcribe, request.Model))
        {
            return NotProvided(httpContext, CapabilityKinds.Transcribe, request.Model);
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

        var result = await executor.RunAsync(job, cancellationToken);

        httpContext.Response.Headers[LocalApiEndpoints.ServedByHeader] = LocalApiEndpoints.ServedBySolo;
        return Render(httpContext, AudioRenderer.Transcription(result, request));
    }

    private static async Task<IResult> HandleSpeechAsync(
        HttpContext httpContext,
        ToolExecutor executor,
        CancellationToken cancellationToken)
    {
        if (LocalApiEndpoints.CapabilityDisabled(httpContext, CapabilityKinds.Speak, out var refusal))
        {
            return Error(503, refusal, OpenAiErrorTypes.ApiError, code: "capability_disabled");
        }

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

        if (!executor.Provides(CapabilityKinds.Speak, request.Model))
        {
            return NotProvided(httpContext, CapabilityKinds.Speak, request.Model);
        }

        var job = new ToolJob(Guid.NewGuid(), CapabilityKinds.Speak, request.Model, request.ToToolPayload());
        var result = await executor.RunAsync(job, cancellationToken);

        httpContext.Response.Headers[LocalApiEndpoints.ServedByHeader] = LocalApiEndpoints.ServedBySolo;
        return Render(httpContext, AudioRenderer.Speech(result, request));
    }

    /// <summary>
    /// The hub's 503 with the same <c>Retry-After</c>, in the node's own words: "this node" rather
    /// than "no node", because on a standalone box those are the same sentence and only one of them
    /// is true.
    /// </summary>
    private static IResult NotProvided(HttpContext httpContext, string capability, string model)
    {
        httpContext.Response.Headers.RetryAfter = ToolExecutor.CapabilityRetryAfterSeconds.ToString();

        return Error(
            503,
            $"this node does not provide '{capability}' for model '{model}'",
            OpenAiErrorTypes.ApiError,
            code: "capability_unavailable");
    }

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
            LocalApiEndpoints.JsonOptions,
            statusCode: status);
}
