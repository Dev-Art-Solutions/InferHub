using InferHub.Node.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends.Colibri;

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
