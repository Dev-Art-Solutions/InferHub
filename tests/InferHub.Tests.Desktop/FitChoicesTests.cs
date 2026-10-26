using System.Text.Json.Nodes;
using InferHub.Desktop;

namespace InferHub.Tests;

/// <summary>Phase 118 in the app: the context the owner types, the quick picks, and the node's answer as one row per engine.</summary>
public sealed class FitChoicesTests
{
    [Theory]
    [InlineData("32k", 32768)]
    [InlineData("128K", 131072)]
    [InlineData("1M", 1048576)]
    [InlineData("40960", 40960)]
    [InlineData("40,960", 40960)]
    [InlineData("1.5k", 1536)]
    [InlineData("", null)]
    [InlineData("lots", null)]
    [InlineData("0", null)]
    public void AContextIsTokensWithKAndMAsTheyAreSaid(string text, int? expected) => Assert.Equal(expected, FitChoices.ParseContext(text));

    [Fact]
    public void ThePicksStopAtTheModelsOwnContextAndIncludeIt()
    {
        Assert.Equal([4096, 8192, 16384, 32768, 40960], FitChoices.Picks(40960));
        Assert.Equal(262144, FitChoices.Picks(null)[^1]);
        Assert.Equal("40K", FitChoices.FormatContext(40960));
        Assert.Equal("40,000", FitChoices.FormatContext(40000));
        Assert.Equal("32K", FitChoices.FormatContext(32768));
    }

    private static readonly JsonObject Answer = (JsonObject)JsonNode.Parse("""
        {
          "link": "o/r",
          "model": {"name": "o/r", "parameters": 30532122624, "experts": 128, "expertsUsed": 8, "trainedContext": 40960, "notes": []},
          "machine": {"gpus": [{"name": "RTX 3090 Ti", "totalMiB": 24564, "freeMiB": 396}], "ramMiB": 261985, "cores": 64},
          "options": [
            {"engine": "llamacpp", "variant": "Q4_K_M", "available": true, "fits": true, "placement": "experts-in-ram", "vramMiB": 8000, "ramMiB": 15000, "maxContext": 40960, "cardContext": 30000, "speed": "medium", "why": ["a", "b"]},
            {"engine": "ollama", "variant": "Q4_K_M", "available": true, "fits": true, "placement": "split", "vramMiB": 23336, "ramMiB": 2000, "maxContext": 40960, "speed": "slow", "why": ["c"]},
            {"engine": "llamacpp", "variant": "Q8_0", "available": true, "fits": true, "placement": "split", "vramMiB": 23336, "ramMiB": 9000, "maxContext": 40960, "speed": "very slow", "why": []},
            {"engine": "llamacpp", "variant": "BF16", "available": true, "fits": false, "placement": "none", "why": ["too big"]}
          ],
          "refused": [{"engine": "colibri", "reason": "a GGUF"}]
        }
        """)!;

    [Fact]
    public void EachEngineIsOneRowWithItsBestOptionAndTheOtherSizesThatFit()
    {
        var rows = FitChoices.Rows(Answer);

        Assert.Equal(["llamacpp", "ollama"], rows.Select(r => r.Engine));
        Assert.Equal("Q4_K_M", rows[0].Variant);
        Assert.Equal(["Q8_0"], rows[0].OtherVariants);
        Assert.Equal("llama.cpp · Q4_K_M · card 7.8 GiB + experts in RAM 15 GiB · medium · up to 40K (30,000 on the card)", rows[0].Headline);
        Assert.Equal(["a", "b"], rows[0].Why);
    }

    [Fact]
    public void TheModelTheMachineAndTheRefusalsAreOneLineEach()
    {
        Assert.Equal("o/r · 30.5B parameters · 128 experts, 8 a token · trained for 40K", FitChoices.Model(Answer));
        Assert.Equal("RTX 3090 Ti 24 GiB (396 MiB free now) · RAM 256 GiB · 64 cores", FitChoices.Machine(Answer));
        Assert.Equal(["colibri: a GGUF"], FitChoices.Refused(Answer));
        Assert.Equal("llama.cpp · BF16 · does not fit", FitChoices.Headline(FitChoices.Option(Answer, "llamacpp", "BF16")!));
    }
}
