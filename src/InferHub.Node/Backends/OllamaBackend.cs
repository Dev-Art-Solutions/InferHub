using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Shared.Contracts;
using OllamaClient;
using OllamaChatRequest = OllamaClient.Models.ChatRequest;
using OllamaGenerateRequest = OllamaClient.Models.GenerateRequest;

namespace InferHub.Node.Backends;

public sealed class OllamaBackend(
    IOllamaHttpClient client,
    ILogger<OllamaBackend> logger) : IInferenceBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Name => "ollama";

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetModels(cancellationToken);

            return response.Models
                .Select(model => new ModelInfo(model.Name, model.Digest, model.Size))
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list models from Ollama");
            return Array.Empty<ModelInfo>();
        }
    }

    public async Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken)
    {
        var request = Deserialize<OllamaGenerateRequest>(requestJson);
        var response = await client.Generate(request, cancellationToken);
        return JsonSerializer.Serialize(response, JsonOptions);
    }

    public async Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken)
    {
        var request = Deserialize<OllamaChatRequest>(requestJson);
        var response = await client.SendChat(request, cancellationToken);
        return JsonSerializer.Serialize(response, JsonOptions);
    }

    private static T Deserialize<T>(string requestJson)
    {
        return JsonSerializer.Deserialize<T>(requestJson, JsonOptions)
            ?? throw new InvalidOperationException("request body could not be deserialized");
    }
}
