using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;

namespace InferHub.Tests;

/// <summary>
/// Phase 101, D6: the hub's copy of the node's rules, for the message. Each refusal names the key on the box
/// that decided it, because the fix is on the box.
/// </summary>
public class NodeUpdateRegistryTests
{
    private static NodeUpdateState State(
        bool check = true,
        bool allowFromHub = true,
        bool canApply = true,
        string phase = NodeUpdatePhase.Available,
        string? whyNot = null) => new(
            "gpu-1", "3.66.0", "3.67.0", phase, check, Auto: false, allowFromHub, canApply, whyNot,
            DateTimeOffset.UtcNow, null, null, null, DateTimeOffset.UtcNow);

    [Fact]
    public void ANodeThatAllowsItIsSentTheCommand()
    {
        Assert.Null(NodeUpdateControl.Refuse("gpu-1", NodeUpdateCommand.KindApply, State()));
        Assert.Null(NodeUpdateControl.Refuse("gpu-1", NodeUpdateCommand.KindCheck, State()));

        // Nothing known to be available is not a refusal: the node checks first, and the hub's last
        // report may be hours older than the release.
        Assert.Null(NodeUpdateControl.Refuse("gpu-1", NodeUpdateCommand.KindApply, State(phase: NodeUpdatePhase.UpToDate)));
    }

    [Fact]
    public void ANodeBeforeTheFeatureIsRefusedWithItsReason()
        => Assert.Contains("before v3.66", NodeUpdateControl.Refuse("gpu-1", NodeUpdateCommand.KindApply, null));

    [Fact]
    public void AnOperatorWhoSaidNeverIsNotOverruledByAClick()
        => Assert.Contains("Update:AllowFromHub off", NodeUpdateControl.Refuse("gpu-1", NodeUpdateCommand.KindApply, State(allowFromHub: false)));

    [Fact]
    public void ABoxThatCannotApplyPassesOnTheNodesOwnSentence()
    {
        var refusal = NodeUpdateControl.Refuse("gpu-1", NodeUpdateCommand.KindApply,
            State(canApply: false, whyNot: "this node was not installed by the Windows setup"));

        Assert.Contains("cannot apply updates: this node was not installed by the Windows setup", refusal);
    }

    [Fact]
    public void ASecondPressWhileApplyingIsRefused()
        => Assert.Contains("already applying", NodeUpdateControl.Refuse("gpu-1", NodeUpdateCommand.KindApply, State(phase: NodeUpdatePhase.Applying)));

    [Fact]
    public void ANodeThatLooksForNothingIsNotAskedToLook()
        => Assert.Contains("looks for no releases", NodeUpdateControl.Refuse("gpu-1", NodeUpdateCommand.KindCheck, State(check: false, allowFromHub: false)));

    [Fact]
    public void AnUnknownKindIsRefused()
        => Assert.Contains("not an update command", NodeUpdateControl.Refuse("gpu-1", "downgrade", State()));

    [Fact]
    public void TheRegistryKeepsTheLastReportAndForgetsADeregisteredNode()
    {
        var registry = new NodeUpdateRegistry();
        registry.Report(State(phase: NodeUpdatePhase.Checking));
        registry.Report(State());

        Assert.Equal(NodeUpdatePhase.Available, registry.Of("GPU-1")!.State);

        registry.Forget("gpu-1");
        Assert.Null(registry.Of("gpu-1"));
    }
}
