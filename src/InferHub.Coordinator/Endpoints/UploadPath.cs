using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace InferHub.Coordinator.Endpoints;

/// <summary>What the leading form fields said, and the body still waiting behind them.</summary>
public sealed record StreamedUploadStart(
    IReadOnlyDictionary<string, string> Fields,
    StreamedUpload Upload)
{
    public string? Field(string name) => Fields.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// Which of the two upload paths a request takes, and how the streamed one starts (phase 53, D2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Size chooses, and the small path is byte-identical to v3.20.</b> At or under
/// <c>Tools:MaxAttachmentBytes</c> a request is read exactly as it was — buffered, with failover,
/// with no ordering requirement and routable to any node in the fleet. Only above it does the
/// streamed path apply, with the constraints it brings (D3's ordering, D4's lost failover, D5's
/// capable node). Streaming everything would have made every ordinary 25 MB request pay those to
/// fix a problem it does not have.
/// </para>
/// <para>
/// With <c>Tools:MaxStreamedBytes</c> at its default of <c>0</c> there is no second path at all,
/// and the 413 a large upload gets is the one v3.20 produced, from the same key, in the same words.
/// </para>
/// <para>
/// <b>A body with no <c>Content-Length</c> takes the streamed path when it is enabled.</b> The
/// alternative is buffering a body whose size nobody declared in order to find out whether it was
/// allowed to be buffered, which is the thing this phase removes.
/// </para>
/// </remarks>
public static class UploadPath
{
    public static ToolEdgeOptions OptionsFrom(HttpContext httpContext) =>
        httpContext.RequestServices.GetService<IOptions<ToolEdgeOptions>>()?.Value ?? new ToolEdgeOptions();

    public static bool ShouldStream(HttpContext httpContext, ToolEdgeOptions options)
    {
        if (options.MaxStreamedBytes <= 0 || !httpContext.Request.HasFormContentType)
        {
            return false;
        }

        var length = httpContext.Request.ContentLength;
        return length is null || length > options.MaxAttachmentBytes;
    }

    /// <summary>
    /// The refusal a request earns before a byte is read, when it declared a length nothing will
    /// accept. Null when there is no reason to refuse yet — a body with no declared length is
    /// counted as it arrives instead (<see cref="StreamedUpload"/>).
    /// </summary>
    public static string? TooLargeUpFront(HttpContext httpContext, ToolEdgeOptions options)
    {
        var length = httpContext.Request.ContentLength;

        if (length is null)
        {
            return null;
        }

        var ceiling = Math.Max(options.MaxAttachmentBytes, Math.Max(0, options.MaxStreamedBytes));

        return length > ceiling + UploadLimits.EnvelopeBytes
            ? ToolAttachmentLimits.TooLarge(
                "file",
                length.Value,
                ceiling,
                options.MaxStreamedBytes > 0
                    ? ToolAttachmentLimits.MaxStreamedBytesKey
                    : ToolAttachmentLimits.MaxAttachmentBytesKey)
            : null;
    }

    /// <summary>
    /// Reads the multipart body up to the first file part, so the request can be validated,
    /// admitted and routed before any of the bytes arrive — which is what D3's ordering
    /// requirement exists to make possible.
    /// </summary>
    public static async Task<StreamedUploadStart> BeginAsync(
        HttpContext httpContext,
        ToolEdgeOptions options,
        CancellationToken cancellationToken)
    {
        var boundary = HeaderUtilities.RemoveQuotes(
            MediaTypeHeaderValue.Parse(httpContext.Request.ContentType!).Boundary).Value;

        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw new BadHttpRequestException(
                "the multipart body has no boundary",
                StatusCodes.Status400BadRequest);
        }

        var reader = new MultipartReader(boundary!, httpContext.Request.Body);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = await reader.ReadNextSectionAsync(cancellationToken);

        while (section is not null)
        {
            var disposition = ContentDispositionHeaderValue.Parse(section.ContentDisposition);

            if (disposition.IsFileDisposition())
            {
                break;
            }

            var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;

            if (!string.IsNullOrWhiteSpace(name))
            {
                using var streamReader = new StreamReader(section.Body);
                fields[name!] = await streamReader.ReadToEndAsync(cancellationToken);
            }

            section = await reader.ReadNextSectionAsync(cancellationToken);
        }

        var chunkBytes = options.StreamChunkBytes > 0
            ? options.StreamChunkBytes
            : ToolAttachmentLimits.DefaultStreamChunkBytes;

        return new StreamedUploadStart(
            fields,
            new StreamedUpload(reader, section, options.MaxStreamedBytes, chunkBytes));
    }
}
