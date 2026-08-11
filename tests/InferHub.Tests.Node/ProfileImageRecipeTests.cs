using InferHub.Node.Profiles;
using InferHub.Node.Tools;
using InferHub.Shared.Contracts;

namespace InferHub.Tests;

/// <summary>
/// Phase 48. A profile may switch an image recipe off; switching one on is bounded by three things
/// the box owns — whether the recipe exists, whether its licence was accepted, and whether it fits.
/// </summary>
/// <remarks>
/// This is <c>ProfileClampTests</c>'s discipline on the first ceiling in the clamp that is
/// <em>arithmetic</em> rather than a list. The refusals matter as much as the narrowings: a
/// coordinator that could enable a recipe past the VRAM budget would put the out-of-memory error
/// back exactly where D2 took it out of.
/// </remarks>
public class ProfileImageRecipeTests
{
    private static ImageRecipeInfo Recipe(
        string id,
        int vramMiB,
        bool permissive = true,
        string licence = "Apache-2.0") =>
        new(id, licence, permissive, "https://example.invalid/licence", vramMiB, "none");

    private static LocalCeiling Ceiling(
        int budgetMiB = 24576,
        int reserveMiB = 2048,
        params string[] acceptedLicenses) =>
        new(
            DisabledCapabilities: [],
            ToolsEnabled: true,
            AllowedTools: ["diffusion"],
            MaxConcurrency: null,
            SupportsModelManagement: true,
            ImageRecipeCatalogue:
            [
                Recipe("sdxl", 8000),
                Recipe("flux-schnell", 12000),
                Recipe("qwen-image", 19000),
                Recipe("sdxl-turbo", 8000, permissive: false, licence: "sai-nc-community")
            ],
            AcceptedLicenseIds: acceptedLicenses,
            VramBudgetMiB: budgetMiB,
            VramReserveMiB: reserveMiB);

    private static NodeProfile Profile(IReadOnlyDictionary<string, bool> recipes) =>
        new("images", 1, new NodeProfileSelector(NodeId: "node-a"), ImageRecipes: recipes);

    [Fact]
    public void SwitchingARecipeOffIsHonoured()
    {
        var result = NodeProfileClamp.Apply(Ceiling(), Profile(new Dictionary<string, bool>
        {
            ["qwen-image"] = false
        }));

        Assert.Equal(["qwen-image"], result.Effective.DisabledImageRecipes);
        Assert.Contains("image recipe 'qwen-image' off", result.Applied);
        Assert.Empty(result.Refusals);
    }

    /// <summary>
    /// Narrowing is honoured even for a recipe this box has never heard of: the answer to "stop
    /// offering that" is never "I decline to not offer it".
    /// </summary>
    [Fact]
    public void SwitchingOffARecipeThisNodeDoesNotHaveIsStillHonoured()
    {
        var result = NodeProfileClamp.Apply(Ceiling(), Profile(new Dictionary<string, bool>
        {
            ["some-model-nobody-installed"] = false
        }));

        Assert.Equal(["some-model-nobody-installed"], result.Effective.DisabledImageRecipes);
        Assert.Empty(result.Refusals);
    }

    [Fact]
    public void ARecipeThisNodeDoesNotHaveCannotBeSwitchedOn()
    {
        var result = NodeProfileClamp.Apply(Ceiling(), Profile(new Dictionary<string, bool>
        {
            ["sd35-medium"] = true
        }));

        var refusal = Assert.Single(result.Refusals);
        Assert.Equal("imageRecipe:sd35-medium", refusal.Item);
        Assert.Contains("has no image recipe", refusal.Reason);
        Assert.Contains("a profile cannot add one", refusal.Reason);
    }

    /// <summary>
    /// <b>The hub cannot accept a licence on the operator's behalf.</b> That decision lives on the
    /// box, in a key an operator typed, and a coordinator that could grant it would make the whole
    /// consent theatre.
    /// </summary>
    [Fact]
    public void ARecipeWhoseLicenceThisNodeHasNotAcceptedIsRefusedWithTheLicenceNamed()
    {
        var result = NodeProfileClamp.Apply(Ceiling(), Profile(new Dictionary<string, bool>
        {
            ["sdxl-turbo"] = true
        }));

        var refusal = Assert.Single(result.Refusals);
        Assert.Equal("imageRecipe:sdxl-turbo", refusal.Item);
        Assert.Contains("sai-nc-community", refusal.Reason);
        Assert.Contains("Tools:Image:AcceptedLicenses", refusal.Reason);
        Assert.Contains("on the box, not in a profile", refusal.Reason);
    }

