using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Tools;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 48, D4. Weights are pulled by an explicit command on phase 26's channel — never lazily
/// inside a request.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug this replaces is a shipped one.</b> v3.14.0 fetched on first use inside
/// <c>requestTimeoutSeconds</c>: on a fresh volume the first <c>sdxl</c> call spent 900 seconds
/// downloading and then returned 502, twice. FLUX is twice that on the wire and Qwen-Image is more,
/// so the only honest shape is an operator action with progress on a channel built for minutes.
/// </para>
/// <para>
/// It runs against the <b>real</b> echo worker over a real child process, for phase-41's reason: a
/// stub would answer whatever the test author already believed, and what is being checked here is
/// that a worker's own words reach a coordinator's progress stream.
/// </para>
/// </remarks>
public class ToolModelCommandTests
{
    private static ToolManifest DiffusionLike(string id = "diffusion") =>
        ToolWorkerFixture.Manifest(id, kind: CapabilityKinds.Image, models: ["sdxl", "flux-schnell"]);

    private static async Task<(ProcessToolRuntime Runtime, ToolExecutor Executor)> StartAsync(
        ToolWorkerFixture.TempDirectory scratch,
        ToolWorkerFixture.TempDirectory manifests,
        string toolId = "diffusion")
    {
        manifests.WriteManifest($"{toolId}.json", new
        {
            id = toolId,
            capabilities = new[] { new { kind = CapabilityKinds.Image, models = new[] { "sdxl", "flux-schnell" } } },
            command = ToolWorkerFixture.Command(),
            maxWorkers = 1
        });

        var options = ToolWorkerFixture.Options(scratch.Path, toolId);
        options.ManifestDirectory = manifests.Path;

        var runtime = new ProcessToolRuntime(
            ToolWorkerFixture.Wrap(options),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            NullLogger<ProcessToolRuntime>.Instance);

        await runtime.StartAsync(CancellationToken.None);

        var executor = new ToolExecutor(
            runtime,
            ToolWorkerFixture.Wrap(options),
            NullLogger<ToolExecutor>.Instance);

        return (runtime, executor);
    }

    /// <summary>A pull streams the worker's own words and ends with exactly one terminal frame.</summary>
    [Fact]
    public async Task APullStreamsProgressAndEndsReady()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        using var manifests = new ToolWorkerFixture.TempDirectory("inferhub-manifests");
        var (runtime, executor) = await StartAsync(scratch, manifests);

        await using var _ = runtime;

        var steps = await executor
            .ManageModelAsync("diffusion", ModelCommand.KindPull, "flux-schnell", CancellationToken.None)
            .ToListAsync();

        Assert.Equal(1, steps.Count(step => step.Done));
        Assert.Same(steps[^1], steps.Single(step => step.Done));

        var terminal = steps[^1];
        Assert.Null(terminal.Error);
        Assert.Equal("ready", terminal.Status);

        // The intermediate statuses are the worker's, not this node's — the whole reason they are
        // read out of the payload rather than invented at the edge.
        Assert.Contains(steps, step => step.Status.StartsWith("downloading (", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Status == "verifying");
    }

    /// <summary>
    /// A pull of something already present is a fast, honest no-op — not a second download. The
    /// worker is the one that knows, because it is the one holding the readiness marker.
    /// </summary>
    [Fact]
    public async Task ASecondPullOfTheSameModelReportsItIsAlreadyPresent()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        using var manifests = new ToolWorkerFixture.TempDirectory("inferhub-manifests");
        var (runtime, executor) = await StartAsync(scratch, manifests);

        await using var _ = runtime;

        await executor.ManageModelAsync("diffusion", ModelCommand.KindPull, "sdxl", CancellationToken.None).ToListAsync();

        var again = await executor
            .ManageModelAsync("diffusion", ModelCommand.KindPull, "sdxl", CancellationToken.None)
            .ToListAsync();

        Assert.Equal("already-present", again[^1].Status);
        Assert.True(again[^1].Done);
        Assert.DoesNotContain(again, step => step.Status.StartsWith("downloading", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeleteAnswersAndDoesNotStreamADownload()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        using var manifests = new ToolWorkerFixture.TempDirectory("inferhub-manifests");
        var (runtime, executor) = await StartAsync(scratch, manifests);

        await using var _ = runtime;

        var steps = await executor
            .ManageModelAsync("diffusion", ModelCommand.KindDelete, "sdxl", CancellationToken.None)
            .ToListAsync();

        var terminal = Assert.Single(steps);
        Assert.True(terminal.Done);
        Assert.Equal("deleted", terminal.Status);
        Assert.Null(terminal.Error);
    }

    /// <summary>
    /// <b>A tool this node does not have is refused by name, and the ceiling is the reason.</b>
    /// <c>Tools:Allowed</c> is the operator's grant (phase-41 D2) and a coordinator cannot add to
    /// it by issuing a command against a tool that is not on the list.
    /// </summary>
    [Fact]
    public async Task ACommandForAToolThisNodeDoesNotRunIsATerminalErrorNamingIt()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        using var manifests = new ToolWorkerFixture.TempDirectory("inferhub-manifests");
        var (runtime, executor) = await StartAsync(scratch, manifests);

        await using var _ = runtime;

        var steps = await executor
            .ManageModelAsync("something-else", ModelCommand.KindPull, "sdxl", CancellationToken.None)
            .ToListAsync();

        var terminal = Assert.Single(steps);
        Assert.True(terminal.Done);
        Assert.NotNull(terminal.Error);
        Assert.Contains("something-else", terminal.Error);
        Assert.Contains("Tools:Allowed", terminal.Error);
    }

