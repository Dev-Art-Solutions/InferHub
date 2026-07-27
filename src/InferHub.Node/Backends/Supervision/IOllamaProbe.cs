namespace InferHub.Node.Backends.Supervision;

/// <summary>
/// The cheapest question that can be asked of a local Ollama, on its own short deadline.
/// </summary>
public interface IOllamaProbe
{
    Task<BackendHealth> CheckAsync(CancellationToken cancellationToken);
}
