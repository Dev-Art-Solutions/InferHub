using System.Runtime.CompilerServices;
using InferHub.Node.Configuration;
using InferHub.Node.Tools;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace InferHub.Node.LocalApi;

/// <summary>
/// Solo mode's upload path (phase 53, D7): the request body copies <em>straight</em> into the
/// scratch file the worker will read, with no hub, no SignalR and no chunk window in between.
/// </summary>
/// <remarks>
/// <para>
/// This is the coordinator's <c>UploadPath</c> / <c>StreamedUpload</c> pair again, and the
/// duplication is phase-37 D6's line rather than an oversight: what a request <em>means</em> is
/// shared — the cap, its sentence and the frame contract all live in <c>InferHub.Shared</c> — while
/// the ASP.NET plumbing that reads a multipart body is per host, because design rule 2 keeps
/// ASP.NET out of the shared library. It is the phase's parity risk, and it is pinned by
/// <c>SoloUploadParityTests</c> driving the same upload at both hosts.
/// </para>
/// <para>
/// Two things are deliberately <b>simpler</b> here, and both follow from there being nothing to
/// route: there is no D5 declaration to check (this node either takes the upload or does not), and
/// there is no D3 ordering requirement, because no decision has to be made before the bytes arrive.
/// A solo node accepts the fields in any order. That is a real difference in what is accepted, and
/// it is one-directional — everything the hub takes, solo takes too.
/// </para>
/// </remarks>
internal static class LocalUploadPath
{
    public static ToolOptions OptionsFrom(HttpContext httpContext) =>
        httpContext.RequestServices.GetService<IOptions<ToolOptions>>()?.Value ?? new ToolOptions();

    public static bool ShouldStream(HttpContext httpContext, ToolOptions options)
    {
        if (options.MaxStreamedBytes <= 0 || !httpContext.Request.HasFormContentType)
        {
            return false;
        }

        var length = httpContext.Request.ContentLength;
        return length is null || length > options.MaxAttachmentBytes;
    }

    public static string? TooLargeUpFront(HttpContext httpContext, ToolOptions options)
    {
        var length = httpContext.Request.ContentLength;

        if (length is null)
        {
            return null;
        }

        var ceiling = Math.Max(options.MaxAttachmentBytes, Math.Max(0, options.MaxStreamedBytes));

        return length > ceiling + LocalUploadLimits.EnvelopeBytes
            ? ToolAttachmentLimits.TooLarge(
                "file",
                length.Value,
                ceiling,
                options.MaxStreamedBytes > 0
                    ? $"{ToolOptions.SectionName}:{nameof(ToolOptions.MaxStreamedBytes)}"
                    : $"{ToolOptions.SectionName}:{nameof(ToolOptions.MaxAttachmentBytes)}")
            : null;
    }

    /// <summary>Raises this request's body ceiling to match the keys, before the body is touched.</summary>
    public static void Prepare(HttpContext httpContext, ToolOptions options)
    {
        var feature = httpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();

        if (feature is null || feature.IsReadOnly)
        {
            return;
        }

        feature.MaxRequestBodySize =
            Math.Max(options.MaxAttachmentBytes, Math.Max(0, options.MaxStreamedBytes)) + LocalUploadLimits.EnvelopeBytes;
    }

    /// <summary>Reads the leading form fields, leaving the body positioned at the first file part.</summary>
    public static async Task<LocalUploadStart> BeginAsync(
        HttpContext httpContext,
        ToolOptions options,
        CancellationToken cancellationToken)
    {
        var boundary = HeaderUtilities.RemoveQuotes(
            MediaTypeHeaderValue.Parse(httpContext.Request.ContentType!).Boundary).Value;

        if (string.IsNullOrWhiteSpace(boundary))
        {
            throw new BadHttpRequestException("the multipart body has no boundary", StatusCodes.Status400BadRequest);
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

        var ceiling = options.MaxStreamedBytes > 0 ? options.MaxStreamedBytes : options.MaxAttachmentBytes;

        return new LocalUploadStart(fields, new LocalStreamedUpload(reader, section, ceiling));
    }
}

internal static class LocalUploadLimits
{
    /// <summary>Part headers, boundaries and the text fields beside the file, generously.</summary>
    public const long EnvelopeBytes = 64 * 1024;
}

internal sealed record LocalUploadStart(
    IReadOnlyDictionary<string, string> Fields,
    LocalStreamedUpload Upload)
{
    public string? Field(string name) => Fields.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// The solo half of <see cref="IStreamedAttachmentSource"/>: the same start / data / end frames the
/// hub produces, made directly from the request body.
/// </summary>
/// <remarks>
/// Going through frames rather than handing <see cref="ToolExecutor"/> a raw stream is the point of
/// D7 — the executor cannot tell which host it is running under, so the scratch file it writes and
/// the <c>ToolFile</c> path the worker reads are identical in a mesh and alone.
/// </remarks>
internal sealed class LocalStreamedUpload(
    MultipartReader reader,
    MultipartSection? firstFileSection,
    long maxBytes) : IStreamedAttachmentSource
{
    private MultipartSection? pending = firstFileSection;
    private int index;
    private long total;

    public bool HasFile => pending is not null || index > 0;

    public long BytesStreamed => total;

    /// <summary>Set when the upload was refused mid-flight; the edge renders it as a 413.</summary>
    public string? TooLarge { get; private set; }

    public async IAsyncEnumerable<AttachmentChunk> ReadAsync(
        Guid jobId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var section = pending;
        pending = null;

        while (section is not null)
        {
            var disposition = ContentDispositionHeaderValue.Parse(section.ContentDisposition);

            // A field after a file is simply read as a field here: with nothing to route, there is
            // no decision that had to be made before the bytes, so there is nothing to refuse.
            if (disposition.IsFileDisposition())
            {
                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                var mediaType = string.IsNullOrWhiteSpace(section.ContentType)
                    ? "application/octet-stream"
                    : section.ContentType;

                yield return AttachmentChunk.Start(
                    index,
                    string.IsNullOrWhiteSpace(name) ? $"file{index}" : name!,
                    mediaType);

                var buffer = new byte[ToolAttachmentLimits.DefaultStreamChunkBytes];

                while (true)
                {
                    var read = await section.Body.ReadAsync(buffer, cancellationToken);

                    if (read == 0)
                    {
                        break;
                    }

                    total += read;

                    if (total > maxBytes)
                    {
                        TooLarge = ToolAttachmentLimits.TooLarge(
                            string.IsNullOrWhiteSpace(name) ? "file" : name!,
                            total,
                            maxBytes,
                            $"{ToolOptions.SectionName}:{nameof(ToolOptions.MaxStreamedBytes)}");

                        yield break;
                    }

                    yield return AttachmentChunk.Data(index, buffer[..read]);
                }

                yield return AttachmentChunk.End(index);
                index++;
            }

            section = await reader.ReadNextSectionAsync(cancellationToken);
        }
    }
}
