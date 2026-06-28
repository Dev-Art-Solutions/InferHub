using System.Text.Json;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

public sealed class EmbeddingDispatcher(
    IRouter router,
    IDispatcher dispatcher,
    IOptions<VectorStoreOptions> options,
    ILogger<EmbeddingDispatcher> logger) : IEmbeddingDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<string> DispatchEmbedAsync(string rawJson, string? modelOverride, CancellationToken cancellationToken)
    {
        var model = ResolveModel(rawJson, modelOverride)
            ?? throw new InvalidOperationException("embed request is missing 'model'");

        var node = router.Route(model, conversationKey: null)
            ?? throw new NoEmbeddingNodeException(model);

        var job = new InferenceJob(Guid.NewGuid(), "embed", rawJson);
        logger.LogInformation("Dispatching embed job {JobId} for model {Model} to {NodeId}", job.JobId, model, node.NodeId);
        var result = await dispatcher.DispatchAsync(node, job, cancellationToken);

        if (!result.Success)
        {
            throw new InvalidOperationException(result.Error ?? "embed job failed");
        }

        return result.ResponseJson ?? "{}";
    }

    public async Task<float[]> EmbedSingleAsync(string text, string? model, CancellationToken cancellationToken)
    {
        var resolved = !string.IsNullOrWhiteSpace(model) ? model! : options.Value.DefaultEmbeddingModel;
        var request = new EmbedRequest
        {
            Model = resolved,
            Input = JsonSerializer.SerializeToElement(text)
        };
        var rawJson = JsonSerializer.Serialize(request, JsonOptions);

        var responseJson = await DispatchEmbedAsync(rawJson, modelOverride: resolved, cancellationToken);

        var response = JsonSerializer.Deserialize<EmbedResponse>(responseJson, JsonOptions)
            ?? throw new InvalidOperationException("embed response could not be parsed");

        if (response.Embeddings.Count == 0)
        {
            throw new InvalidOperationException("embed response had no vectors");
        }

        return response.Embeddings[0].ToArray();
    }

    private static string? ResolveModel(string rawJson, string? modelOverride)
    {
        if (!string.IsNullOrWhiteSpace(modelOverride))
        {
            return modelOverride;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (document.RootElement.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
            {
                return model.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}

public sealed class NoEmbeddingNodeException(string model)
    : InvalidOperationException($"no node is advertising embedding model '{model}'")
{
    public string Model { get; } = model;
}
