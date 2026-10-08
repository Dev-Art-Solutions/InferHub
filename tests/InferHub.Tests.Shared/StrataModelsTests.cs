using InferHub.Shared.HuggingFace;
using InferHub.Shared.Strata;

namespace InferHub.Tests;

/// <summary>
/// Phase 99 D4: a Hugging Face link to one of Strata's repos names a family and a size, the same way
/// on the hub and on the node; anything Strata's installer would not take is refused with the list.
/// </summary>
public class StrataModelsTests
{
    [Theory]
    [InlineData("ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-GGUF:IQ2_XS", "qwen", "IQ2_XS", "strata-iq2_xs")]
    [InlineData("https://huggingface.co/ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-GGUF/tree/main/IQ3_XXS", null, null, null)]
    [InlineData("https://huggingface.co/ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-GGUF/blob/main/IQ3_XXS/Qwen3.8-Flash-Next-GSQ-RCO-IQ3_XXS-00001-of-00002.gguf", "qwen", "IQ3_XXS", "strata-iq3_xxs")]
    [InlineData("hf.co/ukisai/Swift-1.5-Qwen3.8-Flash-Next-GSQ-RCO-GGUF:iq2_xs", "swift", "IQ2_XS", "strata-swift-iq2_xs")]
    [InlineData("ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-Coder-GGUF", "coder", "IQ1_M", "strata-coder-iq1_m")]
    [InlineData("unsloth/Qwen3.8-Flash-Next-GGUF:UD-IQ4_XS", "unsloth", "UD-IQ4_XS", "strata-unsloth-ud-iq4_xs")]
    [InlineData("https://huggingface.co/unsloth/Qwen3.8-Flash-Next-GGUF/resolve/main/UD-Q4_K_XL/Qwen3.8-Flash-Next-UD-Q4_K_XL-00001-of-00004.gguf", "unsloth", "UD-Q4_K_XL", "strata-unsloth-ud-q4_k_xl")]
    public void ALinkNamesAFamilyAndASize(string link, string? family, string? size, string? name)
    {
        Assert.True(HfReference.TryParse(link, null, out var reference, out var parseError), parseError);
        Assert.True(StrataModels.IsStrataRepo(reference!));

        var matched = StrataModels.TryMatch(reference!, out var f, out var s, out var error);

        if (family is null)
        {
            // A /tree/ link carries no file and no quant: the qwen repo has four sizes, so it must say which.
            Assert.False(matched);
            Assert.Contains("Q2_0, IQ2_XS, IQ3_XXS, IQ3_S", error);
            return;
        }

        Assert.True(matched, error);
        Assert.Equal(family, f!.Name);
        Assert.Equal(size, s!.Name);
        Assert.Equal(name, StrataModels.CatalogName(f, s));
    }

    [Fact]
    public void IQ3_XXSIsNotReadAsAShorterSize()
    {
        HfReference.TryParse("ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-GGUF//IQ3_XXS/Qwen3.8-Flash-Next-GSQ-RCO-IQ3_XXS-00002-of-00002.gguf", null, out var reference, out _);

        Assert.True(StrataModels.TryMatch(reference!, out _, out var size, out _));
        Assert.Equal("IQ3_XXS", size!.Name);
    }

    [Fact]
    public void ASizeAFamilyDoesNotHaveIsRefusedWithItsSizes()
    {
        // Swift 1.5 has no IQ3_S and no Q2_0 (setup.py's MODELS families).
        HfReference.TryParse("ukisai/Swift-1.5-Qwen3.8-Flash-Next-GSQ-RCO-GGUF:IQ3_S", null, out var reference, out _);

        Assert.False(StrataModels.TryMatch(reference!, out _, out _, out var error));
        Assert.Contains("IQ2_XS, IQ3_XXS", error);
    }

    [Fact]
    public void ARevisionIsRefusedBecauseSetupPinsItsOwn()
    {
        HfReference.TryParse("ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-Coder-GGUF@abc123", null, out var reference, out _);

        Assert.False(StrataModels.TryMatch(reference!, out _, out _, out var error));
        Assert.Contains("pins", error);
    }

    [Fact]
    public void AnyOtherRepoIsNotStrata()
    {
        HfReference.TryParse("bartowski/Qwen2.5-7B-Instruct-GGUF:Q4_K_M", null, out var reference, out _);

        Assert.False(StrataModels.IsStrataRepo(reference!));
        Assert.False(StrataModels.TryMatch(reference!, out _, out _, out var error));
        Assert.Contains("not one of Strata's repos", error);
    }

    [Fact]
    public void EveryLinkTheTableWritesParsesBackToItself()
    {
        foreach (var (family, size) in StrataModels.All())
        {
            Assert.True(HfReference.TryParse(StrataModels.Link(family, size), null, out var reference, out var error), error);
            Assert.True(StrataModels.TryMatch(reference!, out var f, out var s, out error), error);
            Assert.Same(family, f);
            Assert.Same(size, s);
        }

        Assert.Equal(9, StrataModels.All().Count());
    }
}
