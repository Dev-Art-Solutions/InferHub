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

    // ---- the tools image (phase 42, D3) ------------------------------------------------------

    [Fact]
    public void NeitherOfTheOlderImagesLearnedAboutPython()
    {
        // The third image is a third image for phase-39 D2's reason, restated: ~1.5 GB of Python
        // wheels are in a layer whether a flag is on or off, so a flag would grow every existing
        // deployment for a feature it does not use.
        foreach (var name in new[] { "Dockerfile", "Dockerfile.ollama" })
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "InferHub.Node", name));

            Assert.DoesNotContain("python", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("requirements-tools", text);
            Assert.DoesNotContain("ffmpeg", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheToolsImageEnablesBothToolOptInsAndTheDownloadConsent()
    {
        var dockerfile = ToolsInstructions();

        // Opt in twice, then a third time for the reach onto the internet (phase-41 D2,
        // phase-42 D4). None of the three is redundant with the others, and this image is the one
        // place where all three are legitimately on.
        Assert.Contains("ENV Tools__Enabled=true", dockerfile);
        Assert.Contains("ENV Tools__Allowed__0=whisper", dockerfile);
        Assert.Contains("ENV Tools__Allowed__1=piper", dockerfile);
        Assert.Contains("ENV Tools__AllowModelDownload=true", dockerfile);

        // The permissions trap, for the sixth time. Every path this image writes to has to be
        // under /data, and /data has to exist in the image and be owned by app.
        Assert.Contains("ENV Tools__ScratchDirectory=/data/tools/scratch", dockerfile);
        Assert.Contains("/data/tools/hf", dockerfile);
        Assert.Contains("/data/tools/voices", dockerfile);
        Assert.Contains("chown -R app:app /data", dockerfile);
        Assert.Contains("USER app", dockerfile);
    }

    [Fact]
    public void TheToolsImagePinsTheSameOllamaAsTheBundledOne()
    {
        // Two images, one engine version. Bumping one and not the other means `:ollama` and
        // `:tools` of the same tag contain different Ollamas, which is phase-39 D9's question with
        // one more way to get it wrong.
        Assert.Equal(OllamaVersionOf(BundledDockerfile()), OllamaVersionOf(ToolsDockerfile()));
        Assert.Matches(@"ARG OLLAMA_SHA256=[0-9a-f]{64}", ToolsDockerfile());
    }

    // ---- the diffusion image (phase 46, D9) --------------------------------------------------

    [Fact]
    public void TheDiffusionImageDoesNotStackOnTheOtherThree()
    {
        var dockerfile = DiffusionInstructions();

        // D9, and it is the decision most likely to be argued with. The other three node images
        // stack — :ollama is the plain one plus an engine, :tools is :ollama plus two workers. This
        // one starts from the plain runtime again, because stacking reaches ~15 GB and because a
        // card running a diffusion pipeline has no room for a chat model beside it: bundling Ollama
        // would ship a combination the docs would then have to tell people not to use. Two
        // containers and capability routing is the answer, which is what phase 40 was built for.
        Assert.DoesNotContain("ollama", dockerfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("whisper", dockerfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("piper", dockerfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requirements-tools", dockerfile);
    }

    [Fact]
    public void NoneOfTheOlderImagesLearnedAboutTorch()
    {
        // The same argument phase 42 made about Python, one image further out: several gigabytes of
        // CUDA wheels are in a layer whether a flag is on or off.
        foreach (var name in new[] { "Dockerfile", "Dockerfile.ollama", "Dockerfile.tools" })
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "InferHub.Node", name));

            Assert.DoesNotContain("torch", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("diffusers", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("requirements-diffusion", text);
        }
    }

    [Fact]
    public void TheDiffusionImageEnablesItsOptInsAndKeepsRequireGpuOn()
    {
        var dockerfile = DiffusionInstructions();

        Assert.Contains("ENV Tools__Enabled=true", dockerfile);
        Assert.Contains("ENV Tools__Allowed__0=diffusion", dockerfile);
        Assert.Contains("ENV Tools__AllowModelDownload=true", dockerfile);
        Assert.Contains("ENV Tools__Image__RecipeDirectory=/opt/inferhub/recipes", dockerfile);

        // The fourth opt-in is a REFUSAL rather than a grant, and it stays on in the image whose
        // whole purpose needs a card: a tool that loads happily on a CPU and then serves
        // four-minute requests is a node the fleet keeps routing to, and every caller pays for the
        // discovery (phase-46 D7).
        Assert.Contains("ENV Tools__Image__RequireGpu=true", dockerfile);

        // The permissions trap, for the seventh time.
        Assert.Contains("ENV Tools__ScratchDirectory=/data/tools/scratch", dockerfile);
        Assert.Contains("/data/tools/hf", dockerfile);
        Assert.Contains("chown -R app:app /data", dockerfile);
        Assert.Contains("USER app", dockerfile);

        // `compute`, not the runtime's default of `utility` — which gives a working nvidia-smi and
        // NO libcuda, so every diagnostic looks right and everything runs on the CPU.
        Assert.Contains("NVIDIA_DRIVER_CAPABILITIES=compute,utility", dockerfile);
    }

    /// <summary>
    /// The v3.10.0 bug, asserted as a build step rather than remembered: a venv built by one Python
    /// minor and run by another imports nothing, every manifest still loads, <c>/api/status</c> still
    /// answers, and the first generation dies on an import. The image now proves its own venv at
    /// build time, so that failure cannot be published again.
    /// </summary>
    [Fact]
    public void TheDiffusionImageAssertsItsVenvImportsAtBuildTime()
    {
        var dockerfile = DiffusionInstructions();

        Assert.Contains("python3 -m venv /opt/inferhub/venv", dockerfile);
        Assert.Contains("import torch, diffusers, transformers, PIL, inferhub_worker", dockerfile);
    }

    /// <summary>
    /// Every shipped recipe pins a commit sha. Without it, "which weights were in 3.14.0" has no
    /// answer and two builds of the same tag can contain different models — phase-39 D9's question,
    /// asked of a Hugging Face repo instead of a tarball.
    /// </summary>
    [Fact]
    public void EveryShippedRecipePinsARevisionAndNamesItsLicence()
    {
        var recipes = Directory.GetFiles(Path.Combine(RepoRoot(), "python", "recipes"), "*.json");

        Assert.NotEmpty(recipes);

        foreach (var path in recipes)
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var name = Path.GetFileName(path);

            Assert.True(root.TryGetProperty("id", out _), $"{name} has no id");
            Assert.True(root.TryGetProperty("repo", out _), $"{name} has no repo");

            var revision = root.GetProperty("revision").GetString();
            Assert.Matches("^[0-9a-f]{40}$", revision);

            Assert.True(root.TryGetProperty("license", out var licence), $"{name} names no licence");
            Assert.False(string.IsNullOrWhiteSpace(licence.GetProperty("id").GetString()));

            // A list, not a range: SDXL was trained on fixed aspect buckets, and a size outside them
            // does not fail — it produces duplicated limbs, which reads as a bad model.
            Assert.NotEmpty(root.GetProperty("sizes").EnumerateArray());
        }
    }

    /// <summary>
    /// Rule 5's own assertion for this phase: the Python is a subprocess, not a dependency. No
    /// project file may reference it, and <c>InferHub.Shared.csproj</c> must still be empty.
    /// </summary>
    [Fact]
    public void NoProjectReferencesPythonAndTheSharedProjectIsStillEmpty()
    {
        foreach (var project in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(project);

            Assert.DoesNotContain("Python", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CSnakes", text, StringComparison.OrdinalIgnoreCase);
        }

        var shared = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "InferHub.Shared", "InferHub.Shared.csproj"));

        Assert.DoesNotContain("PackageReference", shared);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static string BundledDockerfile()
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "InferHub.Node", "Dockerfile.ollama"));

    private static string ToolsDockerfile()
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "InferHub.Node", "Dockerfile.tools"));

    private static string DiffusionDockerfile()
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "InferHub.Node", "Dockerfile.diffusion"));

    /// <summary>
    /// The Dockerfile with its comments stripped. Necessary here rather than cosmetic: the header of
    /// <c>Dockerfile.diffusion</c> explains at length why the image has no Ollama in it, so a naive
    /// substring search for "ollama" finds the explanation and fails the assertion it is explaining.
    /// </summary>
    private static string DiffusionInstructions()
        => string.Join(
            '\n',
            DiffusionDockerfile().Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    private static string ToolsInstructions()
        => string.Join(
            '\n',
            ToolsDockerfile().Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    private static string OllamaVersionOf(string dockerfile)
        => System.Text.RegularExpressions.Regex.Match(dockerfile, @"ARG OLLAMA_VERSION=(\S+)").Groups[1].Value;

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
