using System.Net;
using System.Net.Sockets;
using System.Text;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 36. No processes are started and no server is stopped: the supervisor is a state machine
/// over three seams and a clock, which is precisely so that the classification, the restart budget
/// and the one-shot install rule can be pinned without killing anything on a build agent.
/// </summary>
/// <remarks>
/// The probe tests are the exception — two of them use a real socket, because a stub handler can
/// only echo the exception a test author already believed in. "Connection refused is Unreachable"
/// and "accepts but never answers is Wedged" are claims about what .NET actually throws, and only
/// a real socket can make them.
/// </remarks>
public class OllamaSupervisorTests
{
    // ---- probe classification --------------------------------------------------------------

    [Fact]
    public async Task AnAnsweringOllamaIsHealthy()
    {
        var probe = StubProbe(HttpStatusCode.OK, """{"version":"0.5.4"}""");

        Assert.Equal(BackendHealth.Healthy, await probe.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AFiveHundredIsWedgedBecauseTheProcessIsUpAndBroken()
    {
        var probe = StubProbe(HttpStatusCode.InternalServerError, "boom");

        Assert.Equal(BackendHealth.Wedged, await probe.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AFourOhFourIsAResponsiveServerAndIsLeftAlone()
    {
        // Something answered promptly. Restarting a server over a path we got wrong would be
        // fixing the wrong problem.
        var probe = StubProbe(HttpStatusCode.NotFound, "not found");

        Assert.Equal(BackendHealth.Healthy, await probe.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AConnectionRefusedIsUnreachableSoItGetsStartedNotStopped()
    {
        var port = FreePort();
        var probe = RealProbe($"http://127.0.0.1:{port}/", TimeSpan.FromSeconds(2));

        Assert.Equal(BackendHealth.Unreachable, await probe.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AListenerThatAcceptsAndNeverAnswersIsWedged()
    {
        // The exact failure this phase exists for: the process is alive, the port accepts, and
        // nothing ever comes back.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = listener.AcceptTcpClientAsync();

            var probe = RealProbe($"http://127.0.0.1:{port}/", TimeSpan.FromMilliseconds(600));

            Assert.Equal(BackendHealth.Wedged, await probe.CheckAsync(CancellationToken.None));
        }
        finally
        {
            listener.Stop();
        }
    }

    // ---- the threshold ---------------------------------------------------------------------

    [Fact]
    public async Task OneFailedProbeRestartsNothing()
    {
        var (supervisor, probe, control, _) = Build(o => o.UnhealthyThreshold = 3);
        probe.Returns(BackendHealth.Unreachable);

        await supervisor.TickAsync(CancellationToken.None);

        Assert.Empty(control.Calls);
        Assert.Null(supervisor.Health);
    }

    [Fact]
    public async Task TheThresholdIsConsecutiveAndASuccessResetsIt()
    {
        var (supervisor, probe, control, _) = Build(o => o.UnhealthyThreshold = 3);
        probe.Returns(
            BackendHealth.Unreachable,
            BackendHealth.Unreachable,
            BackendHealth.Healthy,
            BackendHealth.Unreachable,
            BackendHealth.Unreachable);

        await Tick(supervisor, 5);

        // Five failures in a row would have restarted twice. Two, then two, restarts nothing —
        // a saturated box mid-load is not a wedge.
        Assert.Empty(control.Calls);
    }

    [Fact]
    public async Task CrossingTheThresholdRestarts()
    {
        var (supervisor, probe, control, _) = Build(o => o.UnhealthyThreshold = 3);
        probe.Returns(BackendHealth.Unreachable, BackendHealth.Unreachable, BackendHealth.Unreachable);
        probe.Default = BackendHealth.Healthy;

        await Tick(supervisor, 3);

        Assert.Contains("start", control.Calls);
    }

    // ---- three states, three cures ---------------------------------------------------------

    [Fact]
    public async Task AnUnreachableOllamaIsStartedAndNeverStopped()
    {
        var (supervisor, probe, control, _) = Build(o => o.UnhealthyThreshold = 1);
        probe.Returns(BackendHealth.Unreachable);
        probe.Default = BackendHealth.Healthy;

        await supervisor.TickAsync(CancellationToken.None);

        // A stop here would be a no-op that hides a genuine config error — a wrong port or a
        // wrong host — behind a cheerful "restarted Ollama".
        Assert.Equal(["discover", "start"], control.Calls);
    }

    [Fact]
    public async Task AWedgedOllamaIsStoppedBeforeItIsStarted()
    {
        var (supervisor, probe, control, _) = Build(o => o.UnhealthyThreshold = 1);
        probe.Returns(BackendHealth.Wedged);
        probe.Default = BackendHealth.Healthy;

        await supervisor.TickAsync(CancellationToken.None);

        // Starting a wedged process fails on a port that is already bound, and the log then
        // blames the wrong thing.
        Assert.Equal(["discover", "stop", "start"], control.Calls);
    }

    [Fact]
    public async Task AFailedStopDoesNotGoOnToStart()
    {
        var (supervisor, probe, control, _) = Build(o => o.UnhealthyThreshold = 1);
        control.StopResult = ProcessControlResult.Denied("not allowed to control the service");
        probe.Returns(BackendHealth.Wedged);
        probe.Default = BackendHealth.Healthy;

        await supervisor.TickAsync(CancellationToken.None);

        Assert.Equal(["discover", "stop"], control.Calls);
    }

    [Fact]
    public async Task ADiscoveredServiceIsRestartedThroughItsManagerRatherThanRespawned()
    {
        var (supervisor, probe, control, _) = Build(o => o.UnhealthyThreshold = 1);
        control.Installation = OllamaInstallation.Service("Ollama");
        probe.Returns(BackendHealth.Wedged);
        probe.Default = BackendHealth.Healthy;

        await supervisor.TickAsync(CancellationToken.None);

        // Spawning `ollama serve` next to a service-managed install gets you two servers fighting
        // over :11434, and the one that loses is the one whose logs the operator is reading.
        Assert.All(control.Targets, target => Assert.Equal(OllamaInstallKind.Service, target.Kind));
        Assert.All(control.Targets, target => Assert.Equal("Ollama", target.Target));
    }

    // ---- the restart budget ----------------------------------------------------------------

    [Fact]
    public async Task TheBudgetStopsRestartingButNeverStopsProbing()
    {
        var time = new FakeClock(DateTimeOffset.UtcNow);
        var (supervisor, probe, control, _) = Build(
            o =>
            {
                o.UnhealthyThreshold = 1;
                o.MaxRestartAttempts = 2;
                o.RestartWindow = TimeSpan.FromMinutes(10);
            },
            time);

        // An Ollama that cannot start: every attempt fails, so readiness is never waited for.
        control.StartResult = ProcessControlResult.Failed("exit code 1");
        probe.Default = BackendHealth.Unreachable;

        await Tick(supervisor, 5);

        Assert.Equal(2, control.Calls.Count(call => call == "start"));

        // Probing continues past the budget — giving up on restarting is not giving up on
        // recovering.
        Assert.Equal(5, probe.Calls);

        var recoveries = 0;
        supervisor.Recovered += () => recoveries++;
        probe.Default = BackendHealth.Healthy;
        await supervisor.TickAsync(CancellationToken.None);

        Assert.Equal(1, recoveries);
        Assert.Equal(BackendHealth.Healthy, supervisor.Health);
    }

    [Fact]
    public async Task TheBudgetRefillsOnceTheWindowHasPassed()
    {
        var time = new FakeClock(DateTimeOffset.UtcNow);
        var (supervisor, probe, control, _) = Build(
            o =>
            {
                o.UnhealthyThreshold = 1;
                o.MaxRestartAttempts = 2;
                o.RestartWindow = TimeSpan.FromMinutes(10);
            },
            time);

        control.StartResult = ProcessControlResult.Failed("exit code 1");
        probe.Default = BackendHealth.Unreachable;

        await Tick(supervisor, 3);
        Assert.Equal(2, control.Calls.Count(call => call == "start"));

        time.Advance(TimeSpan.FromMinutes(11));
        await supervisor.TickAsync(CancellationToken.None);

        Assert.Equal(3, control.Calls.Count(call => call == "start"));
    }

    // ---- readiness -------------------------------------------------------------------------

    [Fact]
    public async Task RecoveryIsAnnouncedOnlyOnceAProbeActuallySucceeds()
    {
        var (supervisor, probe, _, _) = Build(o =>
        {
            o.UnhealthyThreshold = 1;
            o.ReadyTimeout = TimeSpan.FromSeconds(5);
        });

        var recoveries = 0;
        supervisor.Recovered += () => recoveries++;

        // Down, restarted, then two more failures before the model finishes loading.
        probe.Returns(
            BackendHealth.Unreachable,
            BackendHealth.Unreachable,
            BackendHealth.Unreachable,
            BackendHealth.Healthy);

        await supervisor.TickAsync(CancellationToken.None);

        Assert.Equal(1, recoveries);
        Assert.Equal(BackendHealth.Healthy, supervisor.Health);
    }

    [Fact]
    public async Task AReadyTimeoutIsALoggedFailureRatherThanAHang()
    {
        var (supervisor, probe, _, _) = Build(o =>
        {
            o.UnhealthyThreshold = 1;
            o.ReadyTimeout = TimeSpan.FromMilliseconds(30);
            o.ProbeTimeout = TimeSpan.FromMilliseconds(1);
        });

        probe.Default = BackendHealth.Unreachable;

        await supervisor.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(BackendHealth.Unreachable, supervisor.Health);
    }

    // ---- auto-install ----------------------------------------------------------------------

    [Fact]
    public async Task AutoInstallOffNeverInstallsEvenWithNothingOnTheBox()
    {
        var (supervisor, probe, control, installer) = Build(o =>
        {
            o.UnhealthyThreshold = 1;
            o.AutoInstall = false;
        });

        control.Installation = OllamaInstallation.Missing;
        probe.Default = BackendHealth.Unreachable;

        await Tick(supervisor, 3);

        Assert.Equal(0, installer.Calls);
        Assert.DoesNotContain("start", control.Calls);
    }

    [Fact]
    public async Task AutoInstallInstallsOnMissingAndStartsWhatItInstalled()
    {
        var (supervisor, probe, control, installer) = Build(o =>
        {
            o.UnhealthyThreshold = 1;
            o.AutoInstall = true;
        });

        control.Installation = OllamaInstallation.Missing;
        installer.OnInstall = () => control.Installation = OllamaInstallation.Binary("/usr/local/bin/ollama");
        probe.Returns(BackendHealth.Unreachable);
        probe.Default = BackendHealth.Healthy;

        await Tick(supervisor, 3);

        Assert.Equal(1, installer.Calls);
        Assert.Contains("start", control.Calls);
    }

    [Fact]
    public async Task AutoInstallIsNeverReachedFromTheRestartPath()
    {
        var (supervisor, probe, control, installer) = Build(o =>
        {
            o.UnhealthyThreshold = 1;
            o.AutoInstall = true;
            o.ReadyTimeout = TimeSpan.FromMilliseconds(20);
        });

        // Installed, just not answering. Install is a diagnosis, not a retry.
        control.Installation = OllamaInstallation.Binary("/usr/local/bin/ollama");
        probe.Default = BackendHealth.Unreachable;

        await Tick(supervisor, 3);

        Assert.Equal(0, installer.Calls);
    }

    [Fact]
    public async Task AFailedInstallIsNotRetried()
    {
        var (supervisor, probe, control, installer) = Build(o =>
        {
            o.UnhealthyThreshold = 1;
            o.AutoInstall = true;
        });

        control.Installation = OllamaInstallation.Missing;
        installer.Result = ProcessControlResult.Failed("404 from the mirror");
        probe.Default = BackendHealth.Unreachable;

        await Tick(supervisor, 5);

        Assert.Equal(1, installer.Calls);
    }

    // ---- events ----------------------------------------------------------------------------

    [Fact]
    public async Task RecoveredFiresExactlyOncePerOutage()
    {
        var (supervisor, probe, _, _) = Build(o => o.UnhealthyThreshold = 2);

        var recoveries = 0;
        supervisor.Recovered += () => recoveries++;

        probe.Returns(
            BackendHealth.Unreachable, BackendHealth.Unreachable,  // outage one, restarted
            BackendHealth.Healthy,                                 // readiness
            BackendHealth.Healthy, BackendHealth.Healthy,          // two more healthy ticks
            BackendHealth.Wedged, BackendHealth.Wedged,            // outage two
            BackendHealth.Healthy);                                // readiness

        await Tick(supervisor, 6);

        Assert.Equal(2, recoveries);
    }

    [Fact]
    public async Task RestartingIsRaisedBeforeTheRestartAndCarriesTheState()
    {
        var (supervisor, probe, _, _) = Build(o => o.UnhealthyThreshold = 1);

        var observed = new List<BackendHealth>();
        supervisor.Restarting += observed.Add;

        probe.Returns(BackendHealth.Wedged);
        probe.Default = BackendHealth.Healthy;

        await supervisor.TickAsync(CancellationToken.None);

        Assert.Equal([BackendHealth.Wedged], observed);
    }

    [Fact]
    public async Task ASubscriberThatThrowsDoesNotBreakTheSupervisor()
    {
        var (supervisor, probe, control, _) = Build(o => o.UnhealthyThreshold = 1);
        supervisor.Restarting += _ => throw new InvalidOperationException("subscriber is broken");

        probe.Returns(BackendHealth.Unreachable);
        probe.Default = BackendHealth.Healthy;

        await supervisor.TickAsync(CancellationToken.None);

        Assert.Contains("start", control.Calls);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static async Task Tick(OllamaSupervisor supervisor, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await supervisor.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private static (OllamaSupervisor Supervisor, FakeProbe Probe, FakeControl Control, FakeInstaller Installer)
        Build(Action<OllamaSupervisorOptions>? configure = null, TimeProvider? time = null)
    {
        var options = new OllamaSupervisorOptions
        {
            Enabled = true,
            ProbeInterval = TimeSpan.FromSeconds(1),
            ProbeTimeout = TimeSpan.FromMilliseconds(1),
            ReadyTimeout = TimeSpan.FromSeconds(5),
            RestartBackoff = TimeSpan.FromMilliseconds(1)
        };

        configure?.Invoke(options);

        var probe = new FakeProbe();
        var control = new FakeControl();
        var installer = new FakeInstaller();

        var supervisor = new OllamaSupervisor(
            Options.Create(options),
            Options.Create(new OllamaOptions()),
            probe,
            control,
            installer,
            time ?? TimeProvider.System,
            NullLogger<OllamaSupervisor>.Instance);

        return (supervisor, probe, control, installer);
    }

    private static OllamaProbe StubProbe(HttpStatusCode status, string body)
        => new(new SingleClientFactory(new HttpClient(new StubHandler(status, body))
        {
            BaseAddress = new Uri("http://localhost:11434/")
        }));

    private static OllamaProbe RealProbe(string endpoint, TimeSpan probeTimeout)
        => new(new SingleClientFactory(new HttpClient(OllamaProbe.CreateHandler(probeTimeout))
        {
            BaseAddress = new Uri(endpoint),
            Timeout = probeTimeout
        }));

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class FakeProbe : IOllamaProbe
    {
        private readonly Queue<BackendHealth> scripted = new();

        public BackendHealth Default { get; set; } = BackendHealth.Unreachable;

        public int Calls { get; private set; }

        public void Returns(params BackendHealth[] results)
        {
            foreach (var result in results)
            {
                scripted.Enqueue(result);
            }
        }

        public Task<BackendHealth> CheckAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(scripted.Count > 0 ? scripted.Dequeue() : Default);
        }
    }

    private sealed class FakeControl : IOllamaProcessControl
    {
        public OllamaInstallation Installation { get; set; } = OllamaInstallation.Binary("/usr/local/bin/ollama");

        public ProcessControlResult StartResult { get; set; } = ProcessControlResult.Ok;

        public ProcessControlResult StopResult { get; set; } = ProcessControlResult.Ok;

        public List<string> Calls { get; } = [];

        public List<OllamaInstallation> Targets { get; } = [];

        public Task<OllamaInstallation> DiscoverAsync(CancellationToken cancellationToken)
        {
            Calls.Add("discover");
            return Task.FromResult(Installation);
        }

        public Task<ProcessControlResult> StartAsync(OllamaInstallation installation, CancellationToken cancellationToken)
        {
            Calls.Add("start");
            Targets.Add(installation);
            return Task.FromResult(StartResult);
        }

        public Task<ProcessControlResult> StopAsync(OllamaInstallation installation, CancellationToken cancellationToken)
        {
            Calls.Add("stop");
            Targets.Add(installation);
            return Task.FromResult(StopResult);
        }

        public Task<bool> IsInstalledAsync(CancellationToken cancellationToken)
            => Task.FromResult(Installation.Kind is not OllamaInstallKind.Missing);
    }

    private sealed class FakeInstaller : IOllamaInstaller
    {
        public int Calls { get; private set; }

        public ProcessControlResult Result { get; set; } = ProcessControlResult.Ok;

        public Action? OnInstall { get; set; }

        public Task<ProcessControlResult> InstallAsync(CancellationToken cancellationToken)
        {
            Calls++;
            OnInstall?.Invoke();
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan delta) => now += delta;
    }
}
