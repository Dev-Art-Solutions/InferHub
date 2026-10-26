using System.Text.Json;
using InferHub.Shared.Fit;

namespace InferHub.Tests;

/// <summary>
/// Phase 118 (D1–D3): the GGUF header read from a real-format file, the facts drawn from it and from a
/// <c>config.json</c>, and the estimate's placements, limits, ranking and context ceilings.
/// </summary>
public sealed class GgufHeaderTests
{
    [Fact]
    public async Task TheHeaderKeepsScalarsAndSmallIntegerArraysAndSkipsAVocabulary()
    {
        var vocabulary = Enumerable.Range(0, 20_000).Select(i => $"tok{i}").ToArray();
        var file = GgufWriter.Build(
        [
            ("general.architecture", "qwen3moe"),
            ("qwen3moe.block_count", 48),
            ("qwen3moe.rope.freq_base", 1_000_000.0),
            ("qwen3moe.attention.head_count_kv", new long[] { 4, 0, 4, 0 }),
            ("tokenizer.ggml.tokens", vocabulary),
            ("general.chat_template", new string('x', 10_000)),
            ("general.big", 1L << 40)
        ], [("a", 100), ("b", 50)]);

        var header = await GgufHeader.ReadAsync(new MemoryStream(file));

        Assert.Equal("qwen3moe", header.Architecture);
        Assert.Equal(48, header.Integer("qwen3moe.block_count"));
        Assert.Equal(1L << 40, header.Integer("general.big"));
        Assert.Equal([4, 0, 4, 0], header.Integers("qwen3moe.attention.head_count_kv")!);
        Assert.False(header.Values.ContainsKey("tokenizer.ggml.tokens"));
        Assert.Null(header.Text("general.chat_template"));
        Assert.Equal(["a", "b"], header.Tensors.Select(t => t.Name));
        Assert.Equal(0, header.DataStart % 32);

        // The last tensor's bytes run to the end of the file; the first is its aligned slot.
        var sizes = header.TensorBytes(file.Length);
        Assert.Equal(128, sizes[0].Bytes);
        Assert.Equal(file.Length - header.DataStart - 128, sizes[1].Bytes);
    }

    [Fact]
    public async Task AReadThatEndsInsideThePairsThrowsSoTheReaderAsksForMore()
    {
        var file = GgufWriter.Llama(vocabulary: Enumerable.Range(0, 5000).Select(i => $"t{i}").ToArray());

        await Assert.ThrowsAsync<EndOfStreamException>(() => GgufHeader.ReadAsync(new MemoryStream(file[..2000])));
    }

    [Fact]
    public async Task AReadThatEndsInsideTheTensorListStillGivesThePairs()
    {
        var file = GgufWriter.Llama(layers: 40);
        var full = await GgufHeader.ReadAsync(new MemoryStream(file));
        var cut = (int)full.DataStart - 200;

        var header = await GgufHeader.ReadAsync(new MemoryStream(file[..cut]));

        Assert.Equal(40, header.Integer("llama.block_count"));
        Assert.Empty(header.Tensors);
        Assert.Equal(-1, header.DataStart);
    }

    [Fact]
    public async Task NotAGgufIsRefused()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => GgufHeader.ReadAsync(new MemoryStream("<html>404</html>"u8.ToArray())));
    }
}

public sealed class ModelFactsTests
{
    private static async Task<ModelFacts> Facts(byte[] file, long? parameters = null)
    {
        var header = await GgufHeader.ReadAsync(new MemoryStream(file));
        return ModelFacts.FromGguf(ModelFacts.SourceGguf, "m", header, file.Length, [new ModelVariant("Q4_K_M", file.Length)], parameters);
    }

    [Fact]
    public async Task KvPerTokenIsLayersTimesKvHeadsTimesKeyPlusValueTimesTwoBytes()
    {
        // Qwen3-30B-A3B's own numbers: 48 layers, 4 KV heads of 128 — 96 KiB a token.
        var facts = await Facts(GgufWriter.Llama(arch: "qwen3moe", layers: 48, heads: 32, kvHeads: 4, keyLength: 128));

        Assert.Equal(48 * 4 * 256 * 2, facts.KvBytesPerToken);
        Assert.Equal(98304, facts.KvBytesPerToken);
    }

