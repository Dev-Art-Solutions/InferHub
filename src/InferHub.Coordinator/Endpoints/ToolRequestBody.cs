using System.Text.Json;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Endpoints;

/// <summary>
/// What a client sent to <c>POST /api/tools/{capability}</c>, in either of the two shapes that
/// matter: a JSON body, or multipart when there are bytes to hand over.
/// </summary>
/// <remarks>
/// <para>
/// The node has its own copy of this under <c>LocalApi/</c>, and that duplication is deliberate —
/// phase-37 D6's line: what a request <em>means</em> is shared (the cap and its sentence live in
/// <see cref="ToolAttachmentLimits"/>, in <c>InferHub.Shared</c>), while the ASP.NET plumbing that
/// reads a form is per host, because design rule 2 keeps ASP.NET out of the shared library. It is
/// the phase's parity risk and it is pinned by a test that drives the same request at both hosts.
/// </para>
/// <para>
/// <b>The bytes are held for the duration of the dispatch and nowhere else</b> — no cache, and no
/// log line containing them (phase-40 D4, design rule 7). "No temp file" needed a correction in
/// phase 53: <c>ReadFormAsync</c> spills a section over 64 KB to an <c>ASPNETCORE_*.tmp</c> file
/// underneath us, which is measured in <see cref="ToolAttachment"/>'s remarks. The streamed path
/// (<see cref="UploadPath"/>) is the one that genuinely never materialises the body.
/// </para>
/// </remarks>
public sealed record ToolRequestBody(
    string? Model,
    string Payload,
    bool Stream,
    IReadOnlyList<ToolAttachment>? Attachments)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<ToolRequestBody> ReadAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        var maxBytes = httpContext.RequestServices.GetService<IOptions<ToolEdgeOptions>>()?.Value.MaxAttachmentBytes
            ?? ToolAttachmentLimits.DefaultMaxBytes;

        return httpContext.Request.HasFormContentType
            ? await ReadMultipartAsync(httpContext, maxBytes, cancellationToken)
            : await ReadJsonAsync(httpContext, cancellationToken);
    }

    private static async Task<ToolRequestBody> ReadJsonAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(httpContext.Request.Body);
        var raw = await reader.ReadToEndAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new BadHttpRequestException("request body is required", StatusCodes.Status400BadRequest);
        }

        JsonElement root;

        try
        {
            root = JsonDocument.Parse(raw).RootElement;
        }
        catch (JsonException ex)
        {
            throw new BadHttpRequestException($"invalid JSON: {ex.Message}", StatusCodes.Status400BadRequest);
        }

        if (root.ValueKind is not JsonValueKind.Object)
        {
            throw new BadHttpRequestException("the request body must be a JSON object", StatusCodes.Status400BadRequest);
        }

        // The body *is* the payload — the node passes it through opaquely, because unlike chat and
        // embeddings there is no second dialect to translate between. `model` and `stream` are read
        // out of it rather than stripped from it: a worker that wants to see the model name it was
        // given should see the request its client actually sent.
        var model = root.TryGetProperty("model", out var m) && m.ValueKind is JsonValueKind.String
            ? m.GetString()
            : null;

        var stream = ReadStream(httpContext, root.TryGetProperty("stream", out var s) ? s : default);

        return new ToolRequestBody(model, raw, stream, null);
    }

    private static async Task<ToolRequestBody> ReadMultipartAsync(
        HttpContext httpContext,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var form = await httpContext.Request.ReadFormAsync(cancellationToken);
        var model = form["model"].FirstOrDefault();
        var payload = form["payload"].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
            }
            catch (JsonException ex)
            {
                throw new BadHttpRequestException(
                    $"the 'payload' field is not valid JSON: {ex.Message}",
                    StatusCodes.Status400BadRequest);
            }
        }
        else
        {
            // Every other form field becomes the payload, so a caller can post
            // `-F model=... -F language=en` without also composing JSON by hand.
            var fields = form
                .Where(field => field.Key is not ("model" or "payload" or "stream"))
                .ToDictionary(field => field.Key, field => (object?)field.Value.FirstOrDefault());

            fields["model"] = model;
            payload = JsonSerializer.Serialize(fields, JsonOptions);
        }

        var attachments = new List<ToolAttachment>();

        foreach (var file in form.Files)
        {
            if (file.Length > maxBytes)
            {
                throw new ToolRequestTooLargeException(
                    ToolAttachmentLimits.TooLarge(file.FileName ?? file.Name, file.Length, maxBytes));
            }

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, cancellationToken);
            attachments.Add(new ToolAttachment(
                file.Name,
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                buffer.ToArray()));
        }

        var streamField = form["stream"].FirstOrDefault();
        var stream = bool.TryParse(streamField, out var parsed)
            ? parsed
            : ReadStreamQuery(httpContext);

        return new ToolRequestBody(model, payload!, stream, attachments.Count == 0 ? null : attachments);
    }

    private static bool ReadStream(HttpContext httpContext, JsonElement stream) =>
        stream.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => ReadStreamQuery(httpContext)
        };

    private static bool ReadStreamQuery(HttpContext httpContext) =>
        bool.TryParse(httpContext.Request.Query["stream"].FirstOrDefault(), out var fromQuery) && fromQuery;
}

/// <summary>An attachment over <c>Tools:MaxAttachmentBytes</c>. Rendered as a 413 at the edge.</summary>
public sealed class ToolRequestTooLargeException(string message) : InvalidOperationException(message);

/// <summary>
/// The hub's half of the <c>Tools</c> section: what it will accept on the way in. The node has its
/// own <c>Tools</c> section for what it will run — the two processes are configured separately on
/// purpose, because the box that accepts a 25 MB upload is not the box that has to write it to
/// disk.
/// </summary>
public sealed class ToolEdgeOptions
{
    public const string SectionName = "Tools";

    public long MaxAttachmentBytes { get; set; } = ToolAttachmentLimits.DefaultMaxBytes;

    /// <summary>
    /// The ceiling for an upload that streams *through* the hub (phase 53). <b>Zero is off</b>, and
    /// off is the default: a deployment that changes no config behaves byte for byte as v3.20 did,
    /// including the 413 it produces and the key named in it.
    /// </summary>
    public long MaxStreamedBytes { get; set; }

    /// <summary>How much of a streamed attachment travels in one frame. See phase-53 D1.</summary>
    public int StreamChunkBytes { get; set; } = ToolAttachmentLimits.DefaultStreamChunkBytes;
}
