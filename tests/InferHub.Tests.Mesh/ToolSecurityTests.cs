using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Node.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// The consents and the containment. These are the tests that keep phase-41 D2, D3 and D7 true,
/// and every one of them runs a real child process — a stubbed <c>Process</c> would echo whatever
/// environment the test author already believed in.
/// </summary>
public class ToolSecurityTests
{
    /// <summary>
    /// The load-bearing one. The node's environment holds <c>Auth__NodeEnrollmentSecret</c>,
    /// <c>LocalApi__ApiKeys__0</c> and whatever else the deployment set; a worker that inherited
    /// all of it would be a credential leak that never has to do anything visible to be real.
    /// </summary>
    [Fact]
    public async Task AWorkerDoesNotInheritAVariableTheNodeHasAndTheManifestDidNotName()
    {
        const string secretName = "InferHubTest__NodeEnrollmentSecret";
        const string allowedName = "InferHubTest__Allowed";

        Environment.SetEnvironmentVariable(secretName, "super-secret-enrollment-value");

        try
        {
            using var scratch = new ToolWorkerFixture.TempDirectory();

            var manifest = ToolWorkerFixture.Manifest(
                environment: new Dictionary<string, string> { [allowedName] = "named-in-the-manifest" });

            var options = ToolWorkerFixture.Options(scratch.Path, "echo");
            var pool = new ToolWorkerPool(manifest, options, TimeProvider.System, NullLogger.Instance);

            var executor = new ToolExecutor(
                new PoolRuntime(pool),
                ToolWorkerFixture.Wrap(options),
                NullLogger<ToolExecutor>.Instance);

            var leaked = await Ask(executor, secretName);
            var named = await Ask(executor, allowedName);

            Assert.False(
                leaked.GetProperty("present").GetBoolean(),
                "the node's own environment must not reach a tool worker");

            Assert.True(
                named.GetProperty("present").GetBoolean(),
                "a variable the manifest names must reach the worker, or 'env' is useless");

            Assert.Equal("named-in-the-manifest", named.GetProperty("value").GetString());

            await pool.DisposeAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, null);
        }
    }

    /// <summary>
    /// Phase 42's third opt-in reaches the worker as a <em>stated</em> variable rather than an
    /// inherited one — which is the only way it could, since the child's environment is cleared
    /// first (D3). Whisper auto-downloads its weights on first use, and that is a reach onto the
    /// internet from a box whose operator may have deliberately air-gapped it: with the flag off,
    /// the worker must be able to tell, and it must be told the same way whatever shell the node
    /// was started from.
    /// </summary>
    [Theory]
    [InlineData(true, "1")]
    [InlineData(false, "0")]
    public async Task TheModelDownloadConsentIsStatedIntoTheWorkersEnvironment(bool allow, string expected)
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();

        var options = ToolWorkerFixture.Options(scratch.Path, "echo");
        options.AllowModelDownload = allow;

        var pool = new ToolWorkerPool(
            ToolWorkerFixture.Manifest(),
            options,
            TimeProvider.System,
            NullLogger.Instance);

        var executor = new ToolExecutor(
            new PoolRuntime(pool),
            ToolWorkerFixture.Wrap(options),
            NullLogger<ToolExecutor>.Instance);

        var answer = await Ask(executor, "INFERHUB_ALLOW_MODEL_DOWNLOAD");

        Assert.True(answer.GetProperty("present").GetBoolean());
        Assert.Equal(expected, answer.GetProperty("value").GetString());

        await pool.DisposeAsync();
    }

    [Fact]
    public void ModelDownloadIsOffByDefaultSoNothingEverFetchesByAccident()
        => Assert.False(new InferHub.Node.Configuration.ToolOptions().AllowModelDownload);

    /// <summary>
    /// A worker still needs enough environment to run at all: PATH, HOME and — on Windows, where
    /// this suite runs — the handful the platform requires to start a process.
    /// </summary>
    [Fact]
    public async Task TheShortPassThroughListStillReachesTheWorker()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();

        var options = ToolWorkerFixture.Options(scratch.Path, "echo");
        var pool = new ToolWorkerPool(
            ToolWorkerFixture.Manifest(),
            options,
            TimeProvider.System,
            NullLogger.Instance);

        var executor = new ToolExecutor(
            new PoolRuntime(pool),
            ToolWorkerFixture.Wrap(options),
            NullLogger<ToolExecutor>.Instance);

        var path = await Ask(executor, "PATH");
        Assert.True(path.GetProperty("present").GetBoolean());

        await pool.DisposeAsync();
    }

    [Fact]
    public async Task AManifestThatIsNotInToolsAllowedIsLoadedLoggedAndNeverStarted()
    {
        using var manifests = new ToolWorkerFixture.TempDirectory("inferhub-manifests");
        using var scratch = new ToolWorkerFixture.TempDirectory();

        manifests.WriteManifest("echo.json", new
        {
            id = "echo",
            capabilities = new[] { new { kind = "echo", models = new[] { "echo" } } },
            command = ToolWorkerFixture.Command()
        });

        var options = ToolWorkerFixture.Options(scratch.Path); // Allowed is deliberately empty
        options.ManifestDirectory = manifests.Path;

        var captured = new CapturingLoggerProvider();
        using var factory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(captured));

        var runtime = new ProcessToolRuntime(
            ToolWorkerFixture.Wrap(options),
            TimeProvider.System,
            factory,
            factory.CreateLogger<ProcessToolRuntime>());

        await runtime.StartAsync(CancellationToken.None);

        Assert.Empty(runtime.Capabilities);
        Assert.True(
            captured.Contains("is not in Tools:Allowed"),
            "a manifest that is present but not allowed must say so — 'I put the file there and nothing happened' is otherwise a silent afternoon");

        await Assert.ThrowsAnyAsync<Exception>(
            () => runtime.AcquireAsync("echo", "echo", CancellationToken.None));

        await runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ToolsOffRegistersTheNoOpRuntimeAndNeverSpawnsAnything()
    {
        var runtime = new NoToolRuntime();

        Assert.False(runtime.Enabled);
        Assert.Empty(runtime.Capabilities);
        await Assert.ThrowsAnyAsync<Exception>(
            () => runtime.AcquireAsync("transcribe", "whisper-small", CancellationToken.None));
    }

    /// <summary>
    /// A worker naming a path outside its own scratch directory is either confused or hostile, and
    /// the difference does not matter: reading it would turn "a tool ran" into "a tool exfiltrated
    /// a file through the client-facing API".
    /// </summary>
    [Fact]
    public async Task AFileOutsideTheScratchDirectoryIsRefused()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        using var elsewhere = new ToolWorkerFixture.TempDirectory("inferhub-elsewhere");

        var secret = Path.Combine(elsewhere.Path, "authorized_keys");
        await File.WriteAllTextAsync(secret, "ssh-rsa AAAA...");

        var options = ToolWorkerFixture.Options(scratch.Path, "echo");
        var pool = new ToolWorkerPool(
            ToolWorkerFixture.Manifest(),
            options,
            TimeProvider.System,
            NullLogger.Instance);

        var executor = new ToolExecutor(
            new PoolRuntime(pool),
            ToolWorkerFixture.Wrap(options),
            NullLogger<ToolExecutor>.Instance);

        var payload = JsonSerializer.Serialize(new
        {
            model = "echo",
            behaviour = "escape",
            path = secret
        });

        var result = await executor.RunAsync(
            new InferHub.Shared.Contracts.ToolJob(Guid.NewGuid(), "echo", "echo", payload),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("outside its scratch directory", result.Error);

        // ...and the file is untouched, which is the actual claim.
        Assert.Equal("ssh-rsa AAAA...", await File.ReadAllTextAsync(secret));

        await pool.DisposeAsync();
    }

    [Fact]
    public async Task AnAttachmentOverTheCapIsRefusedBeforeItReachesAWorker()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();

        var options = ToolWorkerFixture.Options(scratch.Path, "echo");
        options.MaxAttachmentBytes = 16;

        var pool = new ToolWorkerPool(
            ToolWorkerFixture.Manifest(),
            options,
            TimeProvider.System,
            NullLogger.Instance);

        var executor = new ToolExecutor(
            new PoolRuntime(pool),
            ToolWorkerFixture.Wrap(options),
            NullLogger<ToolExecutor>.Instance);

        var result = await executor.RunAsync(
            new InferHub.Shared.Contracts.ToolJob(
                Guid.NewGuid(),
                "echo",
                "echo",
                "{}",
                [new InferHub.Shared.Contracts.ToolAttachment("big.bin", "application/octet-stream", new byte[64])]),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("over the 16-byte limit", result.Error);

        await pool.DisposeAsync();
    }

    [Fact]
    public void AllowingToolsWithoutEnablingThemIsAStartupFailureRatherThanASilentNoOp()
    {
        var validator = new ToolOptionsValidator();

        var result = validator.Validate(null, new ToolOptions
        {
            Enabled = false,
            Allowed = ["whisper"]
        });

        Assert.True(result.Failed);
        Assert.Contains("Enabled", result.FailureMessage);
    }

    /// <summary>
    /// Blanking an entry is how an operator removes one that came from an image's environment —
    /// there is no other way, because <c>-e Tools__Allowed__1=</c> is the only lever
    /// <c>docker run</c> gives you over an array element.
    /// </summary>
    /// <remarks>
    /// Found by trying to run the published <c>:tools</c> image as an ordinary chat node:
    /// <c>-e Tools__Enabled=false</c> failed startup with "Allowed names 2 tool(s)", and no second
    /// flag existed that would have helped. Nothing is hidden by accepting a blank — a manifest
    /// that is not in the list is still loaded and still logged by name as "not started", which is
    /// the signal the strict check was standing in for.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABlankAllowedEntryIsHowYouClearOneAnImageNamed(bool enabled)
    {
        var result = new ToolOptionsValidator().Validate(null, new ToolOptions
        {
            Enabled = enabled,
            Allowed = ["", "  "]
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void TheDefaultOptionsAreOffAndValid()
    {
        var result = new ToolOptionsValidator().Validate(null, new ToolOptions());

        Assert.True(result.Succeeded);
        Assert.False(new ToolOptions().Enabled);
    }

    private static async Task<JsonElement> Ask(ToolExecutor executor, string variable)
    {
        var payload = JsonSerializer.Serialize(new { model = "echo", behaviour = "env", name = variable });

        var result = await executor.RunAsync(
            new InferHub.Shared.Contracts.ToolJob(Guid.NewGuid(), "echo", "echo", payload),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        return JsonDocument.Parse(result.Payload!).RootElement;
    }

    private sealed class PoolRuntime(ToolWorkerPool pool) : IToolRuntime
    {
        public bool Enabled => true;

        public IReadOnlyList<InferHub.Shared.Contracts.NodeCapability> Capabilities => pool.Capabilities;

        public event Action? CapabilitiesChanged { add { } remove { } }

        public InferHub.Shared.Contracts.NodeToolState State(string nodeId)
            => new(nodeId, Enabled: true, [pool.Report()], DateTimeOffset.UtcNow);

        public Task<ToolWorkerLease> AcquireAsync(string capability, string model, CancellationToken cancellationToken)
            => pool.AcquireAsync(cancellationToken);

        public Task<ToolWorkerLease> AcquireToolAsync(string toolId, CancellationToken cancellationToken)
            => pool.AcquireAsync(cancellationToken);

        public IReadOnlyList<string> ToolIds => [pool.Manifest.Id];

        public IReadOnlyList<ImageRecipeInfo> ImageRecipes => [];

        public void SetDisabledModels(IReadOnlyCollection<string> models) { }

        public Task SetDisabledToolsAsync(IReadOnlyCollection<string> toolIds, CancellationToken cancellationToken)
            => toolIds.Contains(pool.Manifest.Id, StringComparer.OrdinalIgnoreCase)
                ? pool.SuspendAsync()
                : pool.ResumeAsync(cancellationToken);
    }

}
