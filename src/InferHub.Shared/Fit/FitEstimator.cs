using System.Globalization;

namespace InferHub.Shared.Fit;

/// <summary>One way to run a model here: an engine, a variant, where it would sit, and the arithmetic (phase 118, D1).</summary>
/// <param name="Available">The node can install it for this engine; false still shows what the engine would do.</param>
/// <param name="Placement"><see cref="FitPlacements"/>' names, or <c>none</c>.</param>
/// <param name="MaxContext">The largest context this option holds at all; null when the engine sizes it itself.</param>
/// <param name="CardContext">The largest context with the model wholly on the card (or, for llama.cpp's experts in
/// RAM, the card's part); null when it never is.</param>
/// <param name="Speed"><c>fast</c>, <c>medium</c>, <c>slow</c>, <c>very slow</c> — a class with its reason in
/// <see cref="Why"/>, never a figure.</param>
public sealed record FitOption(
    string Engine,
    string? EngineName,
    string Variant,
    string? Link,
    bool Available,
    bool Fits,
    string Placement,
    long WeightsMiB,
    long KvMiB,
    long VramMiB,
    long RamMiB,
    long DiskMiB,
    int Context,
    int? MaxContext,
    int? CardContext,
    string Speed,
    bool ContextApplied,
    IReadOnlyList<string> Why,
    int Score);

public static class FitPlacements
{
    public const string Gpu = "gpu";
    public const string ExpertsInRam = "experts-in-ram";
    public const string Strata = "strata";
    public const string Split = "split";
    public const string Ram = "ram";
    public const string Cpu = "cpu";
    public const string StreamFromDisk = "stream-from-disk";
    public const string None = "none";
}

/// <summary>
/// The heuristic (phase 118): pure arithmetic over <see cref="ModelFacts"/> and <see cref="MachineFacts"/> for one
/// context, so the node, the hub and the app show the same numbers. Conservative by design — it would rather say
/// "split" for a model that turns out to fit than "on the card" for one that does not load.
/// </summary>
public static class FitEstimator
{
    /// <summary>The context an estimate is for when the owner has not chosen yet — a starting point, not a minimum.</summary>
    public const int DefaultContext = 8192;

    public const int MaxAskedContext = 4 * 1024 * 1024;

    private const double Mi = 1024 * 1024;

    public static IReadOnlyList<FitOption> Estimate(ModelFacts model, MachineFacts machine, int context)
    {
        context = Math.Clamp(context, 0, MaxAskedContext);
        var options = new List<FitOption>();

        foreach (var kind in FitEngines.All)
        {
            var engine = machine.Engines.FirstOrDefault(e => e.Kind == kind)
                         ?? new EngineFacts(kind, null, false, $"this node has no {kind} engine", null, null, null, null);

            if (CanLoad(model, kind) is not null)
            {
                continue;
            }

            foreach (var variant in model.Variants)
            {
                options.Add(Option(model, machine, engine, variant, context));
            }
        }

        return options
            .OrderByDescending(o => o.Score)
            .ThenBy(o => o.WeightsMiB)
            .ToArray();
    }

    /// <summary>The engines that are not offered for this model, each with the sentence why.</summary>
    public static IReadOnlyList<(string Engine, string Reason)> Refused(ModelFacts model) =>
        FitEngines.All.Select(kind => (kind, CanLoad(model, kind))).Where(r => r.Item2 is not null).Select(r => (r.kind, r.Item2!)).ToArray();

    /// <summary>Why an engine cannot load this model at all (D3), or null when it can.</summary>
    public static string? CanLoad(ModelFacts model, string kind) => (model.Source, kind) switch
    {
        (ModelFacts.SourceStrata, FitEngines.Strata) => null,
        (ModelFacts.SourceStrata, _) => "a Strata repo is installed by Strata's setup; its quants need Strata's engine",
        (_, FitEngines.Strata) => "Strata runs only its own families (Qwen3.8-Flash-Next and Qwen3.6-35B-A3B)",
        (ModelFacts.SourceOllama, FitEngines.Ollama) => null,
        (ModelFacts.SourceOllama, _) => "a model from Ollama's library is pulled by Ollama",
        (ModelFacts.SourceGguf, FitEngines.Ollama or FitEngines.LlamaCpp) => null,
        (ModelFacts.SourceGguf, FitEngines.Colibri) => "colibri reads its own format, converted from a safetensors checkpoint, not a GGUF",
        (ModelFacts.SourceCheckpoint, FitEngines.Colibri) => model.IsMoe ? null : "colibri runs mixture-of-experts models only",
        (ModelFacts.SourceCheckpoint, _) => "Ollama and llama.cpp need a GGUF; link a GGUF repository of the same model",
        _ => "not this engine's format"
    };

