using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;
using InferHub.Shared.OpenAi;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InferHub.Node.LocalApi;

/// <summary>
/// Reading a multipart edit on a solo node (phase 50) — the node's ten lines of ASP.NET.
/// </summary>
/// <remarks>
/// <para>
/// This is the coordinator's <c>ImageEndpointSupport.ReadEditAsync</c> with the fleet removed, and
/// it is <b>hand-copied on purpose</b>: phase-37 D6's line is that the frame <em>bodies</em> are
/// shared and the plumbing that reads a request and writes a response is per host. Every sentence a
/// caller can read comes from <see cref="ImageEditRequest"/> and <see cref="ToolAttachmentLimits"/>
/// in <c>InferHub.Shared</c>, so the two hosts cannot refuse differently — and
/// <c>ImageParityTests</c> drives both over real Kestrel to prove they do not.
/// </para>
/// <para>
/// It is also why <see cref="ImageEditRequest.TryParse"/> takes delegates rather than an
/// <c>IFormCollection</c>: design rule 2 keeps ASP.NET out of <c>InferHub.Shared</c>, and this is
/// the shape that lets the validation live there anyway.
/// </para>
/// </remarks>
internal static class LocalImageForm
{
    public static ImageLimits Limits(HttpContext httpContext)
    {
        var images = httpContext.RequestServices.GetService<IOptions<ImageEdgeOptions>>()?.Value ?? new ImageEdgeOptions();

        return images.Resolve(AttachmentCap(httpContext));
    }

    public static long AttachmentCap(HttpContext httpContext) =>
        httpContext.RequestServices.GetService<IOptions<ToolOptions>>()?.Value.MaxAttachmentBytes
        ?? ToolAttachmentLimits.DefaultMaxBytes;

    public static async Task<(ImageEditRequest? Request, IResult? Refusal)> ReadEditAsync(
        HttpContext httpContext,
        string operation,
        CancellationToken cancellationToken)
    {
        var limits = Limits(httpContext);
        var maxAttachmentBytes = AttachmentCap(httpContext);

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

    private static IResult Error(int status, string message, string type, string? param = null, string? code = null)
        => Results.Json(
            OpenAiErrorEnvelope.Create(message, type, code, param),
            LocalApiEndpoints.JsonOptions,
            statusCode: status);
}