    [Fact]
    public async Task APerLayerKvHeadListWithZerosIsAHybridAndOnlyItsAttentionLayersCount()
    {
        var file = GgufWriter.Build(
        [
            ("general.architecture", "nemotron_h"),
            ("nemotron_h.block_count", 4),
            ("nemotron_h.attention.head_count", 8),
            ("nemotron_h.attention.head_count_kv", new long[] { 0, 2, 0, 2 }),
            ("nemotron_h.attention.key_length", 64),
            ("nemotron_h.attention.value_length", 64)
        ]);

        var facts = await Facts(file);

        Assert.Equal(2, facts.AttentionLayers);
        Assert.Equal(2 * 2 * 128 * 2, facts.KvBytesPerToken);
    }

    [Fact]
    public async Task AHybridThatSaysNothingInItsKeysIsCountedFromItsTensors()
    {
        // Qwen3-Next: every fourth block has attn_k; the linear ones have ssm_* and an attn_qkv that is not attention.
        var tensors = new List<(string, long)>();

        for (var i = 0; i < 8; i++)
        {
            tensors.Add(($"blk.{i}.attn_norm.weight", 32));
            tensors.AddRange((i + 1) % 4 == 0
                ? [($"blk.{i}.attn_k.weight", 64)]
                : [($"blk.{i}.attn_qkv.weight", 64), ($"blk.{i}.ssm_a", 32)]);
        }

        var file = GgufWriter.Build(
        [
            ("general.architecture", "qwen3next"),
            ("qwen3next.block_count", 8),
            ("qwen3next.attention.head_count", 16),
            ("qwen3next.attention.head_count_kv", 2),
            ("qwen3next.attention.key_length", 256),
            ("qwen3next.attention.value_length", 256)
        ], tensors);

        var facts = await Facts(file);

        Assert.Equal(2, facts.AttentionLayers);
        Assert.Contains(facts.Notes, n => n.Contains("counted from the tensors"));
    }

    [Fact]
    public async Task AFusedQkvWithoutRecurrentTensorsIsRealAttention()
    {
        var tensors = Enumerable.Range(0, 4).SelectMany(i => new[] { ($"blk.{i}.attn_norm.weight", 32L), ($"blk.{i}.attn_qkv.weight", 64L) });
        var file = GgufWriter.Build(
        [
            ("general.architecture", "phi3"),
            ("phi3.block_count", 4),
            ("phi3.attention.head_count", 8),
            ("phi3.attention.head_count_kv", 8),
            ("phi3.embedding_length", 512)
        ], tensors);

        var facts = await Facts(file);

        Assert.Equal(4, facts.AttentionLayers);
        Assert.Equal(8 * (64 + 64), facts.KvWidth);
    }

    [Fact]
    public async Task MlaCachesTheLatentAndTheRopePartNotTheHeads()
    {
        var file = GgufWriter.Build(
        [
            ("general.architecture", "deepseek2"),
            ("deepseek2.block_count", 61),
            ("deepseek2.attention.head_count", 128),
            ("deepseek2.attention.head_count_kv", 128),
            ("deepseek2.attention.key_length", 192),
            ("deepseek2.attention.value_length", 128),
            ("deepseek2.attention.kv_lora_rank", 512),
            ("deepseek2.rope.dimension_count", 64)
        ]);

        var facts = await Facts(file);

        Assert.Equal(576, facts.KvWidth);
        Assert.Equal(61L * 576 * 2, facts.KvBytesPerToken);
    }

    [Fact]
    public async Task TheExpertsShareIsTheirTensorsBytesOverAll()
    {
        var facts = await Facts(GgufWriter.Llama(experts: 64, denseBytes: 1024 * 10, expertBytes: 1024 * 22));

        Assert.True(facts.IsMoe);
        Assert.InRange(facts.ExpertShare!.Value, 0.85, 0.92);
    }

    [Fact]
    public void AConfigGivesTheSameNumbersForACheckpoint()
    {
        using var config = JsonDocument.Parse("""
            {"model_type":"olmoe","num_hidden_layers":16,"num_attention_heads":16,"num_key_value_heads":16,"hidden_size":2048,
             "num_experts":64,"num_experts_per_tok":8,"intermediate_size":1024,"max_position_embeddings":4096}
            """);

        var facts = ModelFacts.FromConfig("allenai/OLMoE", config.RootElement, 6_900_000_000, [new ModelVariant("checkpoint", 13_800_000_000)], "text-generation");

        Assert.Equal(16 * 16 * 128 * 2 * 2, facts.KvBytesPerToken);
        Assert.Equal(4096, facts.TrainedContext);
        Assert.Equal(64, facts.Experts);
        Assert.InRange(facts.ExpertShare!.Value, 0.9, 0.98);
    }