    private static FitOption Option(ModelFacts model, MachineFacts machine, EngineFacts engine, ModelVariant variant, int context)
    {
        var why = new List<string>();
        var vram = Vram(machine, engine, why);
        var ram = Ram(machine, engine, why);
        var weights = Mib(variant.Bytes);
        var disk = Disk(model, engine.Kind, variant);
        // An engine that gives each parallel slot its own context (Ollama) keeps that many caches.
        long? kvPerToken = model.KvBytesPerToken * Math.Max(1, engine.KvCopies);

        if (engine.KvCopies > 1 && kvPerToken is not null)
        {
            why.Add($"{engine.Kind} keeps {engine.KvCopies} contexts per model (one per parallel request), so the KV cache is counted {engine.KvCopies} times");
        }

        Placed placed;
        int? max = null;
        int? card = null;
        var applied = true;

        switch (engine.Kind)
        {
            case FitEngines.Strata:
                placed = Strata(variant, vram, ram, why);
                applied = true;
                why.Add("Strata sizes its own KV cache; the context is passed to its setup (--context)");
                break;

            case FitEngines.Colibri:
                placed = Colibri(model, variant, ram, context, why);
                applied = false;
                why.Add("colibri sizes its own cache from free RAM; the context is not something the node can set for it");
                break;

            default:
                placed = Place(engine.Kind, model, weights, Kv(kvPerToken, context), Compute(context), vram, ram);
                max = Largest(c => Place(engine.Kind, model, weights, Kv(kvPerToken, c), Compute(c), vram, ram).Fits, model);
                card = Largest(c => Place(engine.Kind, model, weights, Kv(kvPerToken, c), Compute(c), vram, ram).Placement is FitPlacements.Gpu or FitPlacements.ExpertsInRam, model);
                Explain(engine.Kind, model, placed, weights, kvPerToken, context, vram, ram, why);
                break;
        }

        if (variant.Files > 1 && engine.Kind == FitEngines.Ollama && model.Source == ModelFacts.SourceGguf)
        {
            placed = placed with { Fits = false, Placement = FitPlacements.None };
            why.Insert(0, $"Ollama does not pull a GGUF split into {variant.Files} files; llama.cpp does");
        }

        if (engine.DiskFreeMiB is { } free && disk > free)
        {
            placed = placed with { Fits = false };
            why.Insert(0, $"needs {Gib(disk)} of disk where {engine.Kind} keeps models{(engine.Directory is { } d ? $" ({d})" : "")}, {Gib(free)} is free");
        }

        if (model.TrainedContext is { } trained && context > trained)
        {
            why.Add($"the model was trained for {trained.ToString("N0", CultureInfo.InvariantCulture)} tokens; past that its answers degrade");
        }

        if (!engine.Installs && engine.Absent is { } absent)
        {
            why.Insert(0, absent);
        }

        var score = Score(model, variant, placed, engine.Installs);

        return new FitOption(
            engine.Kind,
            engine.Name,
            variant.Name,
            variant.Link,
            engine.Installs,
            placed.Fits,
            placed.Fits ? placed.Placement : FitPlacements.None,
            weights,
            placed.KvMiB,
            placed.VramMiB,
            placed.RamMiB,
            disk,
            context,
            max,
            card,
            placed.Fits ? placed.Speed : "—",
            applied,
            why,
            score);
    }

    private sealed record Placed(bool Fits, string Placement, long VramMiB, long RamMiB, long KvMiB, string Speed);

    /// <summary>Ollama and llama.cpp over a GGUF: the card first, then (llama.cpp, MoE) the experts in RAM, then a split, then the CPU.</summary>
    private static Placed Place(string kind, ModelFacts model, long weights, long kv, long compute, long vram, long ram)
    {
        var whole = weights + kv + compute;

        if (vram > 0 && whole <= vram)
        {
            return new Placed(true, FitPlacements.Gpu, whole, 0, kv, "fast");
        }

        var share = model.ExpertShare ?? 0;

        if (kind == FitEngines.LlamaCpp && vram > 0 && share >= 0.3)
        {
            var dense = (long)(weights * (1 - share));
            var experts = weights - dense;

            if (dense + kv + compute <= vram && experts <= ram)
            {
                return new Placed(true, FitPlacements.ExpertsInRam, dense + kv + compute, experts, kv, "medium");
            }
        }

        if (vram > 0 && whole <= vram + ram)
        {
            var onCard = vram / (double)whole;
            return new Placed(true, FitPlacements.Split, vram, whole - vram, kv, onCard >= 0.6 ? "slow" : "very slow");
        }

        if (vram == 0 && weights + kv <= ram)
        {
            var light = weights <= 8 * 1024 || (model.IsMoe && model.ExpertsUsed is { } used && model.Experts is { } all && used * 8 <= all);
            return new Placed(true, FitPlacements.Cpu, 0, weights + kv, kv, light ? "slow" : "very slow");
        }

        return new Placed(false, FitPlacements.None, Math.Min(whole, vram), Math.Max(0, whole - vram), kv, "—");
    }