    /// <summary>
    /// A generation request for a model whose weights are absent must fail as a <em>job</em>, with
    /// the pull command in the message — never a forty-minute wait. This is the worker's refusal,
    /// carried through the node without being reinterpreted (phase-29 D6).
    /// </summary>
    [Fact]
    public async Task AbsentWeightsFailTheJobAndNameTheCommandThatFixesIt()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        using var manifests = new ToolWorkerFixture.TempDirectory("inferhub-manifests");
        var (runtime, executor) = await StartAsync(scratch, manifests);

        await using var _ = runtime;

        var steps = await executor
            .ManageModelAsync(
                "diffusion",
                ModelCommand.KindPull,
                "flux-schnell",
                CancellationToken.None)
            .ToListAsync();

        Assert.True(steps[^1].Done);

        // …and the refusal path, which is what an operator actually meets on a box that may not
        // reach the internet.
        var refused = await executor
            .ManageModelAsync("nope", ModelCommand.KindPull, "flux-schnell", CancellationToken.None)
            .ToListAsync();

        Assert.NotNull(Assert.Single(refused).Error);
    }

    /// <summary>
    /// The whole command, through <c>ModelCommandExecutor</c> — the same class an
    /// <c>ExecuteModelCommand</c> from the hub lands in. Exactly one frame carries <c>Done</c>,
    /// which is the coordinator's only contract.
    /// </summary>
    [Fact]
    public async Task AToolCommandThroughTheModelCommandExecutorCarriesTheToolAndOneTerminalFrame()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        using var manifests = new ToolWorkerFixture.TempDirectory("inferhub-manifests");
        var (runtime, executor) = await StartAsync(scratch, manifests);

        await using var _ = runtime;

        var commands = new ModelCommandExecutor(
            new UnmanageableBackend(),
            NullLogger<ModelCommandExecutor>.Instance,
            executor);

        var command = new ModelCommand(Guid.NewGuid(), ModelCommand.KindPull, "flux-schnell", "diffusion");

        var frames = await commands.ExecuteAsync(command, "node-a", CancellationToken.None).ToListAsync();

        Assert.Equal(1, frames.Count(frame => frame.Done));
        Assert.All(frames, frame => Assert.Equal("diffusion", frame.Tool));
        Assert.All(frames, frame => Assert.Equal("flux-schnell", frame.ModelName));

        // The backend cannot manage models at all, and that is deliberately irrelevant: phase-26
        // D3's flag is about the node's *inference* backend, and an OpenAI-backed node can host a
        // diffusion tool it manages perfectly well.
        Assert.Null(frames[^1].Error);
        Assert.Equal("ready", frames[^1].Status);
    }

    /// <summary><c>warm</c> has no meaning for a tool model, and it says so rather than inventing one.</summary>
    [Fact]
    public async Task WarmIsNotSomethingAToolModelCanDo()
    {
        var commands = new ModelCommandExecutor(
            new UnmanageableBackend(),
            NullLogger<ModelCommandExecutor>.Instance,
            tools: null);

        var command = new ModelCommand(Guid.NewGuid(), ModelCommand.KindWarm, "sdxl", "diffusion");

        var frames = await commands.ExecuteAsync(command, "node-a", CancellationToken.None).ToListAsync();

        var terminal = Assert.Single(frames);
        Assert.True(terminal.Done);
        Assert.NotNull(terminal.Error);
    }

    /// <summary>
    /// The one that must not regress: a command with no <c>tool</c> is the v3.15 command, byte for
    /// byte, and still goes to the inference backend.
    /// </summary>
    [Fact]
    public async Task ACommandWithNoToolStillGoesToTheBackend()
    {
        var commands = new ModelCommandExecutor(
            new UnmanageableBackend(),
            NullLogger<ModelCommandExecutor>.Instance,
            tools: null);

        var command = new ModelCommand(Guid.NewGuid(), ModelCommand.KindPull, "llama3");

        Assert.False(command.IsToolCommand);

        var terminal = Assert.Single(await commands.ExecuteAsync(command, "node-a", CancellationToken.None).ToListAsync());

        Assert.True(terminal.Done);
        Assert.Contains("cannot manage models", terminal.Error);
        Assert.Null(terminal.Tool);
    }

    /// <summary>A backend that refuses everything, so only the tool half of the split is exercised.</summary>
    private sealed class UnmanageableBackend : IInferenceBackend
    {
        public string Name => "test";

        public string Endpoint => "http://localhost:0";

        public IReadOnlyList<string> Kinds { get; } = [CapabilityKinds.Chat, CapabilityKinds.Embed];

        public bool SupportsModelManagement => false;

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelInfo>>([]);

        public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public IAsyncEnumerable<string> StreamAsync(string kind, string requestJson, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task DeleteAsync(string model, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task WarmAsync(string model, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
