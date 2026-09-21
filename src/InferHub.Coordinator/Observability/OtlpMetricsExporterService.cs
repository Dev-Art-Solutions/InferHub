using System.Reflection;
using System.Text;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Cluster;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Observability;

/// <summary>
/// Pushes the same numbers <c>/metrics</c> serves to an OTLP/HTTP collector on an interval (phase
/// 81), for the operator who has no Prometheus server to scrape it. Gated and shaped the way
/// <c>AutoScalerService</c> and <c>CorpusFailoverService</c> already are (D5): <c>Enabled</c> read
/// from <see cref="IConfiguration"/> at the top of every tick's setup, default <c>false</c>, logged
/// and returned early when off — a deployment that sets nothing here behaves exactly like v3.45.1.
/// </summary>
public sealed class OtlpMetricsExporterService(
    IServiceProvider services,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IOptions<OtlpExporterOptions> options,
    ILogger<OtlpMetricsExporterService> logger) : BackgroundService
{
    public const string HttpClientName = "otlp-exporter";

    // Same computation Program.cs uses for /health and /metrics — kept local rather than shared
    // via DI because it is two lines and reflecting the same assembly, not a service with state.
    private static readonly string Version = typeof(OtlpMetricsExporterService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion
        ?? typeof(OtlpMetricsExporterService).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;

        if (!configuration.GetValue($"{OtlpExporterOptions.SectionName}:Enabled", false))
        {
            logger.LogInformation("OTLP metrics push disabled ({Section}:Enabled is not true)", OtlpExporterOptions.SectionName);
            return;
        }

        if (string.IsNullOrWhiteSpace(opts.Endpoint))
        {
            logger.LogWarning("OTLP metrics push enabled but {Section}:Endpoint is not set — nothing will be sent", OtlpExporterOptions.SectionName);
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, opts.IntervalSeconds));
        var pushUri = new Uri(opts.Endpoint.TrimEnd('/') + "/v1/metrics");

        // Resolved once: these are the same singleton registries MetricsEndpoint resolves per
        // request, so there is nothing per-tick to re-fetch from DI beyond the scoped ones below.
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PushOnceAsync(sp, opts, pushUri, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // D6: a push failure is logged once and discarded. There is no retry queue — a
                // gauge sent late is not "restored," it is a stale value reported as current.
                logger.LogWarning(ex, "OTLP metrics push to {Endpoint} failed", pushUri);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PushOnceAsync(IServiceProvider sp, OtlpExporterOptions opts, Uri pushUri, CancellationToken ct)
    {
        var metrics = sp.GetRequiredService<Metrics>();
        var scrape = ScrapeSnapshotBuilder.Build(
            Version,
            sp.GetRequiredService<INodeRegistry>(),
            metrics,
            sp.GetRequiredService<ThroughputTracker>(),
            sp.GetRequiredService<IRequestQueue>(),
            sp.GetRequiredService<IClientRegistry>(),
            sp.GetRequiredService<AdmissionControl>(),
            sp.GetRequiredService<IConversationAffinity>(),
            sp.GetRequiredService<IClusterMembership>(),
            sp.GetRequiredService<IProfileRegistry>(),
            sp.GetRequiredService<NodeToolRegistry>(),
            sp.GetRequiredService<NodeCorpusRegistry>(),
            sp);

        var now = DateTimeOffset.UtcNow;
        var text = PrometheusFormatter.Format(scrape);
        var document = ExpositionReader.Parse(text);
        var result = OtlpFormatter.Build(document, Version, metrics.StartedAtUtc, now);

        if (result.SkippedFamilies.Count > 0)
        {
            logger.LogDebug("OTLP push skipping histogram families (D3): {Families}", string.Join(", ", result.SkippedFamilies));
        }

        var json = OtlpFormatter.ToJson(result.Payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, pushUri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        foreach (var header in opts.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, ct);

        if (response.IsSuccessStatusCode)
        {
            logger.LogDebug("OTLP metrics push to {Endpoint} succeeded ({Status})", pushUri, (int)response.StatusCode);
        }
        else
        {
            logger.LogWarning("OTLP metrics push to {Endpoint} answered {Status}", pushUri, (int)response.StatusCode);
        }
    }
}
