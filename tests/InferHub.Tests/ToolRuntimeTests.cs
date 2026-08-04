using System.Text.Json;
using InferHub.Node.Tools;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// The phase-41 runtime, driven against a <b>real child process</b> throughout.
/// </summary>
/// <remarks>
/// Every failure mode below is one the roadmap claims the node survives, and each one asserts the
/// same closing fact: it is a failed <em>job</em>, and the runtime is still able to serve the next
/// request afterwards. That last clause is the phase — a tool that can take a node down is worse
/// than no tool.
/// </remarks>
public class ToolRuntimeTests
{
    [Fact]
    public async Task AToolJobRoundTripsThroughARealChildProcess()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        await using var runtime = new SingleToolRuntime(ToolWorkerFixture.Manifest(), scratch.Path);

        var result = await runtime.RunAsync("""{"model":"echo","hello":"world"}""");

        Assert.True(result.Success);
        Assert.Contains("\"hello\":\"world\"", result.Payload);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task AWorkerThatNeverSaysReadyHitsTheStartTimeoutAndIsKilled()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        var manifest = ToolWorkerFixture.Manifest(
            arguments: ["--no-ready"],
            startTimeoutSeconds: 1);

        await using var runtime = new SingleToolRuntime(manifest, scratch.Path);

        var result = await runtime.RunAsync("""{"model":"echo"}""");

        Assert.False(result.Success);
        Assert.Contains("ready", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARequestThatOverrunsIsKilledAndReportedAsAFailedJob()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        var manifest = ToolWorkerFixture.Manifest(requestTimeoutSeconds: 1);
        await using var runtime = new SingleToolRuntime(manifest, scratch.Path);

        var result = await runtime.RunAsync("""{"model":"echo","behaviour":"wedge"}""");

        Assert.False(result.Success);
        Assert.Contains("did not answer", result.Error);

        // The point of the phase: the runtime still works afterwards.
        var next = await runtime.RunAsync("""{"model":"echo","after":"the wedge"}""");
        Assert.True(next.Success);
        Assert.Contains("after", next.Payload);
    }

    [Fact]
    public async Task AWorkerThatExitsMidRequestProducesACleanErrorAndTheNextRequestStartsAFreshOne()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        await using var runtime = new SingleToolRuntime(ToolWorkerFixture.Manifest(), scratch.Path);

        var died = await runtime.RunAsync("""{"model":"echo","behaviour":"exit","code":7}""");

        Assert.False(died.Success);
        Assert.Contains("stopped answering", died.Error);