    [Fact]
    public void AMultimodalConfigIsReadFromItsTextConfig()
    {
        using var config = JsonDocument.Parse("""{"model_type":"gemma3","text_config":{"num_hidden_layers":34,"num_attention_heads":8,"num_key_value_heads":4,"head_dim":256}}""");

        var facts = ModelFacts.FromConfig("google/gemma-3-4b-it", config.RootElement, 4_300_000_000, [], "image-text-to-text");

        Assert.Equal(34, facts.Layers);
        Assert.Equal(4 * 256 * 2, facts.KvWidth);
    }
}

public sealed class FitEstimatorTests
{
    private const long Gi = 1L << 30;

    private static ModelFacts Model(long bytes, double share = 0, int experts = 0, long kvPerTokenLayers = 48, int width = 1024, string source = ModelFacts.SourceGguf, long? parameters = null, int files = 1, int trained = 131072)
        => new(source, "m", "arch", null, parameters ?? bytes * 2, (int)kvPerTokenLayers, (int)kvPerTokenLayers, width, trained, experts, experts / 8, share,
            [new ModelVariant("Q4_K_M", bytes, files, Link: "o/r:Q4_K_M")], []);

    private static MachineFacts Machine(long vramMiB = 24576, long ramMiB = 65536, long? diskMiB = 1 << 22, long? vramLimit = null, long? ramLimit = null, bool installs = true)
        => new(vramMiB > 0 ? [new GpuFacts(0, "card", vramMiB, vramMiB)] : [], ramMiB, ramMiB, 16,
            FitEngines.All.Select(e => new EngineFacts(e, null, installs, installs ? null : "not here", "/m", diskMiB, vramLimit, ramLimit)).ToArray(), []);

    private static FitOption Of(IEnumerable<FitOption> options, string engine) => options.First(o => o.Engine == engine);

    [Fact]
    public void ASmallModelIsAllOnTheCardAndTheArithmeticIsShown()
    {
        var options = FitEstimator.Estimate(Model(8 * Gi), Machine(), 8192);
        var llama = Of(options, FitEngines.LlamaCpp);

        Assert.True(llama.Fits);
        Assert.Equal(FitPlacements.Gpu, llama.Placement);
        Assert.Equal("fast", llama.Speed);
        Assert.Equal(768, llama.KvMiB); // 8192 × 48 layers × 1024 values × 2 bytes
        Assert.Contains(llama.Why, w => w.Contains("weights 8 GiB") && w.Contains("KV for 8,192 tokens"));
        Assert.DoesNotContain(options, o => o.Engine is FitEngines.Colibri or FitEngines.Strata);
    }

    [Fact]
    public void TheContextTheOwnerChoosesIsWhatMovesAMixtureOfExpertsOffTheCard()
    {
        var model = Model(30 * Gi, share: 0.9, experts: 128, width: 2048);

        var small = Of(FitEstimator.Estimate(model, Machine(), 4096), FitEngines.LlamaCpp);
        var large = Of(FitEstimator.Estimate(model, Machine(), 131072), FitEngines.LlamaCpp);

        Assert.Equal(FitPlacements.ExpertsInRam, small.Placement);
        Assert.Equal("medium", small.Speed);
        Assert.Equal(FitPlacements.Split, large.Placement);
        Assert.True(small.CardContext is > 4096 and < 131072, $"card context {small.CardContext}");
    }

    [Fact]
    public void OllamaSplitsAMixtureOfExpertsByLayersAndSaysLlamaCppWouldNot()
    {
        var options = FitEstimator.Estimate(Model(30 * Gi, share: 0.9, experts: 128), Machine(), 4096);
        var ollama = Of(options, FitEngines.Ollama);

        Assert.Equal(FitPlacements.Split, ollama.Placement);
        Assert.Contains(ollama.Why, w => w.Contains("llama.cpp can keep"));
        Assert.True(Of(options, FitEngines.LlamaCpp).Score > ollama.Score);
    }

    [Fact]
    public void WithNoCardItRunsOnTheCpuIfRamHoldsIt()
    {
        var option = Of(FitEstimator.Estimate(Model(4 * Gi), Machine(vramMiB: 0, ramMiB: 32768), 4096), FitEngines.LlamaCpp);

        Assert.Equal(FitPlacements.Cpu, option.Placement);
        Assert.Equal(0, option.VramMiB);
    }

    [Fact]
    public void TooBigForCardAndRamTogetherDoesNotFitAndSaysTheNumbers()
    {
        var option = Of(FitEstimator.Estimate(Model(200 * Gi), Machine(ramMiB: 65536), 8192), FitEngines.LlamaCpp);

        Assert.False(option.Fits);
        Assert.Equal(FitPlacements.None, option.Placement);
        Assert.Contains(option.Why, w => w.Contains("together are not enough"));
        Assert.Null(option.MaxContext);
    }

