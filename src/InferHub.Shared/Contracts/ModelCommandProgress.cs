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
    [property: JsonPropertyName("error")] string? Error,
    /// <summary>
    /// Which tool's models this was about (phase 48), or null for the node's inference backend.
    /// </summary>
    /// <remarks>
    /// It rides along so the coalescing key can tell an Ollama model from a diffusion recipe that
    /// happens to share a name, and so the console can say which of the two a progress bar is
    /// filling for. Appended with a null default, so a v3.15 hub reading a v3.16 node's frame — and
    /// the reverse — both get exactly what they meant.
    /// </remarks>
    [property: JsonPropertyName("tool")] string? Tool = null);