        var next = await runtime.RunAsync("""{"model":"echo","restarted":true}""");
        Assert.True(next.Success);
        Assert.Contains("restarted", next.Payload);
    }

    [Fact]
    public async Task AnErrorFrameIsAFailedJobAndTheWorkerKeepsServing()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        await using var runtime = new SingleToolRuntime(ToolWorkerFixture.Manifest(), scratch.Path);

        var failed = await runtime.RunAsync("""{"model":"echo","behaviour":"error","message":"no such voice"}""");

        Assert.False(failed.Success);
        Assert.Equal("no such voice", failed.Error);

        var next = await runtime.RunAsync("""{"model":"echo","still":"alive"}""");
        Assert.True(next.Success);
    }

    [Fact]
    public async Task TheRestartBudgetGivesUpWithdrawsTheCapabilitiesAndKeepsProbing()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();

        // A worker that exits the moment it is spawned: every start is a failure.
        var manifest = ToolWorkerFixture.Manifest(arguments: ["--exit-on-start"], startTimeoutSeconds: 5);
        var options = ToolWorkerFixture.Options(scratch.Path, "echo");
        var pool = new ToolWorkerPool(manifest, options, TimeProvider.System, NullLogger.Instance);

        var changes = 0;
        pool.CapabilitiesChanged += () => Interlocked.Increment(ref changes);

        await pool.StartAsync(CancellationToken.None);
        Assert.NotEmpty(pool.Capabilities);

        for (var attempt = 0; attempt < options.MaxStartAttempts; attempt++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => pool.AcquireAsync(CancellationToken.None));
        }

        // Past the budget: no capabilities, so the node's next report unroutes it for this work.
        await Assert.ThrowsAnyAsync<Exception>(() => pool.AcquireAsync(CancellationToken.None));
        Assert.Empty(pool.Capabilities);
        Assert.True(changes >= 1, "giving up must raise CapabilitiesChanged so the node re-reports at once");

        // ...and it keeps probing rather than spinning: MaintainAsync is what the runtime's
        // maintenance loop calls, and it must not throw when the probe fails again.
        await pool.MaintainAsync(CancellationToken.None);
        Assert.Empty(pool.Capabilities);

        await pool.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrencyPastMaxWorkersWaitsAndThenRefusesWithARetryHint()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        var manifest = ToolWorkerFixture.Manifest(maxWorkers: 1, requestTimeoutSeconds: 30);
        await using var runtime = new SingleToolRuntime(manifest, scratch.Path, queueMaxWaitSeconds: 1);

        // Occupy the only worker for longer than the queue budget.
        var busy = runtime.RunAsync("""{"model":"echo","behaviour":"sleep","seconds":4}""");
        await Task.Delay(500);

        var refused = await runtime.RunAsync("""{"model":"echo"}""");

        Assert.False(refused.Success);
        Assert.Contains("worker limit", refused.Error);

        // The saturation shape the whole project uses: a retry hint, never a bare failure.
        Assert.NotNull(refused.RetryAfterSeconds);
        Assert.True(refused.RetryAfterSeconds >= 1);

        Assert.True((await busy).Success);
    }

    [Fact]
    public async Task TheScratchDirectoryIsGoneAfterSuccessAndAfterFailure()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        var manifest = ToolWorkerFixture.Manifest(requestTimeoutSeconds: 1);
        await using var runtime = new SingleToolRuntime(manifest, scratch.Path);

        var ok = await runtime.RunAsync(
            """{"model":"echo","behaviour":"files"}""",
            new ToolAttachment("input.txt", "text/plain", "hello from the hub"u8.ToArray()));

        Assert.True(ok.Success);
        Assert.Contains("hello from the hub", ok.Payload);
        Assert.Equal(Array.Empty<string>(), Directory.GetDirectories(scratch.Path));

        var failed = await runtime.RunAsync("""{"model":"echo","behaviour":"wedge"}""");

        Assert.False(failed.Success);
        Assert.Equal(Array.Empty<string>(), Directory.GetDirectories(scratch.Path));
    }

    [Fact]
    public async Task AFileWrittenByTheWorkerComesBackAsAnAttachment()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        await using var runtime = new SingleToolRuntime(ToolWorkerFixture.Manifest(), scratch.Path);

        var result = await runtime.RunAsync(
            """{"model":"echo","behaviour":"files"}""",
            new ToolAttachment("in.txt", "text/plain", "one"u8.ToArray()));

        Assert.True(result.Success);
        var attachment = Assert.Single(result.Attachments!);
        Assert.Equal("echo-output.txt", attachment.Name);
        Assert.Equal("echoed 1 file(s)", System.Text.Encoding.UTF8.GetString(attachment.Bytes));
    }

    [Fact]
    public async Task StderrFromTheWorkerReachesTheNodesLog()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        var captured = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(captured);
        });

        await using var runtime = new SingleToolRuntime(
            ToolWorkerFixture.Manifest(),
            scratch.Path,
            loggerFactory: factory);

        var result = await runtime.RunAsync(
            """{"model":"echo","behaviour":"stderr","message":"Traceback (most recent call last)"}""");

        Assert.True(result.Success);

        // The pump is a separate task; give it a moment to land the line.
        for (var i = 0; i < 40 && !captured.Contains("Traceback"); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(
            captured.Contains("Traceback"),
            "a worker's stderr is where a Python traceback goes, and it must reach the node's log");
    }

    [Fact]
    public async Task ChunksArriveInOrderAndTheStreamEndsWithATerminalChunk()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        await using var runtime = new SingleToolRuntime(ToolWorkerFixture.Manifest(), scratch.Path);

        var chunks = await runtime.StreamAsync("""{"model":"echo","behaviour":"chunks","count":3}""");

        Assert.Equal(4, chunks.Count);
        Assert.All(chunks.Take(3), chunk => Assert.False(chunk.Done));
        Assert.True(chunks[^1].Done);

        for (var i = 0; i < 3; i++)
        {
            Assert.Contains($"\"index\":{i}", chunks[i].Payload);
        }
    }

    [Fact]
    public async Task AStreamingRequestThatProducesFilesFailsNamingTheLimitationRatherThanDroppingThem()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        await using var runtime = new SingleToolRuntime(ToolWorkerFixture.Manifest(), scratch.Path);

        var chunks = await runtime.StreamAsync(
            """{"model":"echo","behaviour":"files"}""",
            new ToolAttachment("in.txt", "text/plain", "x"u8.ToArray()));

        var terminal = Assert.Single(chunks);
        Assert.True(terminal.Done);
        Assert.Contains("streaming request", terminal.Payload);
    }

    [Fact]
    public async Task AWorkerMayNarrowItsManifestAndMayNotWidenIt()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();

        // The manifest claims two models; the worker reports one of them plus one it invented.
        var manifest = ToolWorkerFixture.Manifest(
            kind: "transcribe",
            models: ["whisper-small", "whisper-large-v3"],
            arguments: ["--capabilities", "transcribe:whisper-small,whisper-invented;speak:anything"]);

        var options = ToolWorkerFixture.Options(scratch.Path, "echo");
        var pool = new ToolWorkerPool(manifest, options, TimeProvider.System, NullLogger.Instance);

        await using var lease = await pool.AcquireAsync(CancellationToken.None);

        var capability = Assert.Single(pool.Capabilities);
        Assert.Equal("transcribe", capability.Kind);
        Assert.Equal(["whisper-small"], capability.Models);

        await lease.DisposeAsync();
        await pool.DisposeAsync();
    }

    /// <summary>
    /// Phase 42's one amendment to the narrowing rule, and the only widening anywhere in the
    /// runtime: an <em>empty</em> model list in a manifest is an open set, so the worker's report is
    /// taken as-is. The TTS worker's models are voice files an operator dropped into a directory,
    /// and no list written in advance survives the first new voice — the drift phase-40 D2 refuses.
    /// The <b>kind</b> is still the ceiling, which is what the second half of this asserts.
    /// </summary>
    [Fact]
    public async Task AnEmptyModelListInTheManifestLetsTheWorkerReportWhatItFound()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();

        var manifest = ToolWorkerFixture.Manifest(
            kind: "speak",
            models: [],
            arguments: ["--capabilities", "speak:en_US-amy,bg_BG-ivan;transcribe:not-granted"]);

        var options = ToolWorkerFixture.Options(scratch.Path, "echo");
        var pool = new ToolWorkerPool(manifest, options, TimeProvider.System, NullLogger.Instance);

        await using var lease = await pool.AcquireAsync(CancellationToken.None);

        var capability = Assert.Single(pool.Capabilities);
        Assert.Equal("speak", capability.Kind);
        Assert.Equal(["en_US-amy", "bg_BG-ivan"], capability.Models);

        await lease.DisposeAsync();
        await pool.DisposeAsync();
    }

    [Fact]
    public void AnOpenModelSetOffersNothingUntilAWorkerHasAnswered()
    {
        // Before the handshake, "ask the worker" has no answer — and a capability nobody has
        // confirmed must not be advertised to the fleet in the meantime.
        var declared = new[] { new InferHub.Shared.Contracts.NodeCapability("speak", []) };

        Assert.Empty(ToolWorkerPool.Narrow(declared, reported: null));
    }

    /// <summary>
    /// …which is exactly why an open set must start one worker eagerly, and this is the deadlock
    /// that proves it. <b>Found by running the published image:</b> nothing declares the capability
    /// until a worker has reported, no worker starts until a request is routed, and no request is
    /// routed to a capability nobody declares — so a TTS node with a voice on its volume answered
    /// "this node does not provide 'speak'" forever, with every test green.
    /// </summary>
    [Fact]
    public async Task AnOpenModelSetStartsOneWorkerEagerlyEvenWithMinWorkersZero()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();

        var manifest = ToolWorkerFixture.Manifest(
            kind: "speak",
            models: [],
            minWorkers: 0,
            arguments: ["--capabilities", "speak:en_US-amy"]);

        var options = ToolWorkerFixture.Options(scratch.Path, "echo");
        var pool = new ToolWorkerPool(manifest, options, TimeProvider.System, NullLogger.Instance);

        await pool.StartAsync(CancellationToken.None);

        var capability = Assert.Single(pool.Capabilities);
        Assert.Equal("speak", capability.Kind);
        Assert.Equal(["en_US-amy"], capability.Models);

        await pool.DisposeAsync();
    }

    [Fact]
    public async Task AClosedModelSetStillStartsNothingUntilItIsAsked()
    {
        // The eager start is scoped to the case that needs it. A Whisper pool whose manifest names
        // its models must not load weights at boot on every node in the fleet.
        using var scratch = new ToolWorkerFixture.TempDirectory();

        var pool = new ToolWorkerPool(
            ToolWorkerFixture.Manifest(kind: "transcribe", models: ["whisper-small"], minWorkers: 0),
            ToolWorkerFixture.Options(scratch.Path, "echo"),
            TimeProvider.System,
            NullLogger.Instance);

        await pool.StartAsync(CancellationToken.None);

        Assert.Equal(["whisper-small"], Assert.Single(pool.Capabilities).Models);
        Assert.Equal(0, pool.LiveWorkerCount);

        await pool.DisposeAsync();
    }

    [Fact]
    public async Task AnIdleWorkerIsRetiredAndTheNextRequestStartsAFreshOne()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory();
        var manifest = ToolWorkerFixture.Manifest(idleTimeoutSeconds: 1);
        var options = ToolWorkerFixture.Options(scratch.Path, "echo");
        var pool = new ToolWorkerPool(manifest, options, TimeProvider.System, NullLogger.Instance);

        var lease = await pool.AcquireAsync(CancellationToken.None);
        await lease.DisposeAsync();

        await Task.Delay(1200);
        await pool.MaintainAsync(CancellationToken.None);

        // Nothing observable but "it still works" — which is the assertion that matters, because a
        // retirement that killed the pool would look identical until the next request.
        var next = await pool.AcquireAsync(CancellationToken.None);
        await next.DisposeAsync();

        await pool.DisposeAsync();
    }

    /// <summary>A one-manifest runtime plus an executor over it — the shape both hosts drive.</summary>
    private sealed class SingleToolRuntime : IToolRuntime, IAsyncDisposable
    {
        private readonly ToolWorkerPool pool;
        private readonly ToolExecutor executor;
        private readonly string capabilityKind;

        public SingleToolRuntime(
            ToolManifest manifest,
            string scratch,
            int queueMaxWaitSeconds = 5,
            ILoggerFactory? loggerFactory = null)
        {
            var options = ToolWorkerFixture.Options(scratch, manifest.Id);
            options.QueueMaxWaitSeconds = queueMaxWaitSeconds;

            capabilityKind = manifest.Capabilities[0].Kind;
            pool = new ToolWorkerPool(
                manifest,
                options,
                TimeProvider.System,
                (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger(manifest.Id));

            pool.CapabilitiesChanged += () => CapabilitiesChanged?.Invoke();

            executor = new ToolExecutor(
                this,
                ToolWorkerFixture.Wrap(options),
                (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ToolExecutor>());
        }

        public bool Enabled => true;

        public IReadOnlyList<NodeCapability> Capabilities => pool.Capabilities;

        public NodeToolState State(string nodeId)
            => new(nodeId, Enabled: true, [pool.Report()], DateTimeOffset.UtcNow);

        public event Action? CapabilitiesChanged;

        public Task<ToolWorkerLease> AcquireAsync(string capability, string model, CancellationToken cancellationToken)
            => pool.AcquireAsync(cancellationToken);

        public Task SetDisabledToolsAsync(IReadOnlyCollection<string> toolIds, CancellationToken cancellationToken)
            => toolIds.Contains(pool.Manifest.Id, StringComparer.OrdinalIgnoreCase)
                ? pool.SuspendAsync()
                : pool.ResumeAsync(cancellationToken);

        public Task<ToolResult> RunAsync(string payload, params ToolAttachment[] attachments) =>
            executor.RunAsync(
                new ToolJob(
                    Guid.NewGuid(),
                    capabilityKind,
                    ModelOf(payload),
                    payload,
                    attachments.Length == 0 ? null : attachments),
                CancellationToken.None);

        public async Task<IReadOnlyList<ToolChunk>> StreamAsync(string payload, params ToolAttachment[] attachments)
        {
            var chunks = new List<ToolChunk>();

            var job = new ToolJob(
                Guid.NewGuid(),
                capabilityKind,
                ModelOf(payload),
                payload,
                attachments.Length == 0 ? null : attachments);

            await foreach (var chunk in executor.StreamAsync(job, CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            return chunks;
        }

        public ValueTask DisposeAsync() => pool.DisposeAsync();

        private static string ModelOf(string payload) =>
            JsonDocument.Parse(payload).RootElement.TryGetProperty("model", out var model)
                ? model.GetString() ?? "echo"
                : "echo";
    }
}
