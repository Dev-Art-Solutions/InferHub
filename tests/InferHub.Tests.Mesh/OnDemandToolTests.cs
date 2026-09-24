using InferHub.Node.Configuration;
using InferHub.Node.Resources;
using InferHub.Node.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 85. A real echo worker process, because "the card was given back" means a process
/// exited — a stubbed pool could only report that it had been asked to stop one.
/// </summary>
public class OnDemandToolTests
{
    [Fact]
    public async Task AnOnDemandToolStopsItsWorkerAfterTheRequestAndStillDeclaresItsCapability()
    {
        using var manifests = new ToolWorkerFixture.TempDirectory("inferhub-manifests");
        using var scratch = new ToolWorkerFixture.TempDirectory();

        manifests.WriteManifest("echo.json", new
        {
            id = "echo",
            capabilities = new[] { new { kind = "echo", models = new[] { "echo" } } },
            command = ToolWorkerFixture.Command(),
            minWorkers = 1
        });

        var options = ToolWorkerFixture.Options(scratch.Path, "echo");
        options.ManifestDirectory = manifests.Path;

        var nodeOptions = Microsoft.Extensions.Options.Options.Create(new NodeOptions
        {
            OnDemand = new OnDemandOptions { Enabled = true, ReleaseAfterSeconds = 0, SwitchWaitSeconds = 5 }
        });

        await using var arbiter = new GpuArbiter(nodeOptions, TimeProvider.System, NullLogger<GpuArbiter>.Instance);
        await using var runtime = new ProcessToolRuntime(
            ToolWorkerFixture.Wrap(options),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            NullLogger<ProcessToolRuntime>.Instance,
            nodeOptions,
            arbiter);

        await runtime.StartAsync(CancellationToken.None);

        // minWorkers: 1 started one eagerly; on an on-demand node it does not get to stay.
        Assert.Equal(0, Workers(runtime));
        Assert.Contains(runtime.Capabilities, c => c.Kind == "echo");

        await using (var lease = await runtime.AcquireAsync("echo", "echo", CancellationToken.None))
        {
            Assert.Equal(1, Workers(runtime));
            Assert.Equal(ProcessToolRuntime.GpuTenant("echo"), arbiter.Owner);
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);

        while ((Workers(runtime) > 0 || arbiter.Owner is not null) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(0, Workers(runtime));
        Assert.Null(arbiter.Owner);
        Assert.Contains(runtime.Capabilities, c => c.Kind == "echo");

        // And the next request simply starts one again.
        await using (await runtime.AcquireAsync("echo", "echo", CancellationToken.None))
        {
            Assert.Equal(1, Workers(runtime));
        }

        await runtime.StopAsync(CancellationToken.None);
    }

    private static int Workers(ProcessToolRuntime runtime) =>
        runtime.State("node").Tools.Single(t => t.Id == "echo").Workers;
}
