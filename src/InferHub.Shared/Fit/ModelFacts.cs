using System.Text.Json;

namespace InferHub.Shared.Fit;

/// <summary>
/// What a model is, as far as fitting it goes (phase 118, D2) — read from its GGUF header or its <c>config.json</c>,
/// never from a table of famous models.
/// </summary>
/// <param name="Source">Where the files would come from and what shape they are: <see cref="SourceGguf"/>,
/// <see cref="SourceCheckpoint"/>, <see cref="SourceStrata"/> or <see cref="SourceOllama"/>.</param>
/// <param name="AttentionLayers">Layers that keep a KV cache — all of them, except in a hybrid model.</param>
/// <param name="KvWidth">Values per token per attention layer in the cache: KV heads × (key + value length), or for
/// MLA the compressed rank + the rope part.</param>
/// <param name="ExpertShare">The part of the weights that is routed experts (0 for a dense model) — what can sit in RAM
/// while the rest is on the card.</param>
public sealed record ModelFacts(
    string Source,
    string Name,
    string? Architecture,
    string? Task,
    long? Parameters,
    int? Layers,
    int? AttentionLayers,
    int? KvWidth,
    int? TrainedContext,
    int? Experts,
    int? ExpertsUsed,
    double? ExpertShare,
    IReadOnlyList<ModelVariant> Variants,
    IReadOnlyList<string> Notes)
{
    public const string SourceGguf = "gguf";
    public const string SourceCheckpoint = "checkpoint";
    public const string SourceStrata = "strata";
    public const string SourceOllama = "ollama";

    /// <summary>Bytes of KV cache per token of context, at f16 — null when the model did not say enough to know.</summary>
    public long? KvBytesPerToken => AttentionLayers is { } layers && KvWidth is { } width ? (long)layers * width * 2 : null;

    public bool IsMoe => Experts is > 1;

    /// <summary>The facts a GGUF header gives (architecture-prefixed keys, as llama.cpp writes them).</summary>
    public static ModelFacts FromGguf(string source, string name, GgufHeader header, long firstFileBytes, IReadOnlyList<ModelVariant> variants, long? parameters = null, string? task = null)
    {
        var arch = header.Architecture ?? "llama";
        long? Int(string key) => header.Integer($"{arch}.{key}");

        var layers = (int?)Int("block_count");
        var heads = Int("attention.head_count") ?? header.Integers($"{arch}.attention.head_count")?.Max();
        var kvList = header.Integers($"{arch}.attention.head_count_kv");
        var kvHeads = Int("attention.head_count_kv") ?? kvList?.Max() ?? heads;
        var embedding = Int("embedding_length");
        var keyLength = Int("attention.key_length") ?? (embedding is { } e && heads is > 0 ? e / heads : null);
        var valueLength = Int("attention.value_length") ?? keyLength;
        var notes = new List<string>();

        int? attentionLayers = layers;

        if (kvList is { Length: > 0 } && kvList.Any(k => k == 0))
        {
            attentionLayers = kvList.Count(k => k > 0);
            notes.Add($"hybrid: {attentionLayers} of {layers} layers keep a KV cache");
        }
        else if (Int("full_attention_interval") is > 1 and var interval && layers is { } all)
        {
            attentionLayers = (int)Math.Ceiling(all / (double)interval);
            notes.Add($"hybrid: every {interval}th layer keeps a KV cache ({attentionLayers} of {all})");
        }
        else if (AttentionBlocks(header, layers) is { Seen: > 0 } blocks && blocks.Attention < blocks.Seen && layers is { } count)
        {
            // Qwen3-Next says nothing in its keys (llama.cpp knows it by architecture); its tensors say which blocks have attention.
            attentionLayers = (int)Math.Round(count * blocks.Attention / (double)blocks.Seen);
            notes.Add($"hybrid: {attentionLayers} of {count} layers keep a KV cache (counted from the tensors)");
        }

        int? width = kvHeads is { } kv && keyLength is { } k && valueLength is { } v ? (int)(kv * (k + v)) : null;

        if (Int("attention.kv_lora_rank") is > 0 and var rank)
        {
            // MLA (DeepSeek, Kimi): the cache holds the compressed latent and the rope part, not the full heads.
            var rope = Int("rope.dimension_count") ?? 64;
            width = (int)(rank + rope);
            notes.Add($"MLA: {rank} + {rope} values per token per layer");
        }

        var experts = (int?)Int("expert_count");
        double? share = null;
        var sizes = header.TensorBytes(firstFileBytes);

        if (sizes.Count > 0)
        {
            var total = sizes.Sum(s => s.Bytes);
            var routed = sizes.Where(s => s.Name.Contains("_exps", StringComparison.Ordinal)).Sum(s => s.Bytes);
            share = total > 0 ? routed / (double)total : null;
        }
        else if (experts is > 1)
        {
            notes.Add("the tensor list was not read; the experts' share of the weights is not known");
        }

        return new ModelFacts(
            source,
            name,
            arch,
            task,
            parameters ?? header.Integer("general.parameter_count"),
            layers,
            attentionLayers,
            width,
            (int?)Int("context_length"),
            experts,
            (int?)Int("expert_used_count"),
            experts is > 1 ? share : 0,
            variants,
            notes);
    }

    /// <summary>Blocks seen in the tensor list (a split model's first shard holds only some), and how many have attention keys.</summary>
    private static (int Seen, int Attention) AttentionBlocks(GgufHeader header, int? layers)
    {
        var seen = new HashSet<int>();
        var attention = new HashSet<int>();
        var recurrent = new HashSet<int>();
        var fused = new HashSet<int>();

        foreach (var tensor in header.Tensors)
        {
            if (!tensor.Name.StartsWith("blk.", StringComparison.Ordinal))
            {
                continue;
            }

            var dot = tensor.Name.IndexOf('.', 4);

            if (dot < 0 || !int.TryParse(tensor.Name.AsSpan(4, dot - 4), out var block))
            {
                continue;
            }

            seen.Add(block);
            var rest = tensor.Name[(dot + 1)..];

            if (rest.StartsWith("attn_k.", StringComparison.Ordinal) || rest.StartsWith("attn_k_b.", StringComparison.Ordinal)
                || rest.StartsWith("attn_kv_a_mqa.", StringComparison.Ordinal))
            {
                attention.Add(block);
            }
            else if (rest.StartsWith("attn_qkv.", StringComparison.Ordinal))
            {
                fused.Add(block);
            }
            else if (rest.StartsWith("ssm_", StringComparison.Ordinal))
            {
                recurrent.Add(block);
            }
        }

        // A fused attn_qkv is real attention (Phi) unless the block is recurrent: Qwen3-Next's linear layers name their projection so too.
        fused.ExceptWith(recurrent);
        attention.UnionWith(fused);

        // In a split file the last block seen may continue in the next shard, its attention among the missing tensors.
        if (seen.Count > 0 && seen.Count < (layers ?? int.MaxValue))
        {
            var last = seen.Max();
            seen.Remove(last);
            attention.Remove(last);
        }

        return (seen.Count, attention.Count);
    }

    /// <summary>The facts a Hugging Face <c>config.json</c> gives (the transformers names, with the common variants).</summary>
    public static ModelFacts FromConfig(string name, JsonElement config, long? parameters, IReadOnlyList<ModelVariant> variants, string? task)
    {
        // Multimodal configs keep the language model's numbers under text_config.
        if (config.TryGetProperty("text_config", out var text) && text.ValueKind == JsonValueKind.Object && !config.TryGetProperty("num_hidden_layers", out _))
        {
            config = text;
        }

        long? Int(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (config.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n))
                {
                    return n;
                }
            }

            return null;
        }

        var layers = (int?)Int("num_hidden_layers", "n_layer", "num_layers");
        var heads = Int("num_attention_heads", "n_head");
        var kvHeads = Int("num_key_value_heads", "num_kv_heads") ?? heads;
        var hidden = Int("hidden_size", "d_model", "n_embd");
        var headDim = Int("head_dim") ?? (hidden is { } h && heads is > 0 ? h / heads : null);
        var notes = new List<string>();
        int? width = kvHeads is { } kv && headDim is { } d ? (int)(kv * d * 2) : null;
        int? attentionLayers = layers;

        if (config.TryGetProperty("layer_types", out var types) && types.ValueKind == JsonValueKind.Array)
        {
            var full = types.EnumerateArray().Count(t => t.GetString() is "full_attention" or "attention");

            if (full > 0 && full < types.GetArrayLength())
            {
                attentionLayers = full;
                notes.Add($"hybrid: {full} of {types.GetArrayLength()} layers keep a KV cache");
            }
        }
        else if (Int("full_attention_interval") is > 1 and var interval && layers is { } all)
        {
            attentionLayers = (int)Math.Ceiling(all / (double)interval);
            notes.Add($"hybrid: every {interval}th layer keeps a KV cache");
        }

        if (Int("kv_lora_rank") is > 0 and var rank)
        {
            var rope = Int("qk_rope_head_dim") ?? 64;
            width = (int)(rank + rope);
            notes.Add($"MLA: {rank} + {rope} values per token per layer");
        }

        var experts = (int?)Int("n_routed_experts", "num_experts", "num_local_experts", "moe_num_experts");
        var used = (int?)Int("num_experts_per_tok", "moe_top_k", "num_experts_per_token");
        double? share = 0;

        if (experts is > 1)
        {
            var expertFfn = Int("moe_intermediate_size", "expert_intermediate_size") ?? Int("intermediate_size");
            var dense = Int("first_k_dense_replace") ?? 0;
            share = expertFfn is { } f && hidden is { } hs && layers is { } l && parameters is > 0
                ? Math.Min(0.98, (double)experts.Value * 3 * hs * f * Math.Max(0, l - dense) / parameters.Value)
                : null;
        }

        return new ModelFacts(
            SourceCheckpoint,
            name,
            config.TryGetProperty("model_type", out var type) ? type.GetString() : null,
            task,
            parameters,
            layers,
            attentionLayers,
            width,
            (int?)Int("max_position_embeddings", "n_positions", "seq_length"),
            experts,
            used,
            share,
            variants,
            notes);
    }
}

/// <summary>One downloadable form of a model: a GGUF quant, a checkpoint's own precision, a Strata size, an Ollama tag.</summary>
/// <param name="Bytes">What is downloaded and, for a GGUF, what the weights take in memory.</param>
/// <param name="Files">How many files it is — a split GGUF is several, which Ollama does not pull.</param>
/// <param name="RamHintMiB">For a Strata size: the RAM Strata's own table names for it.</param>
/// <param name="Link">The link that installs exactly this variant (the canonical form the store and Ollama read).</param>
public sealed record ModelVariant(string Name, long Bytes, int Files = 1, long? RamHintMiB = null, string? About = null, string? Link = null);
