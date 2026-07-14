using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Coordinator.Ingestion;
using InferHub.Coordinator.Services;
using Microsoft.AspNetCore.Http;

namespace InferHub.Coordinator.Endpoints;

/// <summary>
/// Document ingestion (phase 23). Deliberately **not** under <c>/api/admin</c>: ingesting is a
/// client action, and requiring an admin key for it would push people toward using one key for
/// everything, which is worse for them than the split it was meant to protect.
/// </summary>
public static class IngestionEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IEndpointRouteBuilder MapIngestionEndpoints(this IEndpointRouteBuilder app)
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
        HttpRequest http,
        IngestionPipeline pipeline,
        CancellationToken cancellationToken)
    {
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

        try
        {
            var result = await pipeline.IngestAsync(collection, request, cancellationToken);

            // A partial ingest is not a success. The chunks that landed are real and visible, and
            // re-posting the same bytes will resume rather than no-op — but the call the caller
            // made did not do what they asked, and the status code says so.
            return result.Status == IngestResult.Partial
                ? Results.Json(result, JsonOptions, statusCode: StatusCodes.Status500InternalServerError)
                : Results.Json(result, JsonOptions);
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
            return Results.Json(new { collection, documents = list }, JsonOptions);
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
                : Results.Json(document, JsonOptions);
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
                    text = ChunkText(c.Payload)
                });

            return Results.Json(new { collection, documentId = id, chunks = ordered }, JsonOptions);
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
                : Results.Json(new { collection, documentId = id, deleted = true, chunks = removed }, JsonOptions);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(StatusCodes.Status404NotFound, ex.Message);
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
        var body = await http.ReadFromJsonAsync<JsonIngestBody>(JsonOptions, cancellationToken)
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
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"'metadata' is not a valid JSON object of strings: {ex.Message}");
        }
    }

    private static string? ChunkText(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } element) return null;
        return element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;
    }

    private static IResult Error(int statusCode, string message) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    private sealed record JsonIngestBody(
        string? Id,
        string? Text,
        string? ContentType,
        string? Source,
        string? Model,
        Dictionary<string, string>? Metadata);
}
