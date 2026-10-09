using System.Text.Json;
using InferHub.Shared.Contracts;
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

    [Fact]
    public void PicturesTravelAsOptionalFieldsBothVersionsRead()
    {
        // Phase 100: a v3.65 hub's install names setup's --vision; a v3.64 node's command has none, which is its own default.
        var command = new ModelCommand(Guid.NewGuid(), ModelCommand.KindPull, "ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-Coder-GGUF:IQ1_M", Engine: ModelCommand.EngineHuggingFace, Vision: "yes");
        Assert.Contains("\"vision\":\"yes\"", JsonSerializer.Serialize(command));
        Assert.Null(JsonSerializer.Deserialize<ModelCommand>("""{"commandId":"6f2b8c1e-0000-0000-0000-000000000001","kind":"pull","modelName":"x","engine":"huggingface"}""")!.Vision);

        var model = JsonSerializer.Deserialize<NodeCatalogModel>("""{"name":"strata-q2_0","state":"unloaded","pinned":false,"inFlight":0,"images":true}""")!;
        Assert.True(model.Images);
        Assert.Null(JsonSerializer.Deserialize<NodeCatalogModel>("""{"name":"m","state":"unloaded","pinned":false,"inFlight":0}""")!.Images);

        // A refused job's 4xx rides the result and the stream's failure chunk; a v3.64 node's has none and stays a 502.
        Assert.Equal(400, JsonSerializer.Deserialize<InferenceResult>(JsonSerializer.Serialize(InferenceResult.Refused(Guid.NewGuid(), "no", 400)))!.Status);
        Assert.Equal(502, InferenceResult.HttpStatusOf(JsonSerializer.Deserialize<InferenceResult>("""{"jobId":"6f2b8c1e-0000-0000-0000-000000000001","success":false,"error":"x"}""")!.Status));
        Assert.True(InferHub.Shared.OpenAi.OpenAiSse.TryReadFailure("""{"error":"no","status":400,"done":true}""", out _, out var status));
        Assert.Equal(400, status);
        Assert.True(InferHub.Shared.OpenAi.OpenAiSse.TryReadFailure("""{"error":"no","done":true}""", out _, out status));
        Assert.Equal(502, status);

        Assert.True(ModelCommand.IsKnownVision(null));
        Assert.True(ModelCommand.WantsImages("cpu"));
        Assert.False(ModelCommand.WantsImages("none"));
        Assert.False(ModelCommand.IsKnownVision("--build"));
    }
}
