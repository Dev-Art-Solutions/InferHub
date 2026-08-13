using System.Runtime.CompilerServices;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace InferHub.Coordinator.Endpoints;

/// <summary>Why a streamed upload stopped early. Never "it failed" — the edge renders each.</summary>
public enum UploadFailureKind
{
    /// <summary>Past <c>Tools:MaxStreamedBytes</c>, counted as it arrived. Rendered as a 413.</summary>
    TooLarge,

    /// <summary>A form field arrived after a file part (phase-53 D3). Rendered as a 400.</summary>
    FieldAfterFile,

    /// <summary>The client stopped sending. Nothing to render — nobody is listening.</summary>
    ClientAborted
}

public sealed record UploadFailure(UploadFailureKind Kind, string Message);

/// <summary>
/// A multipart body being read <em>as the node consumes it</em> (phase 53, D1).
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of what "streaming through the hub" means: the sections are pulled off the
/// live request body by <see cref="ReadAsync"/>, which is driven by the node's
/// <c>StreamAttachments</c> invocation on the connection it already opened (phase-26 D1 — the hub
/// still never dials a node). The hub holds one <see cref="chunkBytes"/> buffer and SignalR's own
/// bounded stream channel, so its memory is a fixed number that does not grow with the upload, and
/// backpressure reaches the client's TCP window for free.
/// </para>
/// <para>
/// <b>Sections are consumed in order and exactly once</b>, because a multipart body is a stream and
/// not a collection. That is why the frames describe the body as it arrives (start / data / end)
/// rather than the job carrying a list of attachment references: how many parts there are, and what
/// they are called, is not knowable until the body has been read.
/// </para>
/// <para>
/// <b>Nothing here touches the disk.</b> That is the phase's whole point — see
/// <see cref="ToolAttachment"/>'s remarks for what the buffered path does underneath, which was
/// measured rather than assumed.
/// </para>
/// </remarks>
public sealed class StreamedUpload(
    MultipartReader reader,
    MultipartSection? firstFileSection,
    long maxBytes,
    int chunkBytes)
{
    private MultipartSection? pending = firstFileSection;
    private int index;
    private long total;


    /// <summary>Set when the enumeration stopped early. The edge reads it after the dispatch.</summary>
    public UploadFailure? Failure { get; private set; }

    /// <summary>How many bytes actually crossed. For the log line, which never carries content.</summary>
    public long BytesStreamed => total;

    /// <summary>Whether the body carried a file part at all.</summary>
    public bool HasFile => pending is not null || index > 0;

    public async IAsyncEnumerable<AttachmentChunk> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The token is SignalR's, tied to the node's stream. That is enough on its own: a client
        // that walks away cancels the endpoint, which cancels the job, which ends this enumeration
        // through the node — see Dispatcher.UploadRegistration for why there is no second mechanism.
        var token = cancellationToken;

        var section = pending;
        pending = null;

        while (section is not null)
        {
            var disposition = ContentDispositionHeaderValue.Parse(section.ContentDisposition);

            if (!disposition.IsFileDisposition())
            {
                // D3. A field after a file is refused rather than dropped: a transcription that
                // ignored `language=bg` and answered in English is the phase-42 failure with no
                // error in it.
                Failure = new UploadFailure(
                    UploadFailureKind.FieldAfterFile,
                    $"form field '{HeaderUtilities.RemoveQuotes(disposition.Name).Value}' arrived after a file part; " +
                    "on the streamed path every field must precede the file, because the request is " +
                    "routed before the bytes are read");

                yield break;
            }

            var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
            var mediaType = string.IsNullOrWhiteSpace(section.ContentType)
                ? "application/octet-stream"
                : section.ContentType;

            // The *part* name, never the caller's filename (phase-42 D5, phase-50).
            yield return AttachmentChunk.Start(index, string.IsNullOrWhiteSpace(name) ? $"file{index}" : name!, mediaType);

            var buffer = new byte[chunkBytes];

            while (true)
            {
                int read;

                try
                {
                    read = await section.Body.ReadAsync(buffer, token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    // The client went away mid-upload. There is nobody to render a status to; the
                    // node sees the enumeration end and fails the job, which is the honest outcome.
                    Failure = new UploadFailure(UploadFailureKind.ClientAborted, "the client stopped sending");
                    yield break;
                }

                if (read == 0)
                {
                    break;
                }

                total += read;

                if (total > maxBytes)
                {
                    // Counted as it arrives, because a body sent with Transfer-Encoding: chunked
                    // never declared a length to check up front. The response status is
                    // best-effort past this point and the docs say so rather than pretending.
                    Failure = new UploadFailure(
                        UploadFailureKind.TooLarge,
                        ToolAttachmentLimits.TooLarge(
                            string.IsNullOrWhiteSpace(name) ? "file" : name!,
                            total,
                            maxBytes,
                            ToolAttachmentLimits.MaxStreamedBytesKey));

                    yield break;
                }

                yield return AttachmentChunk.Data(index, buffer[..read]);
            }

            yield return AttachmentChunk.End(index);
            index++;

            section = await reader.ReadNextSectionAsync(token);
        }
    }
}
