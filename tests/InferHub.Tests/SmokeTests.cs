using System.Text.Json;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;
using InferHub.Shared.Vector;
using InferHub.Shared.Vector.Replication;

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
    public void ChatRequestRoundTripPreservesEntireMessageHistory()
    {
        const string body = """
        {
          "model": "llama3",
          "messages": [
            {"role":"system","content":"You are helpful."},
            {"role":"user","content":"Hi!"},
            {"role":"assistant","content":"Hello, how can I help?"},
            {"role":"user","content":"Tell me about Sofia."}
          ],
          "stream": false
        }
        """;

        var request = JsonSerializer.Deserialize<ChatRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.Equal("llama3", request!.Model);
        Assert.NotNull(request.Messages);
        Assert.Equal(4, request.Messages!.Count);
        Assert.Equal("system", request.Messages[0].Role);
        Assert.Equal("You are helpful.", request.Messages[0].Content);
        Assert.Equal("user", request.Messages[3].Role);
        Assert.Equal("Tell me about Sofia.", request.Messages[3].Content);
    }

    [Fact]
    public void VectorRecordSerializesAsCamelCase()
    {
        var record = new VectorRecord(
            "doc-1",
            [0.1f, 0.2f],
            Payload: null,
            Metadata: new Dictionary<string, string> { ["tag"] = "alpha" },
            SeqNo: 42,
            TimestampUtc: DateTimeOffset.Parse("2026-06-28T00:00:00Z"));

        var json = JsonSerializer.Serialize(record);

        Assert.Contains("\"id\":\"doc-1\"", json);
        Assert.Contains("\"vector\":[", json);
        Assert.Contains("\"metadata\":{\"tag\":\"alpha\"}", json);
        Assert.Contains("\"seqNo\":42", json);
    }

    [Fact]
    public void VectorUpsertAcceptsTextInsteadOfVector()
    {
        const string body = """
        {
          "id": "doc-1",
          "text": "InferHub adds a memory.",
          "model": "nomic-embed-text"
        }
        """;

        var request = JsonSerializer.Deserialize<VectorUpsert>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.Equal("doc-1", request!.Id);
        Assert.Null(request.Vector);
        Assert.Equal("InferHub adds a memory.", request.Text);
        Assert.Equal("nomic-embed-text", request.Model);
    }

    [Fact]
    public void EmbedRequestSerializesAsOllamaShape()
    {
        var request = new EmbedRequest
        {
            Model = "nomic-embed-text",
            Input = JsonSerializer.SerializeToElement("hello")
        };

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"model\":\"nomic-embed-text\"", json);
        Assert.Contains("\"input\":\"hello\"", json);
        Assert.DoesNotContain("\"truncate\"", json);
    }

    [Fact]
    public void VectorUpsertDeserializesFromMinimalBody()
    {
        const string body = """
        {
          "id": "doc-1",
          "vector": [0.1, 0.2, 0.3]
        }
        """;

        var request = JsonSerializer.Deserialize<VectorUpsert>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.Equal("doc-1", request!.Id);
        Assert.NotNull(request.Vector);
        Assert.Equal(3, request.Vector!.Length);
        Assert.Null(request.Metadata);
        Assert.Null(request.Payload);
    }

    [Fact]
    public void VectorReplicaAssignmentSerializesAsCamelCase()
    {
        var assignment = new VectorReplicaAssignment(
            "docs",
            Dimension: 2,
            Distance: "cosine",
            Records: new[]
            {
                new VectorRecord("a", [1f, 0f], null, null, 1, DateTimeOffset.Parse("2026-06-28T00:00:00Z"))
            },
            LastSeq: 1);

        var json = JsonSerializer.Serialize(assignment);

        Assert.Contains("\"collection\":\"docs\"", json);
        Assert.Contains("\"dimension\":2", json);
        Assert.Contains("\"records\":[", json);
        Assert.Contains("\"lastSeq\":1", json);
    }

    [Fact]
    public void CollectionPlacementSerializesAsCamelCase()
    {
        var placement = new CollectionPlacement("docs", TargetReplicas: 2, LiveReplicas: 1, ReplicaNodes: new[] { "node-a" });

        var json = JsonSerializer.Serialize(placement);

        Assert.Contains("\"collection\":\"docs\"", json);
        Assert.Contains("\"targetReplicas\":2", json);
        Assert.Contains("\"liveReplicas\":1", json);
        Assert.Contains("\"replicaNodes\":[\"node-a\"]", json);
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
