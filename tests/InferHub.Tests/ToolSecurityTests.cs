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

        public Task<ToolWorkerLease> AcquireAsync(string capability, string model, CancellationToken cancellationToken)
            => pool.AcquireAsync(cancellationToken);
    }

}
