using System.Text.Json.Serialization;

namespace InferHub.Shared.OpenAi;

public sealed record ModelList(
    [property: JsonPropertyName("data")] IReadOnlyList<OpenAiModel> Data)
{
    [JsonPropertyName("object")]
    public string Object => "list";
}

public sealed record OpenAiModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("owned_by")] string OwnedBy,
    /// What the fleet will do with this model (phase 40) — an InferHub extension, additive to the
    /// OpenAI object. Omitted when empty rather than sent as <c>[]</c>: a client that has never
    /// heard of it should see the object it has always seen.
    [property: JsonPropertyName("capabilities")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    IReadOnlyList<string>? Capabilities = null)
{
    [JsonPropertyName("object")]
    public string Object => "model";
}
