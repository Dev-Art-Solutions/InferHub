using InferHub.Node.Configuration;
using InferHub.Node.Tools;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;
using InferHub.Shared.OpenAi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InferHub.Node.LocalApi;

/// <summary>
/// <c>POST /v1/images/generations</c>, served by the node itself (phase 46; phase-41 D8).
/// </summary>
/// <remarks>
/// <para>
/// Solo mode gets images on the same day the mesh does, because it is the same executor with routing
/// deleted — phase-37 D2's framing, and it is the deployment this whole line of work has been heading
/// for: one <c>docker run</c>, no coordinator, and a box that makes pictures.
/// </para>
/// <para>
/// Everything a client can observe is decided by <see cref="ImageRenderer"/> and
/// <see cref="ImageGenerationRequest"/> in <c>InferHub.Shared</c>, which the coordinator calls too.
/// What is written twice is the body reading and the response writing — phase-37 D6's line — and
/// <c>ImageParityTests</c> drives the same requests at both hosts over real Kestrel, because a
/// difference in either is a difference a caller sees.
/// </para>
/// </remarks>
internal static class LocalImageEndpoints
{
    public static IEndpointRouteBuilder MapLocalImageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/images/generations", HandleGenerationAsync);

        app.MapPost("/v1/images/edits", (HttpContext httpContext, ToolExecutor executor, CancellationToken cancellationToken)
            => HandleEditAsync(ImageOperations.Edit, httpContext, executor, cancellationToken));

        app.MapPost("/v1/images/variations", (HttpContext httpContext, ToolExecutor executor, CancellationToken cancellationToken)
            => HandleEditAsync(ImageOperations.Variation, httpContext, executor, cancellationToken));

        return app;
    }

    private static async Task<IResult> HandleGenerationAsync(
        HttpContext httpContext,
        ToolExecutor executor,
        CancellationToken cancellationToken)
    {
        if (LocalApiEndpoints.CapabilityDisabled(httpContext, CapabilityKinds.Image, out var refusal))
        {
            return Error(503, refusal, OpenAiErrorTypes.ApiError, code: "capability_disabled");
        }

        using var reader = new StreamReader(httpContext.Request.Body);
        var raw = await reader.ReadToEndAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return Error(400, "request body is required", OpenAiErrorTypes.InvalidRequest);
        }

        var request = ImageGenerationRequest.TryParse(
            raw,
            name => httpContext.Request.Headers.TryGetValue(name, out var values) ? values.ToString() : null,
            Limits(httpContext),
            out var invalid,
            out var invalidParam);

        if (request is null)
        {
            return Error(400, invalid, OpenAiErrorTypes.InvalidRequest, param: invalidParam);
        }

        return await RunAsync(httpContext, executor, request, cancellationToken);
    }

    /// <summary>
    /// <c>POST /v1/images/edits</c> and <c>POST /v1/images/variations</c>, on a solo node (phase 50).
    /// </summary>
    private static async Task<IResult> HandleEditAsync(
        string operation,
        HttpContext httpContext,
        ToolExecutor executor,
        CancellationToken cancellationToken)
    {
        if (LocalApiEndpoints.CapabilityDisabled(httpContext, CapabilityKinds.ImageEdit, out var refused))
        {
            return Error(503, refused, OpenAiErrorTypes.ApiError, code: "capability_disabled");
        }

        var (request, refusal) = await LocalImageForm.ReadEditAsync(httpContext, operation, cancellationToken);

        return refusal ?? await RunAsync(httpContext, executor, request!, cancellationToken);
    }

    private static async Task<IResult> RunAsync(
        HttpContext httpContext,
        ToolExecutor executor,
        IImageRequest request,
        CancellationToken cancellationToken)
    {
        if (!executor.Provides(request.Capability, request.Model))
        {
            return NotProvided(httpContext, request.Capability, request.Model);
        }

        var job = new ToolJob(
            Guid.NewGuid(),
            request.Capability,
            request.Model,
            request.ToToolPayload(),
            request.Attachments.Count == 0 ? null : request.Attachments);

        var result = await executor.RunAsync(job, cancellationToken);

        httpContext.Response.Headers[LocalApiEndpoints.ServedByHeader] = LocalApiEndpoints.ServedBySolo;

        return Render(
            httpContext,
            ImageRenderer.Render(result, request, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    private static ImageLimits Limits(HttpContext httpContext) => LocalImageForm.Limits(httpContext);

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

    private static IResult Render(HttpContext httpContext, ImageOutcome outcome)
    {
        if (outcome.IsError)
        {
            if (outcome.RetryAfterSeconds is { } retryAfter)
            {
                httpContext.Response.Headers.RetryAfter = retryAfter.ToString();
            }

            return Error(outcome.Status, outcome.Error!, outcome.ErrorType, outcome.ErrorParam, outcome.ErrorCode);
        }

        return Results.Text(outcome.Json ?? "{}", ImageRenderer.ContentType);
    }

    private static IResult Error(int status, string message, string type, string? param = null, string? code = null)
        => Results.Json(
            OpenAiErrorEnvelope.Create(message, type, code, param),
            LocalApiEndpoints.JsonOptions,
            statusCode: status);
}
