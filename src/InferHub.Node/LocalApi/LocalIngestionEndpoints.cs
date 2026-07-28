using System.Text;
using System.Text.Json;
using InferHub.Shared.Ingestion;
using InferHub.Shared.Vector;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace InferHub.Node.LocalApi;

/// <summary>
/// Document ingestion on a standalone node (phase 38). The hub's routes, bodies and statuses,
/// over the same <see cref="IngestionPipeline"/> — it moved to <c>InferHub.Shared</c> rather than
/// being reimplemented, so the chunking, the deterministic ids, the stale-chunk sweep and the
/// <c>partial</c> verdict are one definition, not two (D2).
/// </summary>
/// <remarks>
/// One difference, and it is loud: <b>PDF is a 415 here</b> (D5). <c>PdfPig</c> is scoped to the
/// coordinator by name (rule 5) and the node registers no <c>IPdfTextExtractor</c>, so the seam
/// refuses rather than degrading. Phase-23 D4 refused OCR because a bad extraction *succeeds
/// quietly* and fills a corpus with plausible nonsense; a PDF silently ingested as its raw bytes
/// would be the same failure with fewer excuses.
/// </remarks>
internal static class LocalIngestionEndpoints
{
    internal const string PdfRefusal =
        "PDF ingestion is not available on a standalone node: the PDF text extractor ships with the coordinator only. Convert the document to text or Markdown first, or ingest it into a hub.";

    public static IEndpointRouteBuilder MapLocalIngestionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/collections/{collection}/documents");

        group.MapPost("/", IngestAsync).DisableAntiforgery();
        group.MapGet("/", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapGet("/{id}/chunks", ChunksAsync);
        group.MapDelete("/{id}", DeleteAsync);

        return app;
    }

