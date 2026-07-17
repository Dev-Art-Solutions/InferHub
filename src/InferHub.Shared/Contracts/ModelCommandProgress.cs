using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// A node → hub progress frame for a <see cref="ModelCommand"/> (phase 26). A pull streams many of
/// these as bytes arrive; delete and warm produce a start frame and a terminal one. The final frame
/// of any command sets <see cref="Done"/> — with <see cref="Error"/> populated iff it failed.
/// </summary>
public sealed record ModelCommandProgress(
    [property: JsonPropertyName("commandId")] Guid CommandId,
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("modelName")] string ModelName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("percent")] double? Percent,
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("error")] string? Error);
