using InferHub.Node.Tools;

namespace InferHub.Tests;

/// <summary>
/// Phase 48, D1/D2. The admission gate is a pure function precisely so this suite can be a table of
/// inputs — it is the piece whose off-by-one costs somebody an out-of-memory error at 2am, and
/// exhaustive is cheap when nothing has to be started.
/// </summary>
public class VramBudgetTests
{
    private static VramBudget.Resident Idle(string model, int mib) => new(model, mib, InUse: false);

    private static VramBudget.Resident Busy(string model, int mib) => new(model, mib, InUse: true);

    /// <summary>
    /// <b>The load-bearing test in this file</b>, and it is the old behaviour rather than the new
    /// one: a deployment that changes no config must behave exactly as it did on v3.15.
    /// </summary>
    [Fact]
    public void ANodeWithNoDeclaredBudgetAdmitsEverything()
    {
        var decision = VramBudget.Evaluate(
            budgetMiB: 0,
            reserveMiB: 2048,
            [Busy("sdxl", 8000)],
            "qwen-image",
            candidateMiB: 60000);

        Assert.Equal(VramBudget.Admission.Admit, decision.Outcome);
        Assert.True(VramBudget.Fits(0, 2048, 60000));
    }

    [Fact]
    public void ARecipeThatExactlyFitsTheHeadroomIsAdmitted()
    {
        // 24576 - 2048 = 22528 exactly.
        var decision = VramBudget.Evaluate(24576, 2048, [], "qwen-image", 22528);

        Assert.Equal(VramBudget.Admission.Admit, decision.Outcome);
    }

    [Fact]
    public void ARecipeThatExceedsTheHeadroomByOneMebibyteIsRefused()
    {
        var decision = VramBudget.Evaluate(24576, 2048, [], "qwen-image", 22529);

        Assert.Equal(VramBudget.Admission.Refuse, decision.Outcome);
        Assert.Contains("22529", decision.Reason);
        Assert.Contains("22528", decision.Reason);

        // …and the same arithmetic decides whether it is declared at all, so nothing routes at it.
        Assert.False(VramBudget.Fits(24576, 2048, 22529));
        Assert.True(VramBudget.Fits(24576, 2048, 22528));
    }

    /// <summary>The reserve is the point of the reserve: it is what Ollama is sitting in.</summary>
    [Fact]
    public void TheReserveIsSubtractedBeforeAnythingIsAdmitted()
    {
        Assert.Equal(VramBudget.Admission.Admit, VramBudget.Evaluate(24576, 0, [], "flux-schnell", 24000).Outcome);
        Assert.Equal(VramBudget.Admission.Refuse, VramBudget.Evaluate(24576, 2048, [], "flux-schnell", 24000).Outcome);
    }

    /// <summary>
    /// The swap case. An idle pipeline is freed by the worker <em>before</em> it allocates the next
    /// one, so it does not count against the candidate — and a model somebody is mid-job on does.
    /// </summary>
    [Fact]
    public void AnIdleResidentIsFreedForTheSwapAndABusyOneIsNot()
    {
        var swap = VramBudget.Evaluate(24576, 2048, [Idle("sdxl", 8000)], "flux-schnell", 20000);
        Assert.Equal(VramBudget.Admission.Admit, swap.Outcome);

        var contended = VramBudget.Evaluate(24576, 2048, [Busy("sdxl", 8000)], "flux-schnell", 20000);
        Assert.Equal(VramBudget.Admission.Wait, contended.Outcome);
        Assert.Contains("8000 MiB is held by work in flight", contended.Reason);
    }

    /// <summary>
    /// <c>Wait</c> and <c>Refuse</c> are not the same answer and must not collapse: one is "come
    /// back shortly" and the other is "this box will never run that".
    /// </summary>
    [Fact]
    public void SomethingThatCannotEverFitIsRefusedRatherThanQueued()
    {
        var decision = VramBudget.Evaluate(24576, 2048, [Busy("sdxl", 8000)], "qwen-image", 60000);

        Assert.Equal(VramBudget.Admission.Refuse, decision.Outcome);
    }

    /// <summary>
    /// Weights that are already on the card cost nothing to use. Refusing here would fail a request
    /// against a pipeline that is sitting right there, because a number in a config file moved.
    /// </summary>
    [Fact]
    public void AModelThatIsAlreadyResidentIsAlwaysAdmitted()
    {
        var decision = VramBudget.Evaluate(8192, 2048, [Idle("qwen-image", 60000)], "qwen-image", 60000);

        Assert.Equal(VramBudget.Admission.Admit, decision.Outcome);
    }

    /// <summary>
    /// A recipe with no <c>vramMiB</c> is admitted rather than guessed at: inventing a figure would
    /// put a model the operator can see on the box behind arithmetic nobody wrote down.
    /// </summary>
    [Fact]
    public void ARecipeThatDeclaresNoVramIsAdmitted()
    {
        Assert.Equal(VramBudget.Admission.Admit, VramBudget.Evaluate(8192, 2048, [], "mystery", 0).Outcome);
        Assert.True(VramBudget.Fits(8192, 2048, 0));
    }

    /// <summary>The validator refuses this configuration; the gate still does not crash on it.</summary>
    [Fact]
    public void AReserveThatSwallowsTheBudgetRefusesRatherThanThrows()
    {
        var decision = VramBudget.Evaluate(2048, 2048, [], "sd15", 4000);

        Assert.Equal(VramBudget.Admission.Refuse, decision.Outcome);
        Assert.Contains("leaves nothing", decision.Reason);
    }

    [Fact]
    public void SeveralBusyResidentsAreSummed()
    {
        var decision = VramBudget.Evaluate(
            49152,
            2048,
            [Busy("sdxl", 8000), Busy("sd15", 4000), Idle("sdxl-turbo", 8000)],
            "qwen-image",
            36000);

        Assert.Equal(VramBudget.Admission.Wait, decision.Outcome);
        Assert.Contains("12000 MiB is held", decision.Reason);
    }

    /// <summary>
    /// The residency map mirrors the worker's own LRU policy, and the part that matters is what it
    /// refuses to evict: a model under a live lease stays, or the gate would admit a second one
    /// onto a card that is busy with the first.
    /// </summary>
    [Fact]
    public void ResidencyEvictsTheLeastRecentlyUsedIdleModelAndNeverABusyOne()
    {
        var residency = new ImageResidency(residentLimit: 1);

        residency.Reserve("sdxl", 8000);
        residency.Release("sdxl");

        residency.Reserve("flux-schnell", 12000);

        var after = residency.Snapshot();
        Assert.Equal("flux-schnell", Assert.Single(after).Model);
        Assert.True(after.Single().InUse);

        // Now the busy one must survive a second reservation, even though the limit is 1.
        residency.Reserve("sd15", 4000);

        Assert.Contains(residency.Snapshot(), r => r.Model == "flux-schnell");
        Assert.Contains(residency.Snapshot(), r => r.Model == "sd15");
    }

    /// <summary>An idle hint frees what is idle. What a lease still covers is left alone.</summary>
    [Fact]
    public void ClearDropsIdleResidentsAndKeepsTheOnesInFlight()
    {
        var residency = new ImageResidency(residentLimit: 4);

        residency.Reserve("sdxl", 8000);
        residency.Release("sdxl");
        residency.Reserve("sd15", 4000);

        residency.Clear();

        var remaining = Assert.Single(residency.Snapshot());
        Assert.Equal("sd15", remaining.Model);
        Assert.True(remaining.InUse);
    }
}
