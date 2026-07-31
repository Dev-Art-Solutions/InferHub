using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// A partial answer from a tool worker, uploaded to the hub on the same client-to-server stream
/// shape <see cref="InferenceChunk"/> uses.
/// </summary>
/// <remarks>
/// The hub method that receives these <b>must not declare a <c>CancellationToken</c> parameter</b>
/// — see the comment on <c>NodeHub.StreamChunks</c>. This is the third method to hit that trap.
/// </remarks>
public sealed record ToolChunk(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("done")] bool Done);