    [Fact]
    public void TheOwnersLimitForTheRuntimeIsTheCeiling()
    {
        var option = Of(FitEstimator.Estimate(Model(8 * Gi), Machine(vramLimit: 4096), 8192), FitEngines.LlamaCpp);

        Assert.Equal(FitPlacements.Split, option.Placement);
        Assert.Contains(option.Why, w => w.Contains("limits llamacpp to 4 GiB of VRAM"));
    }

    [Fact]
    public void NotEnoughDiskIsNotAFit()
    {
        var option = Of(FitEstimator.Estimate(Model(8 * Gi), Machine(diskMiB: 1024), 8192), FitEngines.Ollama);

        Assert.False(option.Fits);
        Assert.StartsWith("needs 8 GiB of disk", option.Why[0]);
    }

    [Fact]
    public void OllamaDoesNotTakeASplitGguf()
    {
        var options = FitEstimator.Estimate(Model(8 * Gi, files: 3), Machine(), 8192);

        Assert.False(Of(options, FitEngines.Ollama).Fits);
        Assert.True(Of(options, FitEngines.LlamaCpp).Fits);
    }

    [Fact]
    public void AnEngineTheNodeLacksIsShownButNotAvailableAndRanksBelowOneItHas()
    {
        var options = FitEstimator.Estimate(Model(8 * Gi), Machine(installs: false), 8192);

        Assert.All(options, o => Assert.False(o.Available));
        Assert.All(options, o => Assert.Equal("not here", o.Why[0]));
    }

    [Fact]
    public void TheLargestContextIsCappedAtTheModelsOwn()
    {
        var option = Of(FitEstimator.Estimate(Model(1 * Gi, trained: 32768), Machine(), 8192), FitEngines.LlamaCpp);

        Assert.Equal(32768, option.MaxContext);
        Assert.Equal(32768, option.CardContext);
    }

    [Fact]
    public void PastTheTrainedContextItSaysSo()
    {
        var option = Of(FitEstimator.Estimate(Model(1 * Gi, trained: 32768), Machine(), 65536), FitEngines.LlamaCpp);

        Assert.Contains(option.Why, w => w.Contains("trained for 32,768 tokens"));
    }

    [Fact]
    public void OnTheCardTheBiggerQuantRanksFirstAndBelowThreeBitsLast()
    {
        var parameters = 20 * Gi;
        var model = new ModelFacts(ModelFacts.SourceGguf, "m", "a", null, parameters, 32, 32, 512, 32768, 0, 0, 0,
            [new ModelVariant("IQ2_XXS", parameters * 2 / 8), new ModelVariant("Q4_K_M", parameters * 48 / 80), new ModelVariant("Q8_0", parameters * 85 / 80)], []);

        var options = FitEstimator.Estimate(model, Machine(vramMiB: 81920), 4096).Where(o => o.Engine == FitEngines.LlamaCpp).ToArray();

        // All on the card: the 8-bit is first (up to six bits counts), the 2-bit last though it is the smallest.
        Assert.Equal(["Q8_0", "Q4_K_M", "IQ2_XXS"], options.Select(o => o.Variant));
    }

    [Fact]
    public void AFourBitModelSplitAcrossTheBusRanksAboveAOneBitOneOnTheCard()
    {
        // Found live (Qwen3-Next, Ollama): a 1.7-bit quant "all on the card, fast" came first.
        var parameters = 80 * Gi;
        var model = new ModelFacts(ModelFacts.SourceGguf, "m", "a", null, parameters, 48, 12, 1024, 262144, 512, 10, 0,
            [new ModelVariant("UD-TQ1_0", parameters * 17 / 80), new ModelVariant("Q4_K_M", parameters * 48 / 80)], []);

        var options = FitEstimator.Estimate(model, Machine(), 8192).Where(o => o.Engine == FitEngines.Ollama).ToArray();

        Assert.Equal(FitPlacements.Gpu, options.Single(o => o.Variant == "UD-TQ1_0").Placement);
        Assert.Equal("Q4_K_M", options[0].Variant);
    }

    [Fact]
    public void ColibriOnVulkanPutsTheDenseChainAndAHotTierOnTheCard()
    {
        var moe = Model(600 * Gi, share: 0.95, experts: 288, source: ModelFacts.SourceCheckpoint, parameters: 321_000_000_000);
        var machine = Machine(ramMiB: 262144);
        machine = machine with { Engines = machine.Engines.Select(e => e.Kind == FitEngines.Colibri ? e with { Accelerator = "vulkan" } : e).ToArray() };

        var option = Of(FitEstimator.Estimate(moe, machine, 32768), FitEngines.Colibri);

        Assert.Equal(FitPlacements.Ram, option.Placement);
        Assert.Equal("medium", option.Speed);
        Assert.InRange(option.VramMiB, 20000, 23348);
        Assert.Contains(option.Why, w => w.StartsWith("Vulkan: the dense chain on the card"));
    }

