using InferHub.Node.Backends;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 39 — the bundled node image: a node and its Ollama in one container, with a GPU or
/// without one, or with no inference process at all.
/// </summary>
/// <remarks>
/// <para>
/// Two of these read the <c>Dockerfile.ollama</c> itself, which is unusual and deliberate. A
/// packaging phase's real acceptance criteria live in an artifact no unit test can reach, and this
/// repo has shipped four images that were dead on arrival while every test was green. The two
/// lines pinned here are the ones whose loss produces a *silently* wrong image — one that starts,
/// answers, and is fifty times slower than it should be — which is the failure nobody notices.
/// The same reasoning as <c>KestrelsWildcardAddressesAreValidAddresses</c> in phase 37.
/// </para>
/// </remarks>
public class BundledNodeTests
{
    // ---- the CUDA probe (D5) ----------------------------------------------------------------

    [Fact]
    public void TheProbeAnswersWithoutThrowingWhereThereIsNoDriver()
    {
        // Build agents have no NVIDIA driver, and neither do most developer machines. The
        // contract that matters is that asking is *always* safe: this runs on every node at
        // startup, and a probe that threw would turn a diagnostic into an outage.
        var devices = CudaDeviceProbe.Detect();

        if (!devices.Available)
        {
            Assert.Empty(devices.Names);
            Assert.Equal(0, devices.Count);
        }
        else
        {
            // A machine that does have one: the report must be self-consistent rather than
            // "available with nothing in it".
            Assert.NotEmpty(devices.Names);
            Assert.Equal(devices.Names.Count, devices.Count);
            Assert.All(devices.Names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        }
    }

    [Fact]
    public void TheProbeIsAnsweredOnceAndCached()
    {
        // Nothing hot-plugs a GPU into a running container, and /api/status must not dlopen the
        // driver per request.
        Assert.Equal(CudaDeviceProbe.Current.Available, CudaDeviceProbe.Current.Available);
        Assert.Same(CudaDeviceProbe.Current.Names, CudaDeviceProbe.Current.Names);
    }

    // ---- report vs refusal (D6) -------------------------------------------------------------

    [Fact]
    public async Task WithNoGpuAndNoRequirementTheNodeStartsAnyway()
    {
        // The heart of the phase. CPU is a supported mode — embedding models, small models and a
        // vector-store-only box all run on it — so an image that refused here would make two of
        // its three documented modes impossible.
        var report = new GpuReport(Options.Create(new OllamaOptions()), NullLogger<GpuReport>.Instance);

        await report.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RequireGpuWithNoGpuFailsStartupAndNamesTheFlagAndTheKey()
    {
        if (CudaDeviceProbe.Current.Available)
        {
            // On a machine with a card the refusal cannot fire, and asserting the opposite would
            // make this suite fail on exactly the hardware the phase targets.
            return;
        }

        var report = new GpuReport(
            Options.Create(new OllamaOptions { RequireGpu = true }),
            NullLogger<GpuReport>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => report.StartAsync(CancellationToken.None));

        Assert.Contains("Ollama:RequireGpu", error.Message);
        Assert.Contains("--gpus all", error.Message);
    }

    [Fact]
    public void RequireGpuDefaultsToFalseSoNothingEverRefusesByAccident()
        => Assert.False(new OllamaOptions().RequireGpu);

    // ---- start at boot (D4) -----------------------------------------------------------------

    [Fact]
    public async Task StartAtBootStartsOllamaWhenNothingIsListening()
    {
        var (supervisor, probe, control) = Build(o => o.StartAtBoot = true);
        probe.Next = BackendHealth.Unreachable;

        await supervisor.StartAtBootAsync(CancellationToken.None);

        Assert.Contains("start", control.Calls);
    }

    [Fact]
    public async Task StartAtBootDoesNotTouchAWedgedOllama()
    {
        // UnhealthyThreshold exists to avoid misdiagnosing a process that is running-but-slow.
        // Something already answering the port at boot is exactly where guessing costs the most:
        // starting it fails on a bound port and the log then blames the wrong thing.
        var (supervisor, probe, control) = Build(o => o.StartAtBoot = true);
        probe.Next = BackendHealth.Wedged;

        await supervisor.StartAtBootAsync(CancellationToken.None);

        Assert.DoesNotContain("start", control.Calls);
        Assert.DoesNotContain("stop", control.Calls);
    }

    [Fact]
    public async Task StartAtBootLeavesAHealthyOllamaAlone()
    {
        var (supervisor, probe, control) = Build(o => o.StartAtBoot = true);
        probe.Next = BackendHealth.Healthy;

        await supervisor.StartAtBootAsync(CancellationToken.None);

        Assert.Empty(control.Calls);
        Assert.Equal(BackendHealth.Healthy, supervisor.Health);
    }

    // ---- stop on shutdown (D4) --------------------------------------------------------------

    [Fact]
    public async Task ShutdownStopsOnlyTheOllamaThisNodeSpawned()
    {
        var (supervisor, _, control) = Build(o => o.StopOnShutdown = true);

        await supervisor.StopAsync(CancellationToken.None);

        // StopSpawnedAsync, never StopAsync: the by-name sweep is for remedying a wedge, and on
        // the way out anything we did not start is somebody else's server.
        Assert.Contains("stop-spawned", control.Calls);
        Assert.DoesNotContain("stop", control.Calls);
    }

    [Fact]
    public async Task ShutdownLeavesOllamaRunningByDefault()
    {
        var (supervisor, _, control) = Build();

        await supervisor.StopAsync(CancellationToken.None);

        Assert.Empty(control.Calls);
    }

    [Fact]
    public void BothNewSupervisorKeysDefaultToTodaysBehaviour()
    {
        var options = new OllamaSupervisorOptions();

        Assert.False(options.StartAtBoot);
        Assert.False(options.StopOnShutdown);
    }

    // ---- the Dockerfile (D6, D9) ------------------------------------------------------------

    [Fact]
    public void TheBundledImageAsksForTheComputeDriverCapability()
    {
        // THE trap of this phase. The NVIDIA container runtime's default capability set is
        // `utility`, which injects nvidia-smi and NOT libcuda — an image where every diagnostic
        // looks right and inference silently runs on the CPU.
        var dockerfile = BundledDockerfile();

        Assert.Contains("NVIDIA_DRIVER_CAPABILITIES=compute", dockerfile);
        Assert.Contains("NVIDIA_VISIBLE_DEVICES=all", dockerfile);
    }

    [Fact]
    public void TheBundledImagePinsAndVerifiesItsOllama()
    {
        // A floating `latest` means two builds of the same InferHub tag contain different
        // inference engines, which makes "it worked in 3.7.0" unanswerable.
        var dockerfile = BundledDockerfile();

        Assert.Matches(@"ARG OLLAMA_VERSION=\d+\.\d+\.\d+", dockerfile);
        Assert.Matches(@"ARG OLLAMA_SHA256=[0-9a-f]{64}", dockerfile);
        Assert.Contains("sha256sum -c -", dockerfile);
    }

    [Fact]
    public void TheBundledImageDoesNotForceAGpuOrPublishOllamasPort()
    {
        // Comments are stripped: this file explains at length why it does *not* do these things,
        // and a substring match over the prose would fail on the explanation.
        var dockerfile = BundledInstructions();

        // Setting either of these would take away a documented mode: CPU inference, and the
        // guarantee that the container's only surface is the API that requires a key.
        Assert.DoesNotContain("Ollama__RequireGpu", dockerfile);
        Assert.DoesNotContain("EXPOSE", dockerfile);

        // Mode 3 (vector store only) is one key, and it only works because the supervisor is the
        // sole thing in the image that would ever start Ollama.
        Assert.Contains("ENV Ollama__Supervisor__Enabled=true", dockerfile);

        // The model store has to be on the volume, or every `docker run` re-downloads gigabytes.
        Assert.Contains("ENV OLLAMA_MODELS=/data/ollama", dockerfile);
    }

    [Fact]
    public void ThePlainNodeImageStaysFreeOfOllama()
    {
        // Rule 5 survives only because this is a *second artifact*. If the bundle ever leaks into
        // the plain image, every coordinator+node compose stack grows by 4 GB for nothing.
        var plain = File.ReadAllText(Path.Combine(RepoRoot(), "src", "InferHub.Node", "Dockerfile"));

        Assert.DoesNotContain("ollama.com/download", plain);
        Assert.DoesNotContain("NVIDIA", plain);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static string BundledDockerfile()
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "InferHub.Node", "Dockerfile.ollama"));

    private static string BundledInstructions()
        => string.Join(
            '\n',
            BundledDockerfile()
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith('#')));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InferHub.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static (OllamaSupervisor Supervisor, FakeProbe Probe, FakeControl Control) Build(
        Action<OllamaSupervisorOptions>? configure = null)
    {
        var options = new OllamaSupervisorOptions
        {
            Enabled = true,
            ProbeInterval = TimeSpan.FromSeconds(1),
            ProbeTimeout = TimeSpan.FromMilliseconds(1),
            ReadyTimeout = TimeSpan.FromMilliseconds(50),
            RestartBackoff = TimeSpan.FromMilliseconds(1)
        };

        configure?.Invoke(options);

        var probe = new FakeProbe();
        var control = new FakeControl();

        var supervisor = new OllamaSupervisor(
            Options.Create(options),
            Options.Create(new OllamaOptions()),
            probe,
            control,
            new FakeInstaller(),
            TimeProvider.System,
            NullLogger<OllamaSupervisor>.Instance);

        return (supervisor, probe, control);
    }

