using InferHub.Node.Tools;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// The <b>reference Python library</b>, driven as a real child process (v3.16.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists, in one sentence: the C# echo worker and the Python reference library
/// had different concurrency designs, and the suite only ever exercised the one that was correct.</b>
/// </para>
/// <para>
/// Phase 47 gave both a way to run a request on a background thread so a <c>cancel</c> could arrive
/// mid-flight. The C# fixture kept <em>one</em> read loop that dispatches and carries on. The Python
/// library grew a <em>second</em> reader — a "control pump" that read from the same stream while a
/// request ran, honoured <c>cancel</c> and <c>ping</c>, and <b>discarded everything else</b>. The
/// frame it discarded was the next <c>request</c>: on a worker that answers quickly, every other job
/// was swallowed and hung until its deadline. It shipped in the v3.16.0 diffusion image and dropped
/// every second image generation on a real card. Nothing in 1141 tests could see it, because nothing
/// in the suite ran the Python library.
/// </para>
/// <para>
/// So these tests drive <c>python/examples/echo.py</c> through the node's real
/// <see cref="ToolWorkerPool"/>. They are <b>skipped</b> when no interpreter is on PATH — the
/// established shape for a gated test here — and they run in CI, where <c>python3</c> exists.
/// </para>
/// </remarks>
public class PythonWorkerProtocolTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InferHub.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static ToolManifest EchoManifest(string python) => new()
    {
        Id = "pyecho",
        Capabilities = [new NodeCapability("echo", ["echo"])],
        Command = [python, "-u", Path.Combine(RepositoryRoot(), "python", "examples", "echo.py")],
        MaxWorkers = 1,
        StartTimeoutSeconds = 120,
        RequestTimeoutSeconds = 60,
        IdleTimeoutSeconds = 900
    };

    /// <summary>
    /// <b>The regression test for the frame-eating bug.</b> Five requests in a row against one warm
    /// worker; every one must answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Each request must take real time, and that is the whole test — an instant one cannot
    /// reproduce the bug.</b> The control pump only blocked on stdin <em>while a request thread was
    /// still alive</em>; an echo that answers in microseconds finishes before the loop gets there,
    /// so the pump never runs and no frame is eaten. The first version of this test used instant
    /// requests and passed against the broken library. Measured against the shipped v3.16.1
    /// package: instant requests <b>5/5</b> (proves nothing), 600 ms requests <b>1/5</b>.
    /// </para>
    /// <para>
    /// Five rather than two, because the failure alternated and a two-request test passes half the
    /// time by luck.
    /// </para>
    /// </remarks>
    [PythonWorkerFact]
    public async Task ConsecutiveRequestsAllGetAnAnswer()
    {
        var python = PythonWorkerTestGate.Interpreter!;

        using var scratch = new ToolWorkerFixture.TempDirectory();
        var options = ToolWorkerFixture.Options(scratch.Path, "pyecho");

        await using var pool = new ToolWorkerPool(
            EchoManifest(python),
            options,
            TimeProvider.System,
            NullLogger.Instance);

        await pool.StartAsync(CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            await using var lease = await pool.AcquireAsync(CancellationToken.None);

            var request = new ToolFrame
            {
                Type = ToolFrameTypes.Request,
                Id = $"req-{i}",
                Capability = "echo",
                Model = "echo",
                Payload = System.Text.Json.JsonSerializer.SerializeToElement(
                    new { behaviour = "slow", steps = 4, stepMs = 150 }, ToolProtocol.Json)
            };

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var frames = new List<ToolFrame>();

            await foreach (var frame in lease.ExecuteAsync(request, deadline.Token))
            {
                frames.Add(frame);
            }

            var result = Assert.Single(frames, f => f.Type is ToolFrameTypes.Result);
            Assert.Equal($"req-{i}", result.Id);

            // The progress frames prove the request really did take time, so a future edit that
            // makes it instant fails here rather than silently turning this back into decoration.
            Assert.Equal(4, frames.Count(f => f.Type is ToolFrameTypes.Progress));
        }
    }

    /// <summary>
    /// A cancel must still reach a request that is running — which is what the second reader was
    /// added for, and what any fix has to keep true.
    /// </summary>
    [PythonWorkerFact]
    public async Task ACancelReachesARunningRequestAndTheWorkerSurvivesIt()
    {
        var python = PythonWorkerTestGate.Interpreter!;

        using var scratch = new ToolWorkerFixture.TempDirectory();
        var options = ToolWorkerFixture.Options(scratch.Path, "pyecho");

        await using var pool = new ToolWorkerPool(
            EchoManifest(python),
            options,
            TimeProvider.System,
            NullLogger.Instance);

        await pool.StartAsync(CancellationToken.None);

        await using (var lease = await pool.AcquireAsync(CancellationToken.None))
        {
            var slow = new ToolFrame
            {
                Type = ToolFrameTypes.Request,
                Id = "slow",
                Capability = "echo",
                Model = "echo",
                Payload = System.Text.Json.JsonSerializer.SerializeToElement(
                    new { behaviour = "slow", steps = 60, stepMs = 100 }, ToolProtocol.Json)
            };

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var frames = lease.ExecuteAsync(slow, deadline.Token).GetAsyncEnumerator(deadline.Token);

            // Let it get going, then ask it to stop from outside the enumeration — the shape the
            // node's own cancel registration uses.
            Assert.True(await frames.MoveNextAsync());
            await lease.CancelAsync("slow", CancellationToken.None);

            ToolFrame? terminal = null;

            while (await frames.MoveNextAsync())
            {
                terminal = frames.Current;
            }

            await frames.DisposeAsync();

            Assert.NotNull(terminal);
            Assert.Equal(ToolFrameTypes.Error, terminal!.Type);
            Assert.Equal(ToolErrorCodes.Cancelled, terminal.Code);
        }

        // …and the whole point: the worker is still there, so the next caller does not pay for the
        // first one's change of mind.
        await using var next = await pool.AcquireAsync(CancellationToken.None);

        var after = new ToolFrame
        {
            Type = ToolFrameTypes.Request,
            Id = "after",
            Capability = "echo",
            Model = "echo",
            Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { }, ToolProtocol.Json)
        };

        using var secondDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var answered = false;

        await foreach (var frame in next.ExecuteAsync(after, secondDeadline.Token))
        {
            answered |= frame.Type is ToolFrameTypes.Result && frame.Id == "after";
        }

        Assert.True(answered, "the worker did not survive the cancel");
    }
}
