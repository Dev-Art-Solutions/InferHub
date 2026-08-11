using InferHub.Coordinator.Vector;

namespace InferHub.Tests.Vector;

public class ReplicaPlacementTests
{
    [Fact]
    public void EmptyFleetReturnsEmpty()
    {
        var target = ReplicaPlacement.ComputeTarget(
            orderedCandidates: Array.Empty<NodeCandidate>(),
            currentHolders: new HashSet<string>(),
            targetCount: 2);

        Assert.Empty(target);
    }

    [Fact]
    public void PlacesUpToTargetSpreadingFirst()
    {
        var candidates = new[]
        {
            new NodeCandidate("conn-a", "node-a", 0),
            new NodeCandidate("conn-b", "node-b", 0),
            new NodeCandidate("conn-c", "node-c", 0),
        };

        var target = ReplicaPlacement.ComputeTarget(candidates, new HashSet<string>(), targetCount: 2);

        Assert.Equal(new[] { "conn-a", "conn-b" }, target.ToArray());
    }

    [Fact]
    public void KeepsAlreadyHeldReplicasBeforeAddingNew()
    {
        var candidates = new[]
        {
            new NodeCandidate("conn-a", "node-a", 0),
            new NodeCandidate("conn-b", "node-b", 0),
            new NodeCandidate("conn-c", "node-c", 0),
        };

        // conn-c already holds; should stay even though conn-a/b come earlier in order.
        var target = ReplicaPlacement.ComputeTarget(
            candidates,
            new HashSet<string> { "conn-c" },
            targetCount: 2);

        Assert.Contains("conn-c", target);
        Assert.Equal(2, target.Count);
    }

    [Fact]
    public void CapsAtCandidateCountWhenTargetExceedsFleet()
    {
        var candidates = new[]
        {
            new NodeCandidate("conn-a", "node-a", 0),
        };

        var target = ReplicaPlacement.ComputeTarget(candidates, new HashSet<string>(), targetCount: 5);

        Assert.Single(target);
        Assert.Equal("conn-a", target[0]);
    }

    [Fact]
    public void ReturnsEmptyWhenTargetIsZero()
    {
        var candidates = new[]
        {
            new NodeCandidate("conn-a", "node-a", 0),
        };

        var target = ReplicaPlacement.ComputeTarget(candidates, new HashSet<string>(), targetCount: 0);

        Assert.Empty(target);
    }
}
