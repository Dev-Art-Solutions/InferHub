using InferHub.Shared.Contracts;
using OllamaClient;

namespace InferHub.Node.Backends;

public sealed class OllamaBackend(
    IOllamaHttpClient client,
    ILogger<OllamaBackend> logger) : IInferenceBackend
{
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
}
