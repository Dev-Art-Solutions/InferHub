using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;

namespace InferHub.Tests;

/// <summary>
/// Phase 78 (D2–D4). <see cref="AutoScalerService.ShouldScaleIn"/> and
/// <see cref="AutoScalerService.RoutableNodeCountByModel"/> are the pure decision core, pulled out of
/// the background tick so they are testable without a live <c>INodeRegistry</c>, <c>Metrics</c> or
/// <c>NodeModelToggle</c> — none of which any existing test in this project constructs (phase 76 was
/// live-verified only). The tick itself is still exercised live, per that phase's own note.
/// </summary>
public class AutoScalerScaleInTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);

    /// <summary>D2, the load-bearing decision: never disable the only routable copy of a model.</summary>
    [Fact]
    public void TheOnlyRoutableCopyOfAModelIsNeverAScaleInCandidateNoMatterHowIdle()
    {
        var result = AutoScalerService.ShouldScaleIn(
            routableNodeCount: 1,
            lastAction: null,
            lastServed: Now - TimeSpan.FromDays(30),
            processStartUtc: Now - TimeSpan.FromDays(1),
            now: Now,
            idleAfter: IdleAfter,
            cooldown: Cooldown);

        Assert.False(result);
    }

    [Fact]
    public void ASecondRoutableCopyMakesAnIdlePairAScaleInCandidate()
    {
        var result = AutoScalerService.ShouldScaleIn(
            routableNodeCount: 2,
            lastAction: null,
            lastServed: Now - TimeSpan.FromMinutes(90),
            processStartUtc: Now - TimeSpan.FromDays(1),
            now: Now,
            idleAfter: IdleAfter,
            cooldown: Cooldown);

        Assert.True(result);
    }

    [Fact]
    public void APairServedInsideTheIdleWindowIsNotACandidate()
    {
        var result = AutoScalerService.ShouldScaleIn(
            routableNodeCount: 2,
            lastAction: null,
            lastServed: Now - TimeSpan.FromMinutes(5),
            processStartUtc: Now - TimeSpan.FromDays(1),
            now: Now,
            idleAfter: IdleAfter,
            cooldown: Cooldown);

        Assert.False(result);
    }

    /// <summary>D3: never served at all counts as idle since process start, not idle forever.</summary>
    [Fact]
    public void ANeverServedPairIsIdleSinceProcessStart()
    {
        var justPastIdle = AutoScalerService.ShouldScaleIn(
            routableNodeCount: 2,
            lastAction: null,
            lastServed: null,
            processStartUtc: Now - TimeSpan.FromMinutes(61),
            now: Now,
            idleAfter: IdleAfter,
            cooldown: Cooldown);

        var justShortOfIdle = AutoScalerService.ShouldScaleIn(
            routableNodeCount: 2,
            lastAction: null,
            lastServed: null,
            processStartUtc: Now - TimeSpan.FromMinutes(30),
            now: Now,
            idleAfter: IdleAfter,
            cooldown: Cooldown);

        Assert.True(justPastIdle);
        Assert.False(justShortOfIdle);
    }

    /// <summary>D4: the cooldown map is shared with scale-out and checked in both directions.</summary>
    [Fact]
    public void APairToggledInsideTheCooldownWindowIsSkippedRegardlessOfIdleTime()
    {
        var result = AutoScalerService.ShouldScaleIn(
            routableNodeCount: 2,
            lastAction: Now - TimeSpan.FromMinutes(5),
            lastServed: Now - TimeSpan.FromDays(1),
            processStartUtc: Now - TimeSpan.FromDays(1),
            now: Now,
            idleAfter: IdleAfter,
            cooldown: Cooldown);

        Assert.False(result);
    }

    [Fact]
    public void APairToggledJustOutsideTheCooldownWindowIsEligibleAgain()
    {
        var result = AutoScalerService.ShouldScaleIn(
            routableNodeCount: 2,
            lastAction: Now - TimeSpan.FromMinutes(16),
            lastServed: Now - TimeSpan.FromDays(1),
            processStartUtc: Now - TimeSpan.FromDays(1),
            now: Now,
            idleAfter: IdleAfter,
            cooldown: Cooldown);

        Assert.True(result);
    }

    [Fact]
    public void RoutableNodeCountByModelCountsOnlyHealthyUncordonedNodesAndDeduplicatesByNode()
    {
        var snapshots = new[]
        {
            Node("a", cordoned: false, health: BackendHealth.Healthy, ["llama3"]),
            Node("b", cordoned: false, health: BackendHealth.Healthy, ["llama3", "nomic-embed-text"]),
            Node("c", cordoned: true, health: BackendHealth.Healthy, ["llama3"]), // cordoned: excluded
            Node("d", cordoned: false, health: BackendHealth.Wedged, ["llama3"]), // unhealthy: excluded
            Node("e", cordoned: false, health: null, ["llama3"]),                 // no opinion: counted
        };

        var counts = AutoScalerService.RoutableNodeCountByModel(snapshots);

        Assert.Equal(3, counts["llama3"]);
        Assert.Equal(1, counts["nomic-embed-text"]);
    }

    private static NodeSnapshot Node(string id, bool cordoned, BackendHealth? health, IReadOnlyList<string> models) =>
        new(
            ConnectionId: id,
            NodeId: id,
            Name: id,
            OllamaEndpoint: "http://localhost:11434",
            Version: "v3.43.0",
            LastSeenUtc: Now,
            AgeSeconds: 0,
            InFlight: 0,
            LocalInFlight: 0,
            ModelCount: models.Count,
            Labels: new Dictionary<string, string>(),
            MaxConcurrency: null,
            Cordoned: cordoned,
            SupportsModelManagement: true,
            Capabilities: [new NodeCapability(CapabilityKinds.Chat, models)],
            BackendHealth: health);
}
