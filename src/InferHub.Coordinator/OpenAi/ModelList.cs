using System.Text.Json.Serialization;

namespace InferHub.Coordinator.OpenAi;

public sealed record ModelList(
    [property: JsonPropertyName("data")] IReadOnlyList<OpenAiModel> Data)
{
    [JsonPropertyName("object")]
    public string Object => "list";
}

public sealed record OpenAiModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("owned_by")] string OwnedBy)
{
    [JsonPropertyName("object")]
    public string Object => "model";
}
