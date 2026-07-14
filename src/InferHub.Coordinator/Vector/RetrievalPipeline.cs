using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Ollama;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Vector;

/// <summary>
/// Hub-level RAG pipeline: extract the query text, embed it (on a node), search the
/// collection (node replica if available, hub-local otherwise), and assemble an
/// augmented prompt. The generation itself is still dispatched by the caller — the
/// pipeline returns the rewritten request body and the source ids used, so the
/// endpoint can set them on the response.
///
/// Rule #7 (no conversation content on the coordinator) is preserved: nothing here
/// captures either the original body or the augmented body beyond the in-flight scope
/// of the request. The rewritten JSON is returned to the caller and forgotten.
/// </summary>
public sealed class RetrievalPipeline(
    IOptions<VectorStoreOptions> options,
    IVectorStore store,
    IEmbeddingDispatcher embeddings,
    IVectorQueryRouter queryRouter,
    Metrics metrics,
    ILogger<RetrievalPipeline> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RetrievalOutcome> AugmentChatAsync(
        string rawJson,
        ChatRequest request,
        RetrievalRequest retrieval,
        CancellationToken cancellationToken)
    {
        var queryText = ExtractChatQueryText(request);
        if (string.IsNullOrWhiteSpace(queryText))
        {
            HandleMissing("chat request has no user content to retrieve on");
            return Passthrough(rawJson);
        }

        var matches = await RetrieveAsync(retrieval, queryText, cancellationToken);
        if (matches is null || matches.Count == 0)
        {
            if (matches is { Count: 0 })
            {
                logger.LogInformation("Retrieval for collection {Collection} returned no matches; passing request through unchanged", retrieval.Collection);
            }
            return Passthrough(rawJson);
        }

        var augmentedJson = InjectContextIntoChat(rawJson, matches);
        return new RetrievalOutcome(augmentedJson, matches.Select(ToSource).ToArray(), WasAugmented: true);
    }

    public async Task<RetrievalOutcome> AugmentGenerateAsync(
        string rawJson,
        GenerateRequest request,
        RetrievalRequest retrieval,
        CancellationToken cancellationToken)
    {
        var queryText = request.Prompt;
        if (string.IsNullOrWhiteSpace(queryText))
        {
            HandleMissing("generate request has no 'prompt' to retrieve on");
            return Passthrough(rawJson);
        }

        var matches = await RetrieveAsync(retrieval, queryText, cancellationToken);
        if (matches is null || matches.Count == 0)
        {
            if (matches is { Count: 0 })
            {
                logger.LogInformation("Retrieval for collection {Collection} returned no matches; passing request through unchanged", retrieval.Collection);
            }
            return Passthrough(rawJson);
        }

        var augmentedJson = InjectContextIntoGenerate(rawJson, matches);
        return new RetrievalOutcome(augmentedJson, matches.Select(ToSource).ToArray(), WasAugmented: true);
    }

    private async Task<IReadOnlyList<VectorMatch>?> RetrieveAsync(
        RetrievalRequest retrieval,
        string queryText,
        CancellationToken cancellationToken)
    {
        var opts = options.Value.Retrieval;
        var k = Math.Clamp(retrieval.K ?? opts.DefaultK, 1, opts.MaxRecords);

        float[] vector;
        try
        {
            vector = await embeddings.EmbedSingleAsync(queryText, retrieval.Model, cancellationToken);
        }
        catch (NoEmbeddingNodeException ex)
        {
            HandleMissing(ex.Message);
            return null;
        }

        var query = new VectorQuery(Vector: vector, K: k);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var nodeMatches = await queryRouter.TryQueryOnNodeAsync(retrieval.Collection, query, cancellationToken);
            return nodeMatches ?? await store.QueryAsync(retrieval.Collection, query, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            HandleMissing($"collection '{retrieval.Collection}' does not exist");
            return null;
        }
        finally
        {
            metrics.RecordVectorQuery(retrieval.Collection, Stopwatch.GetElapsedTime(started));
        }
    }

    private void HandleMissing(string reason)
    {
        var mode = options.Value.Retrieval.OnMissing;
        if (string.Equals(mode, "error", StringComparison.OrdinalIgnoreCase))
        {
            throw new RetrievalUnavailableException(reason);
        }
        logger.LogInformation("Retrieval unavailable ({Reason}); passing request through per OnMissing=passthrough", reason);
    }

    private static RetrievalOutcome Passthrough(string rawJson)
        => new(rawJson, Array.Empty<RetrievalSource>(), WasAugmented: false);

    /// <summary>
    /// Lift the chunk's provenance out of its metadata. Chunks written by ingestion carry a
    /// documentId, and PDF chunks carry the page they were lifted from; records upserted straight
    /// into the vector store carry neither, and the citation is simply the id.
    /// </summary>
    private static RetrievalSource ToSource(VectorMatch match)
    {
        if (match.Metadata is not { } metadata)
        {
            return new RetrievalSource(match.Id);
        }

        metadata.TryGetValue("documentId", out var documentId);
        var page = metadata.TryGetValue("page", out var raw) && int.TryParse(raw, out var parsed)
            ? parsed
            : (int?)null;

        return new RetrievalSource(match.Id, documentId, page);
    }

    internal static string? ExtractChatQueryText(ChatRequest request)
    {
        if (request.Messages is null || request.Messages.Count == 0)
        {
            return null;
        }

        for (var i = request.Messages.Count - 1; i >= 0; i--)
        {
            var m = request.Messages[i];
            if (string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Content))
            {
                return m.Content;
            }
        }
        return null;
    }

    internal string BuildContextBlock(IReadOnlyList<VectorMatch> matches)
    {
        var sb = new StringBuilder();
        foreach (var match in matches)
        {
            var text = ExtractMatchText(match);
            sb.Append('[').Append(match.Id).Append("] ").AppendLine(text);
        }
        return options.Value.Retrieval.Template.Replace("{context}", sb.ToString().TrimEnd(), StringComparison.Ordinal);
    }

    internal string InjectContextIntoChat(string rawJson, IReadOnlyList<VectorMatch> matches)
    {
        var block = BuildContextBlock(matches);
        var node = JsonNode.Parse(rawJson)?.AsObject()
            ?? throw new InvalidOperationException("chat request body is not a JSON object");

        var messages = node["messages"]?.AsArray() ?? new JsonArray();
        var systemMessage = new JsonObject
        {
            ["role"] = "system",
            ["content"] = block
        };
        // Insert after any existing leading system message so operator-provided system
        // prompts still land first, but before the first user message.
        var insertAt = 0;
        while (insertAt < messages.Count
               && string.Equals(messages[insertAt]?["role"]?.GetValue<string>(), "system", StringComparison.OrdinalIgnoreCase))
        {
            insertAt++;
        }
        messages.Insert(insertAt, systemMessage);
        node["messages"] = messages;

        return node.ToJsonString(JsonOptions);
    }

    internal string InjectContextIntoGenerate(string rawJson, IReadOnlyList<VectorMatch> matches)
    {
        var block = BuildContextBlock(matches);
        var node = JsonNode.Parse(rawJson)?.AsObject()
            ?? throw new InvalidOperationException("generate request body is not a JSON object");

        var originalPrompt = node["prompt"]?.GetValue<string>() ?? string.Empty;
        node["prompt"] = block + "\n\n" + originalPrompt;
        return node.ToJsonString(JsonOptions);
    }

    private static string ExtractMatchText(VectorMatch match)
    {
        if (match.Payload is { } payload)
        {
            if (payload.ValueKind == JsonValueKind.Object)
            {
                if (payload.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString() ?? string.Empty;
                }
                if (payload.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString() ?? string.Empty;
                }
            }
            else if (payload.ValueKind == JsonValueKind.String)
            {
                return payload.GetString() ?? string.Empty;
            }
            return payload.GetRawText();
        }
        return string.Empty;
    }
}

public sealed record RetrievalRequest(string Collection, int? K, string? Model);

/// <summary>
/// One retrieved chunk, as it appears in the <c>X-InferHub-Sources</c> header. Since v2.5 the
/// header carries objects rather than bare id strings: a chunk id alone identifies the row we
/// retrieved but tells the reader nothing about <em>where it came from</em>, and a citation that
/// cannot name a document and a page is not a citation. <see cref="DocumentId"/> and
/// <see cref="Page"/> are absent for records written directly through <c>/api/vector</c>, which
/// never had a document.
/// </summary>
public sealed record RetrievalSource(
    [property: JsonPropertyName("id")] string Id,
    // Declared on the record rather than left to the caller's serializer options: this record is
    // serialized into a *header*, by two different endpoint files, and a stray "page":null on every
    // citation from a text file is the kind of thing that only one of them would have remembered.
    [property: JsonPropertyName("documentId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DocumentId = null,
    [property: JsonPropertyName("page"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Page = null);

public sealed record RetrievalOutcome(string RawJson, IReadOnlyList<RetrievalSource> Sources, bool WasAugmented);

public sealed class RetrievalUnavailableException(string message) : InvalidOperationException(message);
