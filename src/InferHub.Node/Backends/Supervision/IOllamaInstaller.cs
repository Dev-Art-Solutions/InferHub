namespace InferHub.Node.Backends.Supervision;

/// <summary>
/// Its own seam so the one-shot install rule can be tested without downloading anything.
/// </summary>
public interface IOllamaInstaller
{
    Task<ProcessControlResult> InstallAsync(CancellationToken cancellationToken);
}
