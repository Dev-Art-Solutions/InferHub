using InferHub.Node.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// A worker may change its answer to "what can you do" while it runs, and the node picks it up
/// without a restart (v3.14.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug this exists for.</b> v3.14.0's diffusion worker downloaded model weights inside the
/// request that first named the model, so the first <c>sdxl</c> call on a fresh volume spent the
/// whole 900-second request budget fetching and then returned a 502 — twice, before the download
/// converged. The fix is that a recipe is only <em>declared</em> once its weights are proven
/// loadable, with a background thread doing the fetching. That is only possible if a worker can say
/// "and now this one too" after the handshake.
/// </para>
/// <para>
/// Two mechanisms had to be built for it, and one of them already existed on paper: phase-41 D6
/// specified a ping/pong liveness probe on idle workers, <c>ToolWorkerProcess.PingAsync</c> was
/// written for it, and <b>nothing ever called it</b> between v3.9.0 and v3.14.1. It matters here
/// because an idle worker has nobody reading its stdout, so a late <c>ready</c> sits in the pipe
/// until something drains it.
/// </para>
/// <para>
/// Everything below drives a <b>real child process</b>. A stub would prove the stub re-declares.
/// </para>
/// </remarks>
public class ToolRedeclarationTests
{
    [Fact]
    public async Task ALateReadyWidensWhatThePoolOffers_AndTheNodeIsToldWithoutARestart()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory("inferhub-redeclare");
        var options = ToolWorkerFixture.Options(scratch.Path, "echo");

        // The manifest grants two models; the worker starts able to serve only one — the shape of a
        // diffusion node whose second model is still downloading.
        var manifest = ToolWorkerFixture.Manifest(
            models: ["ready-now", "still-fetching"],
            arguments:
            [
                "--capabilities", "echo:ready-now",
                "--redeclare-on-ping", "echo:ready-now,still-fetching"
            ]);

        await using var pool = new ToolWorkerPool(
            manifest,
            options,
            TimeProvider.System,
            NullLogger.Instance);

        var changes = 0;
        pool.CapabilitiesChanged += () => Interlocked.Increment(ref changes);

        await using (var lease = await pool.AcquireAsync(CancellationToken.None))
        {
            // Started, handshaked, narrowed to what the worker said it could do.
        }

        Assert.Equal(["ready-now"], Models(pool));

        // The maintenance tick is what drains an idle worker's stdout. Before v3.14.1 it retired
        // idle workers and nothing else, so this frame would have sat in the pipe forever.
        await pool.MaintainAsync(CancellationToken.None);

        Assert.Equal(["ready-now", "still-fetching"], Models(pool).Order().ToArray());
        Assert.True(changes >= 1, "the node was never told the capability set had changed");
    }

    /// <summary>
    /// The clamp is unchanged and is still the node's. A worker that re-declares a model its
    /// manifest never granted is refused exactly as it would be at handshake — the operator's file
    /// on the box is the authority, and a script that could grant itself capabilities would be
    /// deciding what traffic the fleet sends it.
    /// </summary>
    [Fact]
    public async Task ALateReadyCanNarrowAndCanNeverWiden()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory("inferhub-redeclare-clamp");
        var options = ToolWorkerFixture.Options(scratch.Path, "echo");

        var manifest = ToolWorkerFixture.Manifest(
            models: ["granted"],
            arguments:
            [
                "--capabilities", "echo:granted",
                "--redeclare-on-ping", "echo:granted,never-granted;secret:everything"
            ]);

        await using var pool = new ToolWorkerPool(
            manifest,
            options,
            TimeProvider.System,
            NullLogger.Instance);

        await using (var lease = await pool.AcquireAsync(CancellationToken.None))
        {
        }

        await pool.MaintainAsync(CancellationToken.None);

        Assert.Equal(["granted"], Models(pool));
        Assert.DoesNotContain(pool.Capabilities, c => c.Kind == "secret");
    }

    /// <summary>
    /// The probe is the liveness check phase-41 D6 asked for, finally wired up: a worker that has
    /// wedged without exiting is retired by maintenance rather than at the expense of the next
    /// caller's queue budget.
    /// </summary>
    [Fact]
    public async Task AnIdleWorkerThatStoppedAnsweringIsRetiredByTheProbe()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory("inferhub-probe");
        var options = ToolWorkerFixture.Options(scratch.Path, "echo");

        await using var pool = new ToolWorkerPool(
            ToolWorkerFixture.Manifest(arguments: ["--wedge-on-ping"]),
            options,
            TimeProvider.System,
            NullLogger.Instance);

        await using (var lease = await pool.AcquireAsync(CancellationToken.None))
        {
        }

        Assert.Equal(1, pool.LiveWorkerCount);

        // The worker is ALIVE and has stopped answering, so `IsAlive` says nothing useful and only
        // the probe can tell. That is the case phase-41 D6 wrote `PingAsync` for and that nothing
        // exercised, because nothing called it.
        await pool.MaintainAsync(CancellationToken.None);

        Assert.Equal(0, pool.LiveWorkerCount);

        // …and the pool recovers: the next request starts a fresh worker.
        await using var next = await pool.AcquireAsync(CancellationToken.None);
        Assert.NotNull(next);
    }

    /// <summary>
    /// The guard on the guard. Without a worker that re-declares, every assertion above would pass
    /// on a pool that simply never changed its mind.
    /// </summary>
    [Fact]
    public async Task AWorkerThatDoesNotRedeclareLeavesTheSetAlone()
    {
        using var scratch = new ToolWorkerFixture.TempDirectory("inferhub-no-redeclare");
        var options = ToolWorkerFixture.Options(scratch.Path, "echo");

        await using var pool = new ToolWorkerPool(
            ToolWorkerFixture.Manifest(models: ["a", "b"], arguments: ["--capabilities", "echo:a"]),
            options,
            TimeProvider.System,
            NullLogger.Instance);

        var changes = 0;
        pool.CapabilitiesChanged += () => Interlocked.Increment(ref changes);

        await using (var lease = await pool.AcquireAsync(CancellationToken.None))
        {
        }

        await pool.MaintainAsync(CancellationToken.None);
        await pool.MaintainAsync(CancellationToken.None);

        Assert.Equal(["a"], Models(pool));

        // One change, from the handshake. A pool that raised on every probe would make the node
        // re-report to its coordinator twice a minute, forever.
        Assert.Equal(1, changes);
    }

    private static string[] Models(ToolWorkerPool pool) =>
        pool.Capabilities.SelectMany(capability => capability.Models).ToArray();
}