    private static Placed Strata(ModelVariant variant, long vram, long ram, List<string> why)
    {
        if (vram == 0)
        {
            why.Add("Strata needs an NVIDIA card; none was seen");
            return new Placed(false, FitPlacements.None, 0, 0, 0, "—");
        }

        if (variant.RamHintMiB is not { } hint)
        {
            why.Add("Strata's table names no RAM for this size");
            return new Placed(true, FitPlacements.Strata, vram, Mib(variant.Bytes), 0, "medium");
        }

        if (hint <= ram)
        {
            why.Add($"Strata's own table: {Gib(hint)} of RAM for {variant.Name}; this node has {Gib(ram)} to give");
            return new Placed(true, FitPlacements.Strata, vram, hint, 0, "medium");
        }

        if (hint * 3 / 4 <= ram)
        {
            why.Add($"Strata's table wants {Gib(hint)} of RAM, this node has {Gib(ram)}: the rest is read from disk on every token");
            return new Placed(true, FitPlacements.StreamFromDisk, vram, ram, 0, "slow");
        }

        why.Add($"Strata's table wants {Gib(hint)} of RAM for {variant.Name}; this node has {Gib(ram)}");
        return new Placed(false, FitPlacements.None, vram, hint, 0, "—");
    }

    /// <summary>colibri: the checkpoint converted, the dense part and the cache in RAM, experts read from disk past what RAM holds.</summary>
    private static Placed Colibri(ModelFacts model, ModelVariant variant, long ram, int context, List<string> why)
    {
        var converted = Converted(model, variant);
        var share = model.ExpertShare ?? 0.9;
        var dense = (long)(converted * (1 - share));
        var kv = Kv(model.KvBytesPerToken, context);
        why.Add($"converted for colibri: about {Gib(converted)} (a byte per dense parameter, 0.6 per expert parameter — GLM-5.3-Flash 321B measured 195 GB; a small model it converts to int8 is nearer a byte each)");

        if (converted + kv <= ram)
        {
            why.Add("the whole model fits in RAM; colibri runs it on the CPU");
            return new Placed(true, FitPlacements.Ram, 0, converted + kv, kv, "slow");
        }

        if (dense + kv <= ram)
        {
            why.Add($"the dense part ({Gib(dense)}) stays in RAM; the experts that do not fit are read from disk — the first answer takes minutes (GLM-5.3-Flash here: 6 min cold, 25 s warm), and an HDD is far slower than an SSD");
            return new Placed(true, FitPlacements.StreamFromDisk, 0, ram, kv, "very slow");
        }

        why.Add($"even the dense part ({Gib(dense)}) does not fit in {Gib(ram)} of RAM");
        return new Placed(false, FitPlacements.None, 0, dense + kv, kv, "—");
    }

    private static void Explain(string kind, ModelFacts model, Placed placed, long weights, long? kvPerToken, int context, long vram, long ram, List<string> why)
    {
        why.Add(kvPerToken is { } perToken
            ? $"weights {Gib(weights)} + KV for {context.ToString("N0", CultureInfo.InvariantCulture)} tokens {Gib(Kv(perToken, context))} ({perToken / 1024.0:0.#} KiB a token, f16) + {Gib(Compute(context))} working space"
            : $"weights {Gib(weights)}; the KV cache per token is not known for this architecture, so the context is not counted");

        why.Add(placed.Placement switch
        {
            FitPlacements.Gpu => $"all on the card: {Gib(placed.VramMiB)} of {Gib(vram)}",
            FitPlacements.ExpertsInRam => $"the card holds the dense part and the KV ({Gib(placed.VramMiB)} of {Gib(vram)}); the experts ({Gib(placed.RamMiB)}) sit in RAM and run on the CPU — only the few chosen per token are read",
            FitPlacements.Split => $"{Gib(vram)} on the card, {Gib(placed.RamMiB)} in RAM: every token crosses between them",
            FitPlacements.Cpu => vram == 0 ? $"no card seen; all in RAM ({Gib(placed.RamMiB)} of {Gib(ram)}), on the CPU" : $"all in RAM ({Gib(placed.RamMiB)}), on the CPU",
            _ => $"needs {Gib(weights + Kv(kvPerToken, context) + Compute(context))}; the card ({Gib(vram)}) and RAM ({Gib(ram)}) together are not enough"
        });

        if (kind == FitEngines.Ollama && model.IsMoe && placed.Placement == FitPlacements.Split)
        {
            why.Add("Ollama splits by layers; llama.cpp can keep a mixture-of-experts' experts in RAM instead, which is usually faster");
        }
    }

