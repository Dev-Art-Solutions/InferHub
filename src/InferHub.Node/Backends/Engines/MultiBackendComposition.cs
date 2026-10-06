using InferHub.Node.Backends.Colibri;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends;

/// <summary>
/// Builds a <see cref="MultiBackend"/> from <c>Backend:Engines</c> (phase 95). Each engine is the
/// class that already drives its type — <see cref="OllamaBackend"/> (behind phase 85's on-demand
/// gate when that is on) or <see cref="UpstreamBackend"/> with options of its own — so there is no
/// second Ollama driver and no second dialect, only a second <em>instance</em>.
/// </summary>
public static class MultiBackendComposition
{
    public static MultiBackend Create(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<BackendOptions>>().Value;
        var colibri = services.GetRequiredService<IOptions<ColibriOptions>>().Value;
        var time = services.GetRequiredService<TimeProvider>();
        var loggers = services.GetRequiredService<ILoggerFactory>();
        var logger = loggers.CreateLogger("InferHub.Node.Engines");

        // Sorted by name, explicitly: that is the order configuration binds them in anyway, and it is
        // the tie-break for a model two engines both report (D1).
        var engines = options.Engines
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => Build(services, pair.Key, pair.Value, colibri, time, logger))
            .ToArray();

        return new MultiBackend(engines, options.StopDrain, time, logger);
    }

    private static Engine Build(
        IServiceProvider services,
        string name,
        EngineOptions engine,
        ColibriOptions colibri,
        TimeProvider time,
        ILogger logger)
    {
        var type = engine.NormalizedType();

        if (type == BackendOptions.Ollama)
        {
            var ollama = services.GetRequiredService<OllamaBackend>();
            var arbiter = services.GetRequiredService<Resources.GpuArbiter>();

            // Phase 85's decorator, exactly as a single-backend Ollama node gets it.
            IInferenceBackend backend = arbiter.Enabled
                ? new OnDemandBackend(ollama, arbiter, ollama.UnloadAsync)
                : ollama;

            return new Engine(name, type, backend, engine.Autostart, engine.Models, unload: ollama.UnloadAsync);
        }

        string? baseUrl;
        EngineProcess? process = null;

        switch (type)
        {
            case BackendOptions.Colibri when colibri.Serve.IsEnabled:
                baseUrl = colibri.LaunchedBaseUrl();
                process = new EngineProcess(
                    name,
                    () => ColibriServe.StartInfo(colibri),
                    () => Directory.Exists(colibri.Serve.Model!)
                        ? null
                        : $"{ColibriOptions.SectionName}:Serve:Model '{colibri.Serve.Model}' is not a directory this node can see; mount the converted model there.",
                    time,
                    logger);
                break;

            case BackendOptions.LlamaCpp when engine.Serve.IsEnabled:
                baseUrl = engine.Serve.LaunchedBaseUrl();
                process = new EngineProcess(
                    name,
                    // The preset INI is written at each launch, so the process reads this config (96 D1).
                    () => LlamaCppServe.StartInfo(engine, LlamaCppServe.WritePresets(name, engine)),
                    () => LlamaCppServe.Precondition(engine),
                    time,
                    logger);
                break;

            default:
                baseUrl = string.IsNullOrWhiteSpace(engine.BaseUrl) ? null : engine.BaseUrl.Trim();
                break;
        }

        IReadOnlyList<string>? kinds = type is BackendOptions.LlamaCpp or BackendOptions.OpenAi
            ? engine.Embeddings ? [CapabilityKinds.Embed] : type == BackendOptions.LlamaCpp ? [CapabilityKinds.Chat] : null
            : null;

        var upstream = new UpstreamBackend(
            services.GetRequiredService<IHttpClientFactory>(),
            Options.Create(new BackendOptions { Type = type }),
            Options.Create(new UpstreamBackendOptions
            {
                BaseUrl = baseUrl,
                ApiKey = engine.ApiKey,
                TimeoutSeconds = engine.TimeoutSeconds
            }),
            services.GetRequiredService<ILogger<UpstreamBackend>>(),
            kinds);

        if (type != BackendOptions.LlamaCpp)
        {
            return new Engine(name, type, upstream, engine.Autostart, engine.Models, process);
        }

        // 96: the same upstream for everything with an Ollama shape, the server's root for the rest.
        var factory = services.GetRequiredService<IHttpClientFactory>();
        var root = RootOf(baseUrl);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, engine.TimeoutSeconds));

        var llamaCpp = new LlamaCppBackend(
            upstream,
            engine,
            router: engine.Serve.IsRouter || engine.Router,
            () =>
            {
                var http = factory.CreateClient(UpstreamBackend.HttpClientName);
                http.BaseAddress = root;
                http.Timeout = timeout;
                return http;
            },
            time,
            logger);

        return new Engine(name, type, llamaCpp, engine.Autostart, engine.Models, process);
    }

    /// <summary><c>http://127.0.0.1:8080/v1</c> → <c>http://127.0.0.1:8080/</c>: llama.cpp's own routes live at the root.</summary>
    internal static Uri? RootOf(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        var trimmed = baseUrl.Trim().TrimEnd('/');

        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3];
        }

        return new Uri(trimmed + "/");
    }
}

/// <summary>
/// Starts the autostart engines with the host and stops every launched one with it (phase 95).
/// </summary>
/// <remarks>
/// <b>A meshed node waits for its profile before autostarting</b> (D4) — up to
/// <see cref="ProfileWait"/>, in the background so nothing else waits with it. Found in the live
/// check: booting straight into <c>Autostart</c> launched a <c>llama-server</c> the hub had stopped,
/// loaded its weights, and killed it a second later when the profile arrived. A hub that does not
/// answer in time gets the autostart set, which is what the box would run without one anyway.
/// </remarks>
public sealed class MultiBackendHost(
    MultiBackend engines,
    IOptions<Configuration.CoordinatorOptions> coordinator,
    ILogger<MultiBackendHost> logger) : IHostedService
{
    public static readonly TimeSpan ProfileWait = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource stopping = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (coordinator.Value.Enabled)
                {
                    await Task.WhenAny(engines.FirstApplied, Task.Delay(ProfileWait, stopping.Token));
                }

                var started = await engines.StartAsync(stopping.Token);

                logger.LogInformation(
                    "This node runs {Count} engine(s): {Engines}. {Started}",
                    engines.Engines.Count,
                    string.Join(", ", engines.Engines.Select(e => $"{e.Name} ({e.Type}{(e.Process is null ? "" : ", launched")})")),
                    started.Count == 0 ? "Nothing more to start." : string.Join(", ", started) + ".");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not start this node's engines.");
            }
        });

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await stopping.CancelAsync();
        await engines.StopAllAsync();
    }
}
