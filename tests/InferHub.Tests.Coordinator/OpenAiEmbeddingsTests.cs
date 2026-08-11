using System.Buffers.Binary;
using System.Text.Json;
using InferHub.Coordinator.OpenAi;
using InferHub.Shared.Ollama;

namespace InferHub.Tests;

public class OpenAiEmbeddingsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static OpenAiEmbeddingsRequest Request(string json)
        => JsonSerializer.Deserialize<OpenAiEmbeddingsRequest>(json, JsonOptions)!;

    private static EmbedResponse EmbedResponse(params float[][] vectors)
        => new()
        {
            Model = "nomic-embed-text",
            Embeddings = vectors.Select(v => v.ToList()).ToList(),
            PromptEvalCount = 9
        };

    [Fact]
    public void SingleStringInputRidesThroughAsAString()
    {
        var ollama = RequestTranslator.ToOllamaEmbed(Request("""
        {"model":"nomic-embed-text","input":"hello"}
        """));

        var body = JsonDocument.Parse(ollama).RootElement;
        Assert.Equal("nomic-embed-text", body.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.String, body.GetProperty("input").ValueKind);
        Assert.Equal("hello", body.GetProperty("input").GetString());
    }

    [Fact]
    public void ArrayInputRidesThroughAsAnArray()
    {
        var ollama = RequestTranslator.ToOllamaEmbed(Request("""
        {"model":"nomic-embed-text","input":["a","b"]}
        """));

        var input = JsonDocument.Parse(ollama).RootElement.GetProperty("input");
        Assert.Equal(JsonValueKind.Array, input.ValueKind);
        Assert.Equal(2, input.GetArrayLength());
        Assert.Equal("a", input[0].GetString());
    }

    [Fact]
    public void TokenArrayInputIsRejected()
    {
        // We have no tokenizer at the edge; embedding the wrong text silently would be worse
        // than an error.
        var ex = Assert.Throws<OpenAiRequestException>(() => RequestTranslator.ToOllamaEmbed(Request("""
        {"model":"nomic-embed-text","input":[1212,318,257]}
        """)));

        Assert.Equal("input", ex.Param);
        Assert.Contains("token arrays are not supported", ex.Message);
    }

    [Fact]
    public void MissingInputIsRejected()
    {
        var ex = Assert.Throws<OpenAiRequestException>(() => RequestTranslator.ToOllamaEmbed(Request("""
        {"model":"nomic-embed-text"}
        """)));

        Assert.Equal("input", ex.Param);
    }

    [Fact]
    public void FloatFormatReturnsRawVectors()
    {
        var response = ResponseTranslator.ToEmbeddings(
            EmbedResponse([0.5f, -0.25f]),
            "nomic-embed-text",
            base64: false);

        Assert.Equal("list", response.Object);

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var data = JsonDocument.Parse(json).RootElement.GetProperty("data");

        var embedding = data[0].GetProperty("embedding");
        Assert.Equal(JsonValueKind.Array, embedding.ValueKind);
        Assert.Equal(0.5f, embedding[0].GetSingle());
        Assert.Equal(-0.25f, embedding[1].GetSingle());
        Assert.Equal("embedding", data[0].GetProperty("object").GetString());
        Assert.Equal(0, data[0].GetProperty("index").GetInt32());
    }

    [Fact]
    public void Base64FormatReturnsLittleEndianFloat32()
    {
        // The OpenAI Python SDK asks for base64 by default and decodes it with numpy —
        // getting the byte order wrong here produces silently garbage vectors.
        var vector = new[] { 0.5f, -0.25f };

        var response = ResponseTranslator.ToEmbeddings(
            EmbedResponse(vector),
            "nomic-embed-text",
            base64: true);

        var json = JsonSerializer.Serialize(response, JsonOptions);
        var embedding = JsonDocument.Parse(json).RootElement
            .GetProperty("data")[0]
            .GetProperty("embedding");

        Assert.Equal(JsonValueKind.String, embedding.ValueKind);

        var bytes = Convert.FromBase64String(embedding.GetString()!);
        Assert.Equal(vector.Length * sizeof(float), bytes.Length);

        for (var i = 0; i < vector.Length; i++)
        {
            var decoded = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
            Assert.Equal(vector[i], decoded);
        }
    }

    [Fact]
    public void MultipleVectorsAreIndexedInOrder()
    {
        var response = ResponseTranslator.ToEmbeddings(
            EmbedResponse([1f], [2f], [3f]),
            "nomic-embed-text",
            base64: false);

        Assert.Equal(3, response.Data.Count);
        Assert.Equal([0, 1, 2], response.Data.Select(d => d.Index).ToArray());
    }

    [Fact]
    public void UsageComesFromPromptEvalCount()
    {
        var response = ResponseTranslator.ToEmbeddings(
            EmbedResponse([1f, 2f]),
            "nomic-embed-text",
            base64: false);

        Assert.Equal(9, response.Usage.PromptTokens);
        Assert.Equal(9, response.Usage.TotalTokens);
    }
}
