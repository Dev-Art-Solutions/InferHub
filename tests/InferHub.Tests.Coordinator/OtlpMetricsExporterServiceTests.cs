using InferHub.Coordinator.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 81, D5/D6. No other <c>BackgroundService</c> in this coordinator has a test that
/// constructs its full DI graph (76's own brief says so out loud), so this proves only the gate:
/// off by default, and "enabled with no endpoint" both return before touching DI or HTTP at all —
/// the <b>deployment-changes-no-config identity check</b> from the phase's own Done-when.
/// A poisoned <see cref="IServiceProvider"/> and <see cref="IHttpClientFactory"/> that throw on
/// first use stand in for "nothing downstream was touched."
/// </summary>
public class OtlpMetricsExporterServiceTests
{
    [Fact]
    public async Task DisabledByDefaultTouchesNothing()
    {
        var service = NewService(config: new Dictionary<string, string?>());

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
        await service.StopAsync(CancellationToken.None);

        // Reaching here without the poisoned stubs throwing is the assertion.
    }

    [Fact]
    public async Task EnabledWithNoEndpointTouchesNothing()
    {
        var service = NewService(config: new Dictionary<string, string?>
        {
            ["Observability:Otlp:Enabled"] = "true",
        });

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
        await service.StopAsync(CancellationToken.None);
    }

    private static OtlpMetricsExporterService NewService(Dictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();

        return new OtlpMetricsExporterService(
            services: new ThrowingServiceProvider(),
            httpClientFactory: new ThrowingHttpClientFactory(),
            configuration: configuration,
            options: Options.Create(new OtlpExporterOptions()),
            logger: NullLogger<OtlpMetricsExporterService>.Instance);
    }

    private sealed class ThrowingServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            throw new InvalidOperationException("the exporter should not have resolved anything while disabled/unconfigured");
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("the exporter should not have created an HTTP client while disabled/unconfigured");
    }
}
