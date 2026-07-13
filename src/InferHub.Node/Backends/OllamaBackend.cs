using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;
using OllamaClient;
using OllamaChatRequest = OllamaClient.Models.ChatRequest;
using OllamaChatStreamRequest = OllamaClient.Models.ChatStreamRequest;
using OllamaEmbedRequest = OllamaClient.Models.EmbedRequest;
using OllamaGenerateRequest = OllamaClient.Models.GenerateRequest;
using OllamaGenerateStreamRequest = OllamaClient.Models.GenerateStreamRequest;

namespace InferHub.Node.Backends;

public sealed class OllamaBackend(
    IOllamaHttpClient client,
    IOptions<OllamaOptions> options,
    ILogger<OllamaBackend> logger) : IInferenceBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Name => "ollama";

    public string Endpoint => options.Value.Endpoint;

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

    public async Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken)
    {
        var request = Deserialize<OllamaEmbedRequest>(requestJson);
        var response = await client.Embed(request, cancellationToken);
        return JsonSerializer.Serialize(response, JsonOptions);
    }

    public IAsyncEnumerable<string> StreamAsync(
        string kind,
        string requestJson,
        CancellationToken cancellationToken)
    {
        return kind switch
        {
            "generate" => StreamGenerateAsync(requestJson, cancellationToken),
            "chat" => StreamChatAsync(requestJson, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported inference job kind '{kind}'.")
        };
    }

    private async IAsyncEnumerable<string> StreamGenerateAsync(
        string requestJson,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = Deserialize<OllamaGenerateStreamRequest>(requestJson);

        await foreach (var chunk in client.Generate(request, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return JsonSerializer.Serialize(chunk, JsonOptions);
        }
    }

    private async IAsyncEnumerable<string> StreamChatAsync(
        string requestJson,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = Deserialize<OllamaChatStreamRequest>(requestJson);

        await foreach (var chunk in client.SendChat(request, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return JsonSerializer.Serialize(chunk, JsonOptions);
        }
    }

    private static T Deserialize<T>(string requestJson)
    {
        return JsonSerializer.Deserialize<T>(requestJson, JsonOptions)
            ?? throw new InvalidOperationException("request body could not be deserialized");
    }
}
