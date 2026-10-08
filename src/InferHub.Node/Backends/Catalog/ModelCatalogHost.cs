using InferHub.Node.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends.Catalog;

/// <summary>
/// Starts a single-backend catalogue — colibri's (phase 97) or Strata's (99) — with the host and stops
/// every model it loaded with it. Under <c>Backend:Engines</c> the engine's own start and stop do this instead.
/// </summary>
/// <remarks>
/// A meshed node waits up to <see cref="MultiBackendHost.ProfileWait"/> for its profile first, for
/// 95 D4's reason: loading the box's <c>Preload</c> and then unloading it a second later because the
/// hub pinned something else is a load of somebody's RAM for nothing.
/// </remarks>
public sealed class ModelCatalogHost(
    ModelCatalog catalog,
    IOptions<CoordinatorOptions> coordinator,
    ILogger<ModelCatalogHost> logger) : IHostedService
{
    private readonly CancellationTokenSource stopping = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (coordinator.Value.Enabled)
                {
                    await Task.WhenAny(catalog.FirstApplied, Task.Delay(MultiBackendHost.ProfileWait, stopping.Token));
                }

                catalog.Start();

                logger.LogInformation(
                    "{Engine} serves a catalogue of {Count} model(s): {Models}; at most {MaxLoaded} loaded at once, {Mode}.",
                    catalog.Name,
                    catalog.CatalogNames.Count,
                    catalog.CatalogNames.Count == 0 ? "none yet" : string.Join(", ", catalog.CatalogNames),
                    catalog.MaxLoaded,
                    catalog.OnDemand ? "idle ones stopped on demand" : "loaded ones kept until evicted or unloaded");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not start the {Engine} catalogue.", catalog.Name);
            }
        });

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await stopping.CancelAsync();
        await catalog.StopAsync(cancellationToken);
    }
}
