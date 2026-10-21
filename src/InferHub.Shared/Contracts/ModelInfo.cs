using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

public sealed record ModelInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("digest")] string? Digest,
    [property: JsonPropertyName("size")] long? SizeBytes,
    /// Phase 109. How many requests for this model its engine runs at once, <b>as the engine itself
    /// answered</b> — a loaded llama.cpp model's <c>total_slots</c>. Null (and absent on the wire) for
    /// every engine that cannot be asked; the hub then falls back to the node's
    /// <see cref="NodeRegistration.ModelSlots"/>. A per-node fact: the hub never shows it in a fleet list.
    [property: JsonPropertyName("slots"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Slots = null);
