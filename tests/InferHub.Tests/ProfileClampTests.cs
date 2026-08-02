using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using InferHub.Node.Profiles;
using InferHub.Node.Tools;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// The acceptance criterion of phase 43, written adversarially: <b>no profile can make a node run
/// anything its own configuration did not already allow.</b>
/// </summary>
/// <remarks>
/// <para>
/// Half of it drives <see cref="NodeProfileClamp"/> as the pure function it is; the other half
/// drives <see cref="NodeProfileApplier"/> over a <b>real tool pool with a real child process</b>,
/// because a ceiling that is only checked in a function nobody calls is not a ceiling. D1's whole
/// claim is about where the clamp <em>runs</em>.
/// </para>
/// </remarks>
public class ProfileClampTests
{
    [Fact]
    public void AProfileCanSwitchACapabilityOff()
    {
        var result = NodeProfileClamp.Apply(
            TestProfiles.OpenCeiling(),
            TestProfiles.Profile(capabilities: new Dictionary<string, bool> { ["embed"] = false }));

        Assert.Equal(["embed"], result.Effective.DisabledCapabilities);
        Assert.Empty(result.Refusals);
        Assert.Contains("capability 'embed' off", result.Applied);
    }

    [Fact]
    public void AProfileCannotReEnableACapabilityTheBoxDisabled()
    {
        var result = NodeProfileClamp.Apply(
            TestProfiles.OpenCeiling(disabled: ["chat"]),
            TestProfiles.Profile(capabilities: new Dictionary<string, bool> { ["chat"] = true }));

        var refusal = Assert.Single(result.Refusals);
        Assert.Equal("capability:chat", refusal.Item);
        Assert.Contains("Node:Capabilities:Disabled", refusal.Reason);

        // And the narrowing survives the attempt — the refusal is not "we ignored it and carried on".
        Assert.Contains("chat", result.Effective.DisabledCapabilities);
    }

    [Fact]
    public void AProfileCannotStartAToolThatIsNotInToolsAllowed()
    {
        var result = NodeProfileClamp.Apply(
            TestProfiles.OpenCeiling(toolsEnabled: true, allowedTools: ["whisper"]),
            TestProfiles.Profile(tools: new Dictionary<string, bool> { ["piper"] = true }));

        var refusal = Assert.Single(result.Refusals);
        Assert.Equal("tool:piper", refusal.Item);
        Assert.Contains("Tools:Allowed", refusal.Reason);
        Assert.Empty(result.Effective.DisabledTools);
    }

