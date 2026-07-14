using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Shared.Vector;

public sealed record VectorRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("vector")] float[] Vector,
    [property: JsonPropertyName("payload")] JsonElement? Payload,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata,
    [property: JsonPropertyName("seqNo")] long SeqNo,
    [property: JsonPropertyName("timestampUtc")] DateTimeOffset TimestampUtc);

/// <summary>
/// A record without its embedding. What a scan returns: listing documents and previewing chunks
/// needs ids, payloads and metadata, and pulling a few thousand 768-float vectors off disk (or
/// across a Postgres connection) to throw them away would be a waste with no upside. The absent
/// vector is a distinct type rather than an empty <c>float[]</c> precisely so nobody can mistake
/// "not fetched" for "not there".
/// </summary>
public sealed record VectorEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("payload")] JsonElement? Payload,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata,
    [property: JsonPropertyName("seqNo")] long SeqNo,
    [property: JsonPropertyName("timestampUtc")] DateTimeOffset TimestampUtc);

public sealed record VectorUpsert(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("vector")] float[]? Vector = null,
    [property: JsonPropertyName("payload")] JsonElement? Payload = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("model")] string? Model = null);