    private static async Task<IResult> IngestAsync(
        string collection,
        HttpContext context,
        IngestionPipeline pipeline,
        CancellationToken cancellationToken)
    {
        var http = context.Request;

        IngestRequest request;
        try
        {
            request = http.HasFormContentType
                ? await ReadMultipartAsync(http, cancellationToken)
                : await ReadJsonAsync(http, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return Error(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (JsonException ex)
        {
            return Error(StatusCodes.Status400BadRequest, $"request body is not valid JSON: {ex.Message}");
        }

        // A PDF is refused before any work, with a message that names the limitation rather than
        // the missing service. The pipeline would refuse too — TextExtractor throws with no
        // IPdfTextExtractor registered — but "PDF extraction is not available in this build" tells
        // an operator nothing about what to do next.
        if (IsPdf(request))
        {
            return Error(StatusCodes.Status415UnsupportedMediaType, PdfRefusal);
        }

        try
        {
            // Auto-provision is on: a node's own config is its provisioning grant, the same
            // reasoning phase-31 D5 applied to a client's collection scope. The dimension is still
            // measured from the first embedded batch, never guessed.
            var result = await pipeline.IngestAsync(collection, request, autoProvision: true, cancellationToken);

            // A partial ingest is not a success. The chunks that landed are real and re-posting the
            // same bytes resumes rather than no-ops — but the call did not do what was asked.
            return result.Status == IngestResult.Partial
                ? Results.Json(result, LocalApiEndpoints.JsonOptions, statusCode: StatusCodes.Status500InternalServerError)
                : Results.Json(result, LocalApiEndpoints.JsonOptions);
        }
        catch (DocumentTooLargeException ex)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, ex.Message);
        }
        catch (UnsupportedMediaTypeException ex)
        {
            return Error(StatusCodes.Status415UnsupportedMediaType, ex.Message);
        }
        catch (ExtractionFailedException ex)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(StatusCodes.Status404NotFound, ex.Message);
        }
        catch (NoEmbeddingNodeException ex)
        {
            return Error(StatusCodes.Status404NotFound, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Error(StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    private static async Task<IResult> ListAsync(
        string collection,
        DocumentIndex documents,
        CancellationToken cancellationToken)
    {
        try
        {
            var list = await documents.ListAsync(collection, cancellationToken);
            return Results.Json(new { collection, documents = list }, LocalApiEndpoints.JsonOptions);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(StatusCodes.Status404NotFound, ex.Message);
        }
    }

    private static async Task<IResult> GetAsync(
        string collection,
        string id,
        DocumentIndex documents,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = await documents.GetAsync(collection, id, cancellationToken);
            return document is null
                ? Error(StatusCodes.Status404NotFound, $"document '{id}' not found in '{collection}'")
                : Results.Json(document, LocalApiEndpoints.JsonOptions);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(StatusCodes.Status404NotFound, ex.Message);
        }
    }

    private static async Task<IResult> ChunksAsync(
        string collection,
        string id,
        DocumentIndex documents,
        CancellationToken cancellationToken)
    {
        try
        {
            var chunks = await documents.ChunksOfAsync(collection, id, cancellationToken);
            if (chunks.Count == 0)
            {
                return Error(StatusCodes.Status404NotFound, $"document '{id}' not found in '{collection}'");
            }

            var ordered = chunks
                .OrderBy(c => int.TryParse(DocumentIndex.Meta(c, ChunkMetadata.ChunkIndex), out var i) ? i : int.MaxValue)
                .Select(c => new
                {
                    id = c.Id,
                    index = DocumentIndex.Meta(c, ChunkMetadata.ChunkIndex),
                    page = DocumentIndex.Meta(c, ChunkMetadata.Page),
                    text = ChunkText.Extract(c.Payload)
                });

            return Results.Json(new { collection, documentId = id, chunks = ordered }, LocalApiEndpoints.JsonOptions);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(StatusCodes.Status404NotFound, ex.Message);
        }
    }

    private static async Task<IResult> DeleteAsync(
        string collection,
        string id,
        DocumentIndex documents,
        CancellationToken cancellationToken)
    {
        try
        {
            var removed = await documents.DeleteAsync(collection, id, cancellationToken);
            return removed == 0
                ? Error(StatusCodes.Status404NotFound, $"document '{id}' not found in '{collection}'")
                : Results.Json(new { collection, documentId = id, deleted = true, chunks = removed }, LocalApiEndpoints.JsonOptions);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(StatusCodes.Status404NotFound, ex.Message);
        }
    }

    private static bool IsPdf(IngestRequest request)
    {
        try
        {
            return TextExtractor.ResolveMediaType(request.ContentType, request.FileName) == TextExtractor.Pdf;
        }
        catch (UnsupportedMediaTypeException)
        {
            // Not a format we read at all; the pipeline's own message is the better one.
            return false;
        }
    }

    private static async Task<IngestRequest> ReadMultipartAsync(HttpRequest http, CancellationToken cancellationToken)
    {
        var form = await http.ReadFormAsync(cancellationToken);
        var file = form.Files["file"] ?? form.Files.FirstOrDefault()
            ?? throw new ArgumentException("multipart upload must carry a 'file' part");

        using var buffer = new MemoryStream();
        await using (var stream = file.OpenReadStream())
        {
            await stream.CopyToAsync(buffer, cancellationToken);
        }

        return new IngestRequest(
            Content: buffer.ToArray(),
            DocumentId: form["id"].FirstOrDefault(),
            ContentType: file.ContentType,
            FileName: file.FileName,
            Metadata: ParseMetadata(form["metadata"].FirstOrDefault()),
            EmbeddingModel: form["model"].FirstOrDefault());
    }

    private static async Task<IngestRequest> ReadJsonAsync(HttpRequest http, CancellationToken cancellationToken)
    {
        var body = await http.ReadFromJsonAsync<JsonIngestBody>(LocalApiEndpoints.JsonOptions, cancellationToken)
            ?? throw new ArgumentException("request body is empty");

        if (string.IsNullOrWhiteSpace(body.Text))
        {
            throw new ArgumentException("'text' is required when posting JSON");
        }

        return new IngestRequest(
            Content: Encoding.UTF8.GetBytes(body.Text),
            DocumentId: body.Id,
            ContentType: body.ContentType ?? TextExtractor.PlainText,
            FileName: body.Source,
            Metadata: body.Metadata,
            EmbeddingModel: body.Model);
    }

    private static IReadOnlyDictionary<string, string>? ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, LocalApiEndpoints.JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"'metadata' is not a valid JSON object of strings: {ex.Message}");
        }
    }

    private static IResult Error(int statusCode, string message)
        => Results.Json(new { error = message }, LocalApiEndpoints.JsonOptions, statusCode: statusCode);

    private sealed record JsonIngestBody(
        string? Id,
        string? Text,
        string? ContentType,
        string? Source,
        string? Model,
        Dictionary<string, string>? Metadata);
}