    [Fact]
    public void AcceptingTheLicenceOnTheBoxIsWhatLetsAProfileSwitchItOn()
    {
        var result = NodeProfileClamp.Apply(
            Ceiling(acceptedLicenses: "sai-nc-community"),
            Profile(new Dictionary<string, bool> { ["sdxl-turbo"] = true }));

        Assert.Empty(result.Refusals);
        Assert.Contains("image recipe 'sdxl-turbo' on", result.Applied);
        Assert.Empty(result.Effective.DisabledImageRecipes);
    }

    /// <summary>The refusal carries the numbers, because "it does not fit" is not actionable.</summary>
    [Fact]
    public void ARecipeThatDoesNotFitTheBudgetIsRefusedWithTheArithmeticInTheMessage()
    {
        var result = NodeProfileClamp.Apply(
            Ceiling(budgetMiB: 12288),
            Profile(new Dictionary<string, bool> { ["qwen-image"] = true }));

        var refusal = Assert.Single(result.Refusals);
        Assert.Equal("imageRecipe:qwen-image", refusal.Item);
        Assert.Contains("19000", refusal.Reason);
        Assert.Contains("10240", refusal.Reason);
        Assert.Contains("Node:Vram:BudgetMiB", refusal.Reason);
    }

    /// <summary>Refusals are per item (phase-43 D6): a bad one does not undo the good ones.</summary>
    [Fact]
    public void OneRefusedRecipeDoesNotStopTheOthers()
    {
        var result = NodeProfileClamp.Apply(
            Ceiling(budgetMiB: 12288),
            Profile(new Dictionary<string, bool>
            {
                ["qwen-image"] = true,
                ["sdxl-turbo"] = false,
                ["sdxl"] = true
            }));

        Assert.Equal(["sdxl-turbo"], result.Effective.DisabledImageRecipes);
        Assert.Contains("image recipe 'sdxl' on", result.Applied);
        Assert.Single(result.Refusals);
    }

    /// <summary>A profile that says nothing about recipes changes nothing about them.</summary>
    [Fact]
    public void AProfileWithNoImageSectionNarrowsNothing()
    {
        var result = NodeProfileClamp.Apply(
            Ceiling(),
            new NodeProfile("plain", 3, new NodeProfileSelector(NodeId: "node-a"), MaxConcurrency: 2));

        Assert.Empty(result.Effective.DisabledImageRecipes);
        Assert.Empty(result.Refusals);
    }

    /// <summary>
    /// A box with no declared budget has no arithmetic to fail: this phase is inert unless somebody
    /// turned it on, which is the same promise every other key here makes.
    /// </summary>
    [Fact]
    public void WithNoDeclaredBudgetNothingIsRefusedOnVram()
    {
        var result = NodeProfileClamp.Apply(
            Ceiling(budgetMiB: 0, reserveMiB: 0),
            Profile(new Dictionary<string, bool> { ["qwen-image"] = true }));

        Assert.Empty(result.Refusals);
        Assert.Contains("image recipe 'qwen-image' on", result.Applied);
    }

    /// <summary>A hostile profile produces refusals, never a node that falls over.</summary>
    [Fact]
    public void AHostileRecipeIdIsRefusedByName()
    {
        var result = NodeProfileClamp.Apply(Ceiling(), Profile(new Dictionary<string, bool>
        {
            ["../../etc/passwd"] = true,
            ["/opt/inferhub/venv/bin/python"] = true,
            [""] = true
        }));

        Assert.Equal(2, result.Refusals.Count);
        Assert.All(result.Refusals, refusal => Assert.Contains("has no image recipe", refusal.Reason));
        Assert.Empty(result.Effective.DisabledImageRecipes);
    }
}
