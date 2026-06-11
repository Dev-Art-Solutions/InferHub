using System.Text.Json;
using InferHub.Shared.Ollama;

namespace InferHub.Tests;

public class SmokeTests
{
    [Fact]
    public void OllamaTagsResponseSerializesModelsAsLowercaseProperty()
    {
        var response = new OllamaTagsResponse([]);

        var json = JsonSerializer.Serialize(response);

        Assert.Contains("\"models\":[]", json);
    }
}
