using System.Net.Http.Json;
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
using OllamaDeleteRequest = OllamaClient.Models.DeleteRequest;
using OllamaPullStreamRequest = OllamaClient.Models.PullStreamRequest;

namespace InferHub.Node.Backends;

public sealed class OllamaBackend(
    IOllamaHttpClient client,
    IOptions<OllamaOptions> options,
    ILogger<OllamaBackend> logger,
    IHttpClientFactory? http = null) : IInferenceBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>What OllamaClient reads a stream's lines with, so a chunk deserializes exactly as it did.</summary>
    private static readonly JsonSerializerOptions NdjsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string Name => "ollama";

    public string Endpoint => options.Value.Endpoint;

    /// <summary>
    /// Ollama serves both, and always has: <c>/api/chat</c> and <c>/api/embed</c> are the same
    /// server. Phase 67 made this a declaration rather than a constant inside
    /// <c>BackendCapabilities</c>, because a cloud upstream may serve only one of them.
    /// </summary>
    public IReadOnlyList<string> Kinds { get; } = [CapabilityKinds.Chat, CapabilityKinds.Embed];

    public async Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken)
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

            // Null, not empty: "could not ask" is not "has none" (see IInferenceBackend).
            return null;
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

        var chunks = http is null
            ? client.Generate(request, cancellationToken)
            : ReadStreamAsync<OllamaClient.Models.GenerateResponse>("generate", request, cancellationToken);

        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
        {
            yield return JsonSerializer.Serialize(chunk, JsonOptions);
        }
    }

    private async IAsyncEnumerable<string> StreamChatAsync(
        string requestJson,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = Deserialize<OllamaChatStreamRequest>(requestJson);

        var chunks = http is null
            ? client.SendChat(request, cancellationToken)
            : ReadStreamAsync<OllamaClient.Models.ChatResponse>("chat", request, cancellationToken);

        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
        {
            yield return JsonSerializer.Serialize(chunk, JsonOptions);
        }
    }

    public bool SupportsModelManagement => true;

    public async IAsyncEnumerable<ModelPullProgress> PullAsync(
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new OllamaPullStreamRequest { Name = model };

        var chunks = http is null
            ? client.Pull(request, cancellationToken)
            : ReadStreamAsync<OllamaClient.Models.PullResponse>("pull", request, cancellationToken);

        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
        {
            yield return new ModelPullProgress(chunk.Status, chunk.Total, chunk.Completed);
        }
    }

    public async Task DeleteAsync(string model, CancellationToken cancellationToken)
    {
        var response = await client.Delete(new OllamaDeleteRequest { Name = model }, cancellationToken);

        if (!response.IsSuccessful)
        {
            throw new InvalidOperationException($"Ollama refused to delete model '{model}'.");
        }
    }

    public async Task WarmAsync(string model, CancellationToken cancellationToken)
    {
        // An empty-prompt generate makes Ollama load the model into memory and return immediately
        // (done_reason "load"), which is exactly a warm — no tokens are produced.
        await client.Generate(new OllamaGenerateRequest { Model = model, Prompt = string.Empty }, cancellationToken);
    }

    /// <summary>The console's unload (96 D3): the same <c>keep_alive: 0</c> as phase 85's release.</summary>
    public Task UnloadAsync(string model, CancellationToken cancellationToken)
        => UnloadAsync([model], cancellationToken);

    /// <summary>
    /// Unloads the named models if Ollama has them resident (phase 85, <c>Node:OnDemand</c>). A
    /// generate with an empty prompt and <c>keep_alive: 0</c> is Ollama's documented way to drop a
    /// model from memory at once.
    /// </summary>
    /// <remarks>
    /// <b>Only the named ones.</b> The Ollama on a desktop is shared — the owner's own chat client,
    /// an agent framework — and unloading a 20 GB model somebody else is using would make them pay
    /// its load again. Matched against <c>/api/ps</c> first, because a <c>keep_alive: 0</c> generate
    /// for a model that is not loaded would load it just to drop it.
    /// </remarks>
    public async Task UnloadAsync(IReadOnlyCollection<string> models, CancellationToken cancellationToken)
    {
        if (models.Count == 0)
        {
            return;
        }

        var wanted = models.Select(NormalizeModelName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var running = await client.GetRunningModels(cancellationToken);

        foreach (var model in running.Models)
        {
            if (!wanted.Contains(NormalizeModelName(model.Name)) && !wanted.Contains(NormalizeModelName(model.Model)))
            {
                continue;
            }

            await client.Generate(
                new OllamaGenerateRequest { Model = model.Name, Prompt = string.Empty, KeepAlive = "0s" },
                cancellationToken);
        }
    }

    /// <summary><c>llama3</c> and <c>llama3:latest</c> are the same model to Ollama.</summary>
    internal static string NormalizeModelName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        var lastSegment = trimmed[(trimmed.LastIndexOf('/') + 1)..];

        return trimmed.Length == 0 || lastSegment.Contains(':') ? trimmed : trimmed + ":latest";
    }

    /// <summary>
    /// Phase 107, found by running it: OllamaClient 2.0.1 posts a stream with <c>PostAsJsonAsync</c>,
    /// whose default reads the <b>whole body</b> before returning, so every streamed answer from an
    /// Ollama node — on the hub and in solo mode — arrived in one piece at the end (first line at 6.3 s
    /// of a 6.3 s answer; 0.45 s straight from Ollama), and a pull's progress only when it was done.
    /// The same request and the same DTOs, read as the lines come. OllamaClient's own fix is its own
    /// release; until a node takes it, this is the path.
    /// </summary>
    private async IAsyncEnumerable<T> ReadStreamAsync<T>(
        string action,
        object request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var httpClient = http!.CreateClient(nameof(OllamaHttpClient));
        var url = new Uri(new Uri(options.Value.Endpoint.TrimEnd('/') + "/"), $"api/{action}");
        using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(request, request.GetType()) };
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new OllamaClient.Exceptions.OllamaException(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(body);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length > 0)
            {
                yield return JsonSerializer.Deserialize<T>(line, NdjsonOptions)!;
            }
        }
    }

    private static T Deserialize<T>(string requestJson)
    {
        return JsonSerializer.Deserialize<T>(requestJson, JsonOptions)
            ?? throw new InvalidOperationException("request body could not be deserialized");
    }
}
