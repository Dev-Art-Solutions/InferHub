using InferHub.Node.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends.Colibri;

/// <summary>
/// Starts a single-backend colibri catalogue with the host and stops every model it loaded with it
/// (phase 97). Under <c>Backend:Engines</c> the engine's own start and stop do this instead.
/// </summary>
/// <remarks>
/// A meshed node waits up to <see cref="MultiBackendHost.ProfileWait"/> for its profile first, for
/// 95 D4's reason: loading the box's <c>Preload</c> and then unloading it a second later because the
/// hub pinned something else is a load of somebody's RAM for nothing.
/// </remarks>
public sealed class ColibriCatalogHost(
    ColibriCatalog catalog,
    IOptions<CoordinatorOptions> coordinator,
    ILogger<ColibriCatalogHost> logger) : IHostedService
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
                    "colibri serves a catalogue of {Count} model(s): {Models}; at most {MaxLoaded} loaded at once, {Mode}.",
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
                logger.LogError(ex, "Could not start the colibri catalogue.");
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

public static class ColibriCatalogComposition
{
    /// <summary>The readiness probe's own short-deadline client (36 D2's reason for a second client).</summary>
    public const string ProbeClientName = "colibri-catalogue-probe";

    /// <param name="timeoutSeconds">The request timeout a loaded model's client gets: <c>Upstream:TimeoutSeconds</c>, or the engine's.</param>
    public static ColibriCatalog Create(IServiceProvider services, int timeoutSeconds)
    {
        var colibri = services.GetRequiredService<IOptions<ColibriOptions>>().Value;
        var backend = services.GetRequiredService<IOptions<BackendOptions>>().Value;
        var factory = services.GetRequiredService<IHttpClientFactory>();
        var time = services.GetRequiredService<TimeProvider>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("InferHub.Node.Colibri");
        var upstreamLogger = services.GetRequiredService<ILogger<UpstreamBackend>>();

        return new ColibriCatalog(
            colibri,
            new ColibriProcessLauncher(colibri, time, logger),
            baseUrl => new UpstreamBackend(
                factory,
                Options.Create(new BackendOptions { Type = BackendOptions.Colibri }),
                Options.Create(new UpstreamBackendOptions { BaseUrl = baseUrl, TimeoutSeconds = timeoutSeconds }),
                upstreamLogger),
            () => factory.CreateClient(ProbeClientName),
            backend.StopDrain,
            time,
            logger);
    }
}
