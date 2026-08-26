using InferHub.Shared.Contracts;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// The half of phase 36 that unit tests cannot reach: the real probe against a real socket, and
/// the real <see cref="OllamaProcessControl"/> against whatever this machine actually has —
/// a Windows service, a systemd unit, or a binary on <c>PATH</c>. Gated behind
/// <c>INFERHUB_TEST_OLLAMA_SUPERVISOR=1</c> because it <strong>stops and starts the local
/// Ollama</strong>.
/// </summary>
/// <remarks>
/// It deliberately stops short of the coordinator. That the recovery is <em>reported</em> is a
/// subscription, pinned in <c>OllamaSupervisorTests</c>; what needs a real machine is everything
/// below the seam — discovery picking the right control path, the service manager or the spawn
/// actually working, and the probe telling a stopped Ollama from a wedged one for real.
/// </remarks>
public class OllamaSupervisorIntegrationTests
{
    [OllamaSupervisorFact]
    public async Task TheSupervisorFindsHowOllamaIsInstalledOnThisMachine()
    {
        var control = Control();

        var installation = await control.DiscoverAsync(CancellationToken.None);

        Assert.NotEqual(OllamaInstallKind.Missing, installation.Kind);
        Assert.NotEmpty(installation.Target);
    }

    [OllamaSupervisorFact]
    public async Task ARunningOllamaProbesHealthy()
    {
        var probe = Probe(TimeSpan.FromSeconds(5));

        Assert.Equal(BackendHealth.Healthy, await probe.CheckAsync(CancellationToken.None));
    }

    [OllamaSupervisorFact]
    public async Task StoppingOllamaMakesItUnreachableAndTheSupervisorBringsItBack()
    {
        var probeTimeout = TimeSpan.FromSeconds(5);
        var probe = Probe(probeTimeout);
        var control = Control();
        var installation = await control.DiscoverAsync(CancellationToken.None);

        Assert.Equal(BackendHealth.Healthy, await probe.CheckAsync(CancellationToken.None));

        var stop = await control.StopAsync(installation, CancellationToken.None);
        Assert.True(stop.Success, stop.Error);

        // Give the port time to actually close before asserting on the classification.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(BackendHealth.Unreachable, await probe.CheckAsync(CancellationToken.None));

        var supervisor = new OllamaSupervisor(
            Options.Create(new OllamaSupervisorOptions
            {
                Enabled = true,
                UnhealthyThreshold = 1,
                ProbeTimeout = probeTimeout,
                ReadyTimeout = TimeSpan.FromMinutes(2)
            }),
            Options.Create(new OllamaOptions { Endpoint = OllamaSupervisorTestGate.Endpoint }),
            probe,
            control,
            new ThrowingInstaller(),
            TimeProvider.System,
            NullLogger<OllamaSupervisor>.Instance);

        var recoveries = 0;
        supervisor.Recovered += () => recoveries++;

        await supervisor.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(3));

        Assert.Equal(1, recoveries);
        Assert.Equal(BackendHealth.Healthy, supervisor.Health);
        Assert.Equal(BackendHealth.Healthy, await probe.CheckAsync(CancellationToken.None));
    }

    private static OllamaProbe Probe(TimeSpan probeTimeout)
        => new(new SingleClientFactory(new HttpClient(OllamaProbe.CreateHandler(probeTimeout))
        {
            BaseAddress = new Uri(OllamaSupervisorTestGate.Endpoint),
            Timeout = probeTimeout
        }));

    private static OllamaProcessControl Control()
        => new(Options.Create(new OllamaSupervisorOptions { Enabled = true }),
            NullLogger<OllamaProcessControl>.Instance);

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>Ollama is installed on a box running these tests; reaching the installer is a bug.</summary>
    private sealed class ThrowingInstaller : IOllamaInstaller
    {
        public Task<ProcessControlResult> InstallAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("the install path must never be reached from a restart");
    }
}
