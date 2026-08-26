using System.Text.Json;
using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Retrieval;

/// <summary>
/// Solo mode's embedding path (phase-38 D6): the same seam ingestion and retrieval already used on
/// the hub, with the fleet taken out of it.
/// </summary>
/// <remarks>
/// <para>
/// On the coordinator, <c>EmbeddingDispatcher</c> picks a node that advertises the model and sends
/// the job over SignalR. Here there is one backend and it is on this machine, so the whole thing is
/// <see cref="InferenceExecutor"/> — which is the phase-37 D2 framing applied a second time: the
/// hub's formatting layer over the node's executor, with the routing layer removed.
/// </para>
/// <para>
/// <see cref="NoEmbeddingNodeException"/> keeps its meaning: on the hub it is "nobody serves this
/// model", here it is "this box does not". Both are the case that will not fix itself in 400 ms,
/// which is why <c>IngestionPipeline</c> deliberately does not retry it.
/// </para>
/// </remarks>
public sealed class LocalEmbeddingDispatcher(
    InferenceExecutor executor,
    IInferenceBackend backend,
    IOptions<LocalRetrievalOptions> options,
    ILogger<LocalEmbeddingDispatcher> logger) : IEmbeddingDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> DispatchEmbedAsync(string rawJson, string? modelOverride, CancellationToken cancellationToken)
    {
        var body = modelOverride is null ? rawJson : WithModel(rawJson, modelOverride);
        var model = ReadModel(body) ?? options.Value.DefaultEmbeddingModel;

        await EnsureServedAsync(model, cancellationToken);

        var result = await executor.RunAsync(new InferenceJob(Guid.NewGuid(), "embed", body), cancellationToken);

        if (!result.Success || string.IsNullOrEmpty(result.ResponseJson))
        {
            throw new InvalidOperationException(NodeErrorText.Readable(result.Error) ?? "embed failed");
        }

        return result.ResponseJson;
    }

    public async Task<float[]> EmbedSingleAsync(string text, string? model, CancellationToken cancellationToken)
    {
        var resolved = string.IsNullOrWhiteSpace(model) ? options.Value.DefaultEmbeddingModel : model;
        await EnsureServedAsync(resolved, cancellationToken);

        var request = new EmbedRequest
        {
            Model = resolved,
            Input = JsonSerializer.SerializeToElement(text)
        };

        var result = await executor.RunAsync(
            new InferenceJob(Guid.NewGuid(), "embed", JsonSerializer.Serialize(request, JsonOptions)),
            cancellationToken);

        if (!result.Success || string.IsNullOrEmpty(result.ResponseJson))
        {
            throw new InvalidOperationException(NodeErrorText.Readable(result.Error) ?? "embed failed");
        }

        var response = JsonSerializer.Deserialize<EmbedResponse>(result.ResponseJson, JsonOptions);
        if (response is null || response.Embeddings.Count == 0 || response.Embeddings[0].Count == 0)
        {
            throw new InvalidOperationException($"embed of '{resolved}' returned no vector");
        }

        return [.. response.Embeddings[0]];
    }

    /// <summary>
    /// Fail with the hub's own exception, and its own status, before any work is dispatched — a
    /// backend that does not have the model will otherwise report it seconds later as an opaque
    /// upstream error, which is much the worse message of the two.
    /// </summary>
    /// <remarks>
    /// A backend that cannot enumerate its models (a wedged Ollama, an upstream that is down) is
    /// deliberately <em>not</em> turned into "model not served": that would be a diagnosis, and the
    /// honest thing is to let the embed attempt itself produce the real error. Compare phase-36 D7,
    /// where an empty model report is what un-routes a node — here there is no fleet to un-route
    /// from, so the request goes through and fails with the backend's own words.
    /// </remarks>
    private async Task EnsureServedAsync(string model, CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelInfo> models;
        try
        {
            models = await backend.ListModelsAsync(cancellationToken) ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not list backend models before embedding with '{Model}'; letting the embed itself report", model);
            return;
        }

        if (models.Count == 0)
        {
            return;
        }

        // Ollama reports "nomic-embed-text:latest" for a model pulled as "nomic-embed-text", and
        // clients say either. Same tolerance the fleet's model matching has.
        var served = models.Any(m =>
            string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase)
            || string.Equals(StripTag(m.Name), StripTag(model), StringComparison.OrdinalIgnoreCase));

        if (!served)
        {
            throw new NoEmbeddingNodeException(model);
        }
    }

    private static string StripTag(string name)
    {
        var colon = name.IndexOf(':');
        return colon < 0 ? name : name[..colon];
    }

    private static string? ReadModel(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            return doc.RootElement.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
                ? model.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string WithModel(string rawJson, string model)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(rawJson)?.AsObject()
            ?? throw new InvalidOperationException("embed request body is not a JSON object");
        node["model"] = model;
        return node.ToJsonString(JsonOptions);
    }
}