    /// <summary>
    /// The hostile shape the phase exists to refuse: a coordinator naming an interpreter, a script
    /// or a command line and hoping the node runs it. There is no field for one — a tool is an id
    /// that must already be on the box's own allow list — so it arrives as an id and is refused by
    /// the same sentence as any other id.
    /// </summary>
    [Theory]
    [InlineData("/opt/inferhub/venv/bin/python")]
    [InlineData("../../../usr/bin/curl")]
    [InlineData("python3 -c 'import os; os.system(\"sh\")'")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    public void AProfileCannotNameAPathAnInterpreterOrACommandLine(string hostile)
    {
        var result = NodeProfileClamp.Apply(
            TestProfiles.OpenCeiling(toolsEnabled: true, allowedTools: ["echo"]),
            TestProfiles.Profile(tools: new Dictionary<string, bool> { [hostile] = true }));

        var refusal = Assert.Single(result.Refusals);
        Assert.Equal($"tool:{hostile}", refusal.Item);
        Assert.Contains("Tools:Allowed", refusal.Reason);
    }

    [Fact]
    public void AProfileCannotSwitchTheToolRuntimeOnAtAll()
    {
        var result = NodeProfileClamp.Apply(
            TestProfiles.OpenCeiling(toolsEnabled: false, allowedTools: ["whisper"]),
            TestProfiles.Profile(tools: new Dictionary<string, bool> { ["whisper"] = true }));

        var refusal = Assert.Single(result.Refusals);
        Assert.Contains("Tools:Enabled", refusal.Reason);
    }

    [Fact]
    public void SwitchingAToolOffAlwaysWorks()
    {
        var result = NodeProfileClamp.Apply(
            TestProfiles.OpenCeiling(toolsEnabled: true, allowedTools: ["whisper", "piper"]),
            TestProfiles.Profile(tools: new Dictionary<string, bool> { ["whisper"] = false }));

        Assert.Empty(result.Refusals);
        Assert.Equal(["whisper"], result.Effective.DisabledTools);
    }

    [Fact]
    public void RaisingMaxConcurrencyAboveTheLocalCapIsRefusedAndTheCapStands()
    {
        var result = NodeProfileClamp.Apply(
            TestProfiles.OpenCeiling(maxConcurrency: 2),
            TestProfiles.Profile(maxConcurrency: 64));

        var refusal = Assert.Single(result.Refusals);
        Assert.Equal("maxConcurrency", refusal.Item);
        Assert.Contains("never raise it", refusal.Reason);
        Assert.Equal(2, result.Effective.MaxConcurrency);
    }

    [Fact]
    public void LoweringMaxConcurrencyWorks()
    {
        var result = NodeProfileClamp.Apply(
            TestProfiles.OpenCeiling(maxConcurrency: 8),
            TestProfiles.Profile(maxConcurrency: 3));

        Assert.Empty(result.Refusals);
        Assert.Equal(3, result.Effective.MaxConcurrency);
    }

    /// <summary>
    /// A node that declared no cap has not made a claim about its hardware, so any number the hub
    /// gives it is the first bound there has ever been — which is a narrowing.
    /// </summary>
    [Fact]
    public void AnUncappedNodeAcceptsAnyCap()
    {
        var result = NodeProfileClamp.Apply(
            TestProfiles.OpenCeiling(maxConcurrency: null),
            TestProfiles.Profile(maxConcurrency: 4));

        Assert.Empty(result.Refusals);
        Assert.Equal(4, result.Effective.MaxConcurrency);
    }

    [Fact]
    public void ABackendThatCannotManageModelsRefusesEveryModelItem()
    {
        var result = NodeProfileClamp.Apply(
            TestProfiles.OpenCeiling(supportsModelManagement: false),
            TestProfiles.Profile(models: new NodeProfileModels(["llama3.2"], ["mistral"])));

        Assert.Equal(2, result.Refusals.Count);
        Assert.All(result.Refusals, refusal => Assert.Contains("cannot manage models", refusal.Reason));
        Assert.Empty(result.EnsureModels);
        Assert.Empty(result.RemoveModels);
    }

    [Fact]
    public void AModelInBothEnsureAndRemoveIsRefusedRatherThanGuessedAt()
    {
        var result = NodeProfileClamp.Apply(
            TestProfiles.OpenCeiling(),
            TestProfiles.Profile(models: new NodeProfileModels(["llama3.2", "mistral"], ["llama3.2"])));

        var refusal = Assert.Single(result.Refusals);
        Assert.Equal("model:llama3.2", refusal.Item);
        Assert.Equal(["mistral"], result.EnsureModels);
        Assert.Empty(result.RemoveModels);
    }

    /// <summary>
    /// Refusals are per item, never all-or-nothing (D6): one impossible thing must not cost the four
    /// possible ones.
    /// </summary>
    [Fact]
    public void OneRefusedItemDoesNotStopTheRest()
    {
        var result = NodeProfileClamp.Apply(
            TestProfiles.OpenCeiling(disabled: ["chat"], toolsEnabled: true, allowedTools: ["echo"], maxConcurrency: 8),
            TestProfiles.Profile(
                capabilities: new Dictionary<string, bool> { ["chat"] = true, ["embed"] = false },
                tools: new Dictionary<string, bool> { ["echo"] = false },
                models: new NodeProfileModels(["llama3.2"]),
                maxConcurrency: 2));

        Assert.Single(result.Refusals);
        Assert.Equal(3, result.Applied.Count);
        Assert.Equal(2, result.Effective.MaxConcurrency);
        Assert.Equal(["echo"], result.Effective.DisabledTools);
        Assert.Equal(["llama3.2"], result.EnsureModels);
    }

    [Fact]
    public void NoProfileMeansTheBoxsOwnConfiguration()
    {
        var result = NodeProfileClamp.Apply(TestProfiles.OpenCeiling(disabled: ["embed"], maxConcurrency: 5), null);

        Assert.Equal(["embed"], result.Effective.DisabledCapabilities);
        Assert.Equal(5, result.Effective.MaxConcurrency);
        Assert.Empty(result.Refusals);
        Assert.Empty(result.Applied);
    }

    // ---- the real application path --------------------------------------------------------------

    /// <summary>
    /// The clamp only means something where it actually runs, so this drives the applier over a real
    /// <see cref="ProcessToolRuntime"/> with a real child process behind it.
    /// </summary>
    [Fact]
    public async Task ASwitchedOffToolStopsBeingProvidedByTheRunningNode()
    {
        await using var fixture = await ApplierFixture.StartAsync();

        Assert.Contains(fixture.Runtime.Capabilities, capability => capability.Kind == "echo");

        var off = await fixture.Applier.ApplyAsync(
            "node-1",
            TestProfiles.Profile(tools: new Dictionary<string, bool> { ["echo"] = false }),
            CancellationToken.None);

        Assert.Empty(off.State.Refusals);
        Assert.Empty(fixture.Runtime.Capabilities);
        await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.Runtime.AcquireAsync("echo", "echo", CancellationToken.None));

        // …and back on, without restarting anything (D6).
        var on = await fixture.Applier.ApplyAsync(
            "node-1",
            TestProfiles.Profile(revision: 2, tools: new Dictionary<string, bool> { ["echo"] = true }),
            CancellationToken.None);

        Assert.Empty(on.State.Refusals);
        Assert.Contains(fixture.Runtime.Capabilities, capability => capability.Kind == "echo");

        await using var lease = await fixture.Runtime.AcquireAsync("echo", "echo", CancellationToken.None);
        Assert.Equal("echo", lease.ToolId);
    }

