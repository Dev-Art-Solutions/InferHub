using InferHub.Node.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends.Strata;

public static class StrataComposition
{
    /// <summary>The readiness probe's own short-deadline client (36 D2's reason for a second client).</summary>
    public const string ProbeClientName = "strata-catalogue-probe";

    /// <summary>Whether this node serves a Strata catalogue: <c>Strata:Root</c>, and Strata as its backend or one of its engines.</summary>
    public static bool HasCatalog(IConfiguration configuration, out EngineOptions? engine)
    {
        var strata = configuration.GetSection(StrataOptions.SectionName).Get<StrataOptions>() ?? new StrataOptions();
        var backend = configuration.GetSection(BackendOptions.SectionName).Get<BackendOptions>() ?? new BackendOptions();

        engine = backend.IsMulti
            ? backend.Engines.Values.FirstOrDefault(e => e.NormalizedType() == BackendOptions.Strata)
            : null;

        return HasCatalog(strata, backend);
    }

    public static bool HasCatalog(StrataOptions strata, BackendOptions backend)
        => strata.IsCatalog && (backend.IsMulti
            ? backend.Engines.Values.Any(e => e.NormalizedType() == BackendOptions.Strata)
            : backend.Normalized() == BackendOptions.Strata);

    /// <param name="timeoutSeconds">The request timeout a loaded model's client gets: <c>Upstream:TimeoutSeconds</c>, or the engine's.</param>
    public static StrataCatalog Create(IServiceProvider services, int timeoutSeconds)
    {
        var strata = services.GetRequiredService<IOptions<StrataOptions>>().Value;
        var backend = services.GetRequiredService<IOptions<BackendOptions>>().Value;
        var factory = services.GetRequiredService<IHttpClientFactory>();
        var time = services.GetRequiredService<TimeProvider>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("InferHub.Node.Strata");
        var upstreamLogger = services.GetRequiredService<ILogger<UpstreamBackend>>();

        return new StrataCatalog(
            strata,
            new StrataProcessLauncher(strata, time, logger),
            (baseUrl, apiKey) => new UpstreamBackend(
                factory,
                Options.Create(new BackendOptions { Type = BackendOptions.Strata }),
                Options.Create(new UpstreamBackendOptions { BaseUrl = baseUrl, ApiKey = apiKey, TimeoutSeconds = timeoutSeconds }),
                upstreamLogger),
            () => factory.CreateClient(ProbeClientName),
            backend.StopDrain,
            time,
            logger)
        {
            // The installer is registered on the same condition (AddStrataInstaller); the panel's list follows it.
            InstallsFromHub = services.GetRequiredService<IOptions<HuggingFace.HuggingFaceOptions>>().Value.Enabled
        };
    }
}
