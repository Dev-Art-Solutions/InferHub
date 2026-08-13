using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Http.Features;

namespace InferHub.Coordinator.Endpoints;

/// <summary>
/// One key moves every ceiling an upload meets (phase 53, D6).
/// </summary>
/// <remarks>
/// <para>
/// <b>There are three limits between a client and a worker, and only one of them is ours.</b>
/// Measured on .NET 10 rather than read off the documentation:
/// </para>
/// <list type="bullet">
/// <item><c>Tools:MaxAttachmentBytes</c> — 25 MB, ours, enforced at both edges.</item>
/// <item>Kestrel's <c>MaxRequestBodySize</c> — <b>30 000 000 bytes</b>, which today's 25 MiB cap
/// clears by about 3.7 MB. Over it, Kestrel answers 413 before a handler runs, and none of our
/// sentence is in it.</item>
/// <item><c>FormOptions.MultipartBodyLengthLimit</c> — 134 217 728 bytes, per multipart body.</item>
/// </list>
/// <para>
/// So they are <b>derived</b> here rather than configured separately, for
/// <see cref="Hubs.NodeHubLimits"/>'s reason in its own words: two numbers that have to agree are
/// two numbers that will not. An operator who raises the attachment cap and then meets a 413 with
/// no key named in it has been handed a puzzle by a design that knew the answer.
/// </para>
/// <para>
/// <b>Applied per route, never globally.</b> A global raise would also un-bound <c>/api/chat</c>,
/// <c>/v1/embeddings</c> and the vector data plane, which have no business accepting a 300 MB body
/// and are protected today only by a default nobody chose deliberately.
/// </para>
/// </remarks>
public static class UploadLimits
{
    /// <summary>
    /// Part headers, boundaries and the small text fields beside the file, generously. The same
    /// shape as <see cref="Hubs.NodeHubLimits"/>'s envelope, and for the same reason: the number
    /// that matters is the payload, and the framing must not be able to push a legitimate request
    /// over the line.
    /// </summary>
    public const long EnvelopeBytes = 64 * 1024;

    /// <summary>What a request body may be, given the two caps the operator set.</summary>
    public static long RequestBodyLimitFor(long maxAttachmentBytes, long maxStreamedBytes)
    {
        var payload = Math.Max(
            maxAttachmentBytes > 0 ? maxAttachmentBytes : ToolAttachmentLimits.DefaultMaxBytes,
            Math.Max(0, maxStreamedBytes));

        return payload + EnvelopeBytes;
    }

    /// <summary>
    /// Raises this request's body ceiling to match the keys. Silently does nothing when the server
    /// does not expose the feature (TestServer) or the body has already started — both are cases
    /// where the limit is somebody else's to enforce, and throwing would turn a working deployment
    /// into a 500.
    /// </summary>
    public static void Apply(HttpContext httpContext, long maxAttachmentBytes, long maxStreamedBytes)
    {
        var feature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

        if (feature is null || feature.IsReadOnly)
        {
            return;
        }

        feature.MaxRequestBodySize = RequestBodyLimitFor(maxAttachmentBytes, maxStreamedBytes);
    }

    /// <summary>
    /// The multipart limit for the <em>buffered</em> path, where <c>ReadFormAsync</c> is what
    /// enforces it. The streamed path never calls it — it reads sections itself and counts as it
    /// goes, which is the only way to bound a body whose length the client never declared.
    /// </summary>
    public static FormOptions FormOptionsFor(long maxAttachmentBytes, long maxStreamedBytes) => new()
    {
        MultipartBodyLengthLimit = RequestBodyLimitFor(maxAttachmentBytes, maxStreamedBytes)
    };

    /// <summary>
    /// Both derivations, applied to one request, on the routes that take an upload. Call it before
    /// touching the body — Kestrel's ceiling is read-only once the read has started.
    /// </summary>
    public static void Prepare(HttpContext httpContext, long maxAttachmentBytes, long maxStreamedBytes)
    {
        Apply(httpContext, maxAttachmentBytes, maxStreamedBytes);

        if (httpContext.Request.HasFormContentType)
        {
            httpContext.Features.Set<IFormFeature>(new FormFeature(
                httpContext.Request,
                FormOptionsFor(maxAttachmentBytes, maxStreamedBytes)));
        }
    }
}
