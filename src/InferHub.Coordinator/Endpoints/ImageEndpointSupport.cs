using System.Text.Json;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;
using InferHub.Shared.OpenAi;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Endpoints;

/// <summary>
/// What the two hub image routes — the synchronous <c>/v1</c> one and the async <c>/api</c> one —
/// answer identically, in one place (phase 47).
/// </summary>
/// <remarks>
/// Both surfaces accept the same body and the same three extension headers, and both must refuse
/// an over-budget client, an absent capability and a nonexistent model with the same status, the
/// same header and the same sentence. Two copies of that is how a caller ends up able to tell which
/// route answered by reading the error — the exact class of difference phase-42's parity suite was
/// written to find, prevented by construction instead.
/// </remarks>
internal static class ImageEndpointSupport
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ImageLimits Limits(HttpContext httpContext)
    {
        var images = httpContext.RequestServices.GetService<IOptions<ImageEdgeOptions>>()?.Value ?? new ImageEdgeOptions();

        var attachments = httpContext.RequestServices.GetService<IOptions<ToolEdgeOptions>>()?.Value.MaxAttachmentBytes
            ?? ToolAttachmentLimits.DefaultMaxBytes;

        return images.Resolve(attachments);
    }

    /// <summary>
    /// Phase-40 D5, unchanged and reused rather than re-argued: a capability nobody provides is
    /// fleet state and gets the saturation shape; a model nobody holds at all is still the 404 that
    /// is byte-identical to the one a scoped-out model produces (phase-25 D4).
    /// </summary>
    /// <param name="lease">
    /// The admission lease, disposed on the way out. A refusal that leaked one would hold a slot of
    /// the client's concurrency limit for as long as the process lived — which reads to them as a
    /// rate limit that never resets.
    /// </param>
    public static IResult? NoNode(HttpContext httpContext, INodeRegistry registry, string model, IDisposable? lease)
    {
        var router = httpContext.RequestServices.GetRequiredService<Services.IRouter>();

        if (router.Route(model, capability: CapabilityKinds.Image) is not null)
        {
            return null;
        }

        lease?.Dispose();

        if (ToolEndpoints.KnownToTheFleet(registry, model))
        {
            httpContext.Response.Headers.RetryAfter = ToolEndpoints.CapabilityRetryAfterSeconds.ToString();

            return Error(
                503,
                $"no node currently provides '{CapabilityKinds.Image}' for model '{model}'",
                OpenAiErrorTypes.ApiError,
                code: "capability_unavailable");
        }

        return Error(404, $"model '{model}' not found", OpenAiErrorTypes.NotFound, param: "model", code: "model_not_found");
    }

    public static IResult Rejected(HttpContext httpContext, AdmissionDecision admission)
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

    public static IResult Error(int status, string message, string type, string? param = null, string? code = null)
        => Results.Json(
            OpenAiErrorEnvelope.Create(message, type, code, param),
            Json,
            statusCode: status);
}

/// <summary>
/// Waits for a job to reach a terminal state, for the synchronous route and for solo mode.
/// </summary>
/// <remarks>
/// It subscribes rather than polls, because the whole point of the phase is that a caller finds out
/// when something happens rather than a second later. The timeout is a real bound and returning
/// <c>false</c> is not a failure of the job: the work keeps going, and the caller is handed the id
/// that lets them collect it.
/// </remarks>
internal static class ImageJobWait
{
    public static async Task<bool> ForTerminalAsync(
        ImageJobStore store,
        ImageJobRecord record,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        if (record.IsTerminal)
        {
            return true;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged(ImageJobRecord changed)
        {
            if (changed.Id == record.Id && changed.IsTerminal)
            {
                done.TrySetResult();
            }
        }

        store.Changed += OnChanged;

        try
        {
            // Re-checked after subscribing: a job that finished between the check above and the
            // subscription would otherwise wait out the whole budget for an event already fired.
            if (record.IsTerminal)
            {
                return true;
            }

            await done.Task.WaitAsync(budget, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            store.Changed -= OnChanged;
        }
    }
}
