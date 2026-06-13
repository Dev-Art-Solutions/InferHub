using System.Text.Json;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;

namespace InferHub.Tests;

public class SmokeTests
{
    [Fact]
    public void OllamaTagsResponseSerializesModelsAsLowercaseProperty()
    {
        var response = new OllamaTagsResponse([new("llama3", "digest-1", 123)]);

        var json = JsonSerializer.Serialize(response);

        Assert.Contains("\"models\":[", json);
        Assert.Contains("\"name\":\"llama3\"", json);
        Assert.Contains("\"digest\":\"digest-1\"", json);
        Assert.Contains("\"size\":123", json);
    }

    [Fact]
    public void NodeModelsSerializesExpectedContractFields()
    {
        var response = new NodeModels(
            "node-1",
            [new ModelInfo("llama3", "digest-1", 123)],
            DateTimeOffset.Parse("2026-06-13T00:00:00Z"));

        var json = JsonSerializer.Serialize(
            response,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.Contains("\"nodeId\":\"node-1\"", json);
        Assert.Contains("\"models\":[", json);
        Assert.Contains("\"refreshedAt\":\"2026-06-13T00:00:00+00:00\"", json);
    }
}