    [Fact]
    public void OllamaWithParallelSlotsKeepsAContextForEach()
    {
        var machine = Machine();
        machine = machine with { Engines = machine.Engines.Select(e => e.Kind == FitEngines.Ollama ? e with { KvCopies = 4 } : e).ToArray() };

        var options = FitEstimator.Estimate(Model(8 * Gi), machine, 8192);

        Assert.Equal(4 * 768, Of(options, FitEngines.Ollama).KvMiB);
        Assert.Equal(768, Of(options, FitEngines.LlamaCpp).KvMiB);
        Assert.Contains(Of(options, FitEngines.Ollama).Why, w => w.Contains("counted 4 times"));
    }

    [Fact]
    public void AStrataSizeIsJudgedByStrataOwnRamFigure()
    {
        var model = new ModelFacts(ModelFacts.SourceStrata, "s", "strata", null, null, null, null, null, null, null, null, null,
            [new ModelVariant("IQ1_M", 58_000_000_000, RamHintMiB: 30517), new ModelVariant("Q8_0", 188_000_000_000, RamHintMiB: 183105)], []);

        var options = FitEstimator.Estimate(model, Machine(ramMiB: 65536), 32768);

        Assert.All(options, o => Assert.Equal(FitEngines.Strata, o.Engine));
        Assert.True(options.Single(o => o.Variant == "IQ1_M").Fits);
        Assert.False(options.Single(o => o.Variant == "Q8_0").Fits);
        Assert.Equal(3, FitEstimator.Refused(model).Count);
    }

    [Fact]
    public void ColibriTakesAMixtureOfExpertsCheckpointAndNothingElse()
    {
        var moe = Model(600 * Gi, share: 0.95, experts: 288, source: ModelFacts.SourceCheckpoint, parameters: 321_000_000_000);
        var dense = Model(14 * Gi, source: ModelFacts.SourceCheckpoint);

        var colibri = Of(FitEstimator.Estimate(moe, Machine(ramMiB: 262144), 8192), FitEngines.Colibri);

        Assert.Equal(FitPlacements.Ram, colibri.Placement);
        Assert.True(colibri.ContextApplied);
        Assert.Contains(colibri.Why, w => w.Contains("Colibri:Serve:Vulkan=true"));
        Assert.Empty(FitEstimator.Estimate(dense, Machine(), 8192));
        Assert.Contains(FitEstimator.Refused(dense), r => r.Engine == FitEngines.Colibri && r.Reason.Contains("mixture-of-experts"));
        Assert.Equal(FitPlacements.StreamFromDisk, Of(FitEstimator.Estimate(moe, Machine(ramMiB: 98304), 8192), FitEngines.Colibri).Placement);
    }
}

public sealed class ModelLinkTests
{
    [Theory]
    [InlineData("qwen2.5:7b", "library", "qwen2.5", "7b", "qwen2.5:7b")]
    [InlineData("llama3.2", "library", "llama3.2", "latest", "llama3.2:latest")]
    [InlineData("https://ollama.com/library/gemma3:12b", "library", "gemma3", "12b", "gemma3:12b")]
    [InlineData("ollama.com/someone/model:q4", "someone", "model", "q4", "someone/model:q4")]
    public void AnOllamaNameOrLinkIsOllama(string text, string owner, string model, string tag, string pull)
    {
        Assert.True(ModelLink.TryParse(text, out var link, out _));
        Assert.Equal(new OllamaName(owner, model, tag), link!.Ollama);
        Assert.Equal(pull, link.Ollama!.PullName);
    }

    [Theory]
    [InlineData("unsloth/Qwen3-30B-A3B-GGUF")]
    [InlineData("https://huggingface.co/unsloth/Qwen3-30B-A3B-GGUF")]
    [InlineData("hf.co/unsloth/Qwen3-30B-A3B-GGUF:Q4_K_M")]
    public void ARepoIsHuggingFaceEvenWithoutItsHost(string text)
    {
        Assert.True(ModelLink.TryParse(text, out var link, out _));
        Assert.Equal("unsloth/Qwen3-30B-A3B-GGUF", link!.HuggingFace!.RepoId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com/x/y")]
    [InlineData("ollama.com/a/b/c")]
    public void AnythingElseIsRefusedWithASentence(string text)
    {
        Assert.False(ModelLink.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
