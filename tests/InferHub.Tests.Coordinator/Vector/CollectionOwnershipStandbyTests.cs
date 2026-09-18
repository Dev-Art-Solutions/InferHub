using InferHub.Coordinator.Vector;

namespace InferHub.Tests;

/// <summary>Phase 77: <see cref="CollectionOwnership"/>'s standby bookkeeping, in isolation from the SignalR relay.</summary>
public class CollectionOwnershipStandbyTests
{
    [Fact]
    public void AssigningAStandbyForAHubOwnedCollectionIsRefused()
    {
        var ownership = new CollectionOwnership();

        var error = ownership.AssignStandby("docs", "node-b");

        Assert.NotNull(error);
        Assert.Null(ownership.StandbyNodeOf("docs"));
    }

    [Fact]
    public void ANodeCannotBeItsOwnStandby()
    {
        var ownership = new CollectionOwnership();
        ownership.Assign("node-a", ["docs"]);

        var error = ownership.AssignStandby("docs", "node-a");

        Assert.NotNull(error);
        Assert.Null(ownership.StandbyNodeOf("docs"));
    }

    [Fact]
    public void AValidStandbyIsRecordedAndSurvivesAnUnrelatedRebuild()
    {
        var ownership = new CollectionOwnership();
        ownership.Assign("node-a", ["docs"]);

        var error = ownership.AssignStandby("docs", "node-b");

        Assert.Null(error);
        Assert.Equal("node-b", ownership.StandbyNodeOf("docs"));

        // Rebuild is what an unrelated profile edit triggers (D2): it re-derives owners from scratch
        // but must not touch admin-set standby assignments, unlike the ownership hazard phase-77's
        // own research found in the owners dictionary.
        ownership.Rebuild([("node-a", null)]);

        Assert.Equal("node-b", ownership.StandbyNodeOf("docs"));
    }

    [Fact]
    public void ClearingAStandbyRemovesIt()
    {
        var ownership = new CollectionOwnership();
        ownership.Assign("node-a", ["docs"]);
        ownership.AssignStandby("docs", "node-b");

        ownership.ClearStandby("docs");

        Assert.Null(ownership.StandbyNodeOf("docs"));
    }

    [Fact]
    public void StandbyAssignmentsReportsOwnerAndStandbyTogether()
    {
        var ownership = new CollectionOwnership();
        ownership.Assign("node-a", ["docs"]);
        ownership.AssignStandby("docs", "node-b");

        var (owner, standby) = ownership.StandbyAssignments()["docs"];

        Assert.Equal("node:node-a", owner);
        Assert.Equal("node-b", standby);
    }
}