    private static long Vram(MachineFacts machine, EngineFacts engine, List<string> why)
    {
        // A card keeps some for the driver and the desktop; 5%, at least 512 MiB.
        var usable = machine.Gpus.Sum(g => Math.Max(0, g.TotalMiB - Math.Max(512, g.TotalMiB / 20)));

        if (engine.VramLimitMiB is { } limit && limit < usable)
        {
            why.Add($"the owner limits {engine.Kind} to {Gib(limit)} of VRAM");
            usable = limit;
        }

        var free = machine.Gpus.Where(g => g.FreeMiB is not null).Sum(g => g.FreeMiB!.Value);

        if (machine.Gpus.Count > 0 && machine.Gpus.All(g => g.FreeMiB is not null) && free + 1024 < usable)
        {
            why.Add($"right now only {Gib(free)} of VRAM is free — another program holds the rest");
        }

        return usable;
    }

    private static long Ram(MachineFacts machine, EngineFacts engine, List<string> why)
    {
        // The system and the node itself keep a tenth, at least 4 GiB.
        var usable = Math.Max(0, machine.RamMiB - Math.Max(4096, machine.RamMiB / 10));

        if (engine.RamLimitMiB is { } limit && limit < usable)
        {
            why.Add($"the owner limits {engine.Kind} to {Gib(limit)} of RAM");
            usable = limit;
        }

        return usable;
    }

    private static long Disk(ModelFacts model, string kind, ModelVariant variant) =>
        kind == FitEngines.Colibri
            ? Mib(variant.Bytes) + Converted(model, variant) // the checkpoint is downloaded, then converted beside it
            : Mib(variant.Bytes);

    private static long Converted(ModelFacts model, ModelVariant variant) =>
        model.Parameters is > 0 ? Mib((long)(model.Parameters.Value * (1 - 0.4 * (model.ExpertShare ?? 0.9)))) : Mib(variant.Bytes) / 2;

    private static int Score(ModelFacts model, ModelVariant variant, Placed placed, bool available)
    {
        if (!placed.Fits)
        {
            return available ? -100 : -200;
        }

        var score = placed.Placement switch
        {
            FitPlacements.Gpu => 50,
            FitPlacements.ExpertsInRam => 40,
            FitPlacements.Strata => 35,
            FitPlacements.Split => 20,
            FitPlacements.Ram => 15,
            FitPlacements.Cpu => 10,
            _ => 5
        };

        // Within a placement a bigger quant answers better up to about six bits a weight (then it only costs memory), and below three it gets worse fast; on a tie the smaller, faster one is first.
        if (model.Parameters is > 0)
        {
            var bits = variant.Bytes * 8.0 / model.Parameters.Value;
            score += (int)Math.Min(6, Math.Round(bits));

            // Q4_0, Q4_1, Q5_0, Q5_1: the old quants, more bytes for less than the K and I quants of the same size.
            if (variant.Name.Length == 4 && variant.Name[0] is 'Q' or 'q' && variant.Name[1] is '4' or '5' && variant.Name[2] == '_' && variant.Name[3] is '0' or '1')
            {
                score -= 1;
            }

            // Split across the bus, the share on the card decides the speed more than the quant decides the answer.
            if (placed.Placement == FitPlacements.Split && placed.Speed == "slow")
            {
                score += 3;
            }

            // Below two bits a model is a curiosity: a four-bit one split across the bus answers better, if slower.
            score -= bits < 2 ? 35 : bits < 3 ? 15 : 0;
        }

        return score + (available ? 100 : 0);
    }

    /// <summary>The largest context (in steps of 256) for which <paramref name="fits"/> holds, capped at the model's own; null when none does.</summary>
    private static int? Largest(Func<int, bool> fits, ModelFacts model)
    {
        if (model.KvBytesPerToken is null)
        {
            return null;
        }

        var high = Math.Min(model.TrainedContext ?? 1 << 20, MaxAskedContext);

        if (!fits(256))
        {
            return null;
        }

        if (fits(high))
        {
            return high;
        }

        int low = 256;

        while (high - low > 256)
        {
            var mid = (low + high) / 2 / 256 * 256;

            if (mid <= low)
            {
                break;
            }

            if (fits(mid))
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static long Kv(long? perToken, int context) => perToken is { } p ? (long)Math.Ceiling(p * (double)context / Mi) : 0;

    /// <summary>The engine's working buffers: half a gibibyte, plus a little per token (attention scratch grows with the context).</summary>
    private static long Compute(int context) => 512 + context / 100;

    private static long Mib(long bytes) => (long)Math.Ceiling(bytes / Mi);

    public static string Gib(long mib) => mib >= 1024
        ? (mib / 1024.0).ToString(mib >= 10 * 1024 ? "0" : "0.#", CultureInfo.InvariantCulture) + " GiB"
        : mib.ToString(CultureInfo.InvariantCulture) + " MiB";
}
