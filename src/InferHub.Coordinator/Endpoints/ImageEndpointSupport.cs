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

        return images.Resolve(AttachmentCap(httpContext));
    }

    public static long AttachmentCap(HttpContext httpContext) =>
        httpContext.RequestServices.GetService<IOptions<ToolEdgeOptions>>()?.Value.MaxAttachmentBytes
        ?? ToolAttachmentLimits.DefaultMaxBytes;

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
    /// <param name="capability">
    /// <c>image</c> or, since phase 50, <c>image-edit</c>. The 503 for the second one <b>names the
    /// recipes on this fleet that can edit</b>, which the hub genuinely knows — it is the fleet's
    /// own capability declarations, not a model catalogue (which the hub still does not have, see
    /// phase-46 D6). "FLUX cannot inpaint, but sdxl and sd15 can" is the whole difference between a
    /// refusal somebody can act on and one that sends them to the docs.
    /// </param>
    public static IResult? NoNode(
        HttpContext httpContext,
        INodeRegistry registry,
        string model,
        IDisposable? lease,
        string capability = CapabilityKinds.Image)
    {
        var router = httpContext.RequestServices.GetRequiredService<Services.IRouter>();

        if (router.Route(model, capability: capability) is not null)
        {
            return null;
        }

        lease?.Dispose();

        if (ToolEndpoints.KnownToTheFleet(registry, model))
        {
            httpContext.Response.Headers.RetryAfter = ToolEndpoints.CapabilityRetryAfterSeconds.ToString();

            return Error(
                503,
                $"no node currently provides '{capability}' for model '{model}'{Alternatives(registry, capability)}",
                OpenAiErrorTypes.ApiError,
                code: "capability_unavailable");
        }

        return Error(404, $"model '{model}' not found", OpenAiErrorTypes.NotFound, param: "model", code: "model_not_found");
    }

    /// <summary>
    /// The models the fleet <em>does</em> serve under this capability, if any.
    /// </summary>
    /// <remarks>
    /// Deliberately silent when there are none: a sentence ending "the fleet can edit with: " is
    /// worse than one that stops, and an empty list is already said by the 503 itself.
    /// </remarks>
    private static string Alternatives(INodeRegistry registry, string capability)
    {
        var models = registry.Snapshot(DateTimeOffset.UtcNow)
            .SelectMany(node => node.Capabilities ?? [])
            .Where(declared => string.Equals(declared.Kind, capability, StringComparison.OrdinalIgnoreCase))
            .SelectMany(declared => declared.Models)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        return models.Length == 0 ? string.Empty : $". Models on this fleet that do: {string.Join(", ", models)}";
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

    /// <summary>
    /// Reads a multipart edit or variation and validates it (phase 50).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ten lines that touch <c>IFormCollection</c> are per host (phase-37 D6) and the sentences
    /// are not: every refusal below comes from <see cref="ImageEditRequest"/> or
    /// <see cref="ToolAttachmentLimits"/> in <c>InferHub.Shared</c>, so a client cannot tell a hub
    /// from a solo node by reading an error.
    /// </para>
    /// <para>
    /// <b>The caller's filename is dropped</b> and the parts travel as <c>image</c> and
    /// <c>mask</c>. What somebody called a file on their disk is metadata about their day
    /// (phase-42 D5) and has no business crossing the mesh.
    /// </para>
    /// </remarks>
    public static async Task<(ImageEditRequest? Request, IResult? Refusal)> ReadEditAsync(
        HttpContext httpContext,
        ImageLimits limits,
        long maxAttachmentBytes,
        string operation,
        CancellationToken cancellationToken)
    {
        if (!httpContext.Request.HasFormContentType)
        {
            return (null, Error(
                400,
                $"this endpoint takes multipart/form-data with an '{ImageEditRequest.ImagePart}' part",
                OpenAiErrorTypes.InvalidRequest));
        }

        IFormCollection form;

        try
        {
            form = await httpContext.Request.ReadFormAsync(cancellationToken);
        }
        catch (BadHttpRequestException ex)
        {
            return (null, Error(400, ex.Message, OpenAiErrorTypes.InvalidRequest));
        }

        var (image, imageRefusal) = await PartAsync(form, ImageEditRequest.ImagePart, maxAttachmentBytes, cancellationToken);

        if (imageRefusal is not null)
        {
            return (null, imageRefusal);
        }

        var (mask, maskRefusal) = await PartAsync(form, ImageEditRequest.MaskPart, maxAttachmentBytes, cancellationToken);

        if (maskRefusal is not null)
        {
            return (null, maskRefusal);
        }

        var total = (image?.Bytes.LongLength ?? 0) + (mask?.Bytes.LongLength ?? 0);

        if (total > limits.MaxRequestBytes)
        {
            return (null, Error(
                413,
                ImageEditRequest.TooLargeRequest(total, limits.MaxRequestBytes),
                OpenAiErrorTypes.InvalidRequest,
                param: ImageEditRequest.ImagePart));
        }

        var request = ImageEditRequest.TryParse(
            operation,
            name => form[name].FirstOrDefault(),
            name => httpContext.Request.Headers.TryGetValue(name, out var values) ? values.ToString() : null,
            image,
            mask,
            limits,
            out var invalid,
            out var invalidParam);

        return request is null
            ? (null, Error(400, invalid, OpenAiErrorTypes.InvalidRequest, param: invalidParam))
            : (request, null);
    }

    private static async Task<(ToolAttachment? Part, IResult? Refusal)> PartAsync(
        IFormCollection form,
        string name,
        long maxAttachmentBytes,
        CancellationToken cancellationToken)
    {
        var file = form.Files[name];

        if (file is null)
        {
            return (null, null);
        }

        if (file.Length > maxAttachmentBytes)
        {
            // Refused before anything is buffered onward, with the limit in the sentence
            // (phase-40 D4). The *role* is named rather than the caller's filename.
            return (null, Error(
                413,
                ToolAttachmentLimits.TooLarge(name, file.Length, maxAttachmentBytes),
                OpenAiErrorTypes.InvalidRequest,
                param: name));
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);

        return (new ToolAttachment(
            name,
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            buffer.ToArray()), null);
    }
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