    /// <summary>
    /// The one that matters most: a hub asking for a tool this node was never granted must change
    /// nothing about the running node, not merely be logged.
    /// </summary>
    [Fact]
    public async Task AHostileProfileChangesNothingOnTheRunningNode()
    {
        await using var fixture = await ApplierFixture.StartAsync();

        var before = fixture.Runtime.Capabilities;

        var result = await fixture.Applier.ApplyAsync(
            "node-1",
            TestProfiles.Profile(tools: new Dictionary<string, bool>
            {
                ["/opt/evil/worker.py"] = true,
                ["piper"] = true
            }),
            CancellationToken.None);

        Assert.Equal(2, result.State.Refusals.Count);
        Assert.All(result.State.Refusals, refusal => Assert.Contains("Tools:Allowed", refusal.Reason));
        Assert.Equal(before.Count, fixture.Runtime.Capabilities.Count);

        await using var lease = await fixture.Runtime.AcquireAsync("echo", "echo", CancellationToken.None);
        Assert.Equal("echo", lease.ToolId);
    }

    [Fact]
    public async Task TheSameRevisionAppliedTwiceIsANoOpAndSaysSo()
    {
        await using var fixture = await ApplierFixture.StartAsync();

        var profile = TestProfiles.Profile(
            revision: 7,
            models: new NodeProfileModels(["llama3.2"]));

        var first = await fixture.Applier.ApplyAsync("node-1", profile, CancellationToken.None);
        Assert.Single(first.Commands);
        Assert.True(first.Changed);

        var second = await fixture.Applier.ApplyAsync("node-1", profile, CancellationToken.None);
        Assert.Empty(second.Commands);
        Assert.False(second.Changed);
        Assert.Equal(first.State.Revision, second.State.Revision);
    }

    private sealed class ApplierFixture : IAsyncDisposable
    {
        private ToolWorkerFixture.TempDirectory manifests = null!;
        private ToolWorkerFixture.TempDirectory scratch = null!;

        public ProcessToolRuntime Runtime { get; private set; } = null!;

        public NodeProfileApplier Applier { get; private set; } = null!;

        public static async Task<ApplierFixture> StartAsync()
        {
            var fixture = new ApplierFixture
            {
                manifests = new ToolWorkerFixture.TempDirectory("inferhub-profile-manifests"),
                scratch = new ToolWorkerFixture.TempDirectory()
            };

            fixture.manifests.WriteManifest("echo.json", new
            {
                id = "echo",
                capabilities = new[] { new { kind = "echo", models = new[] { "echo" } } },
                command = ToolWorkerFixture.Command(),
                startTimeoutSeconds = 30,
                requestTimeoutSeconds = 30
            });

            var toolOptions = ToolWorkerFixture.Options(fixture.scratch.Path, "echo");
            toolOptions.ManifestDirectory = fixture.manifests.Path;

            fixture.Runtime = new ProcessToolRuntime(
                ToolWorkerFixture.Wrap(toolOptions),
                TimeProvider.System,
                NullLoggerFactory.Instance,
                NullLogger<ProcessToolRuntime>.Instance);

            await fixture.Runtime.StartAsync(CancellationToken.None);

            fixture.Applier = new NodeProfileApplier(
                Options.Create(new NodeOptions { Name = "profile-node" }),
                ToolWorkerFixture.Wrap(toolOptions),
                new ManageableBackend(),
                fixture.Runtime,
                NullLogger<NodeProfileApplier>.Instance);

            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.StopAsync(CancellationToken.None);
            manifests.Dispose();
            scratch.Dispose();
        }
    }

    /// <summary>A backend that claims model management so the model half of a profile gets that far.</summary>
    private sealed class ManageableBackend : IInferenceBackend
    {
        public string Name => "test";

        public string Endpoint => "http://127.0.0.1:0/";

        public bool SupportsModelManagement => true;

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