    private sealed class FakeProbe : IOllamaProbe
    {
        public BackendHealth Next { get; set; } = BackendHealth.Healthy;

        public Task<BackendHealth> CheckAsync(CancellationToken cancellationToken)
            => Task.FromResult(Next);
    }

    private sealed class FakeControl : IOllamaProcessControl
    {
        public List<string> Calls { get; } = [];

        public Task<OllamaInstallation> DiscoverAsync(CancellationToken cancellationToken)
            => Task.FromResult(OllamaInstallation.Binary("/usr/local/bin/ollama"));

        public Task<ProcessControlResult> StartAsync(OllamaInstallation installation, CancellationToken cancellationToken)
        {
            Calls.Add("start");
            return Task.FromResult(ProcessControlResult.Ok);
        }

        public Task<ProcessControlResult> StopAsync(OllamaInstallation installation, CancellationToken cancellationToken)
        {
            Calls.Add("stop");
            return Task.FromResult(ProcessControlResult.Ok);
        }

        public Task<ProcessControlResult> StopSpawnedAsync(CancellationToken cancellationToken)
        {
            Calls.Add("stop-spawned");
            return Task.FromResult(ProcessControlResult.Ok);
        }

        public Task<bool> IsInstalledAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FakeInstaller : IOllamaInstaller
    {
        public Task<ProcessControlResult> InstallAsync(CancellationToken cancellationToken)
            => Task.FromResult(ProcessControlResult.Ok);
    }
}
