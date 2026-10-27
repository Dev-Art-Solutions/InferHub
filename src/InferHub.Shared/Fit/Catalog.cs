using System.Globalization;

namespace InferHub.Shared.Fit;

/// <summary>
/// One model in the catalogue (phase 119): where it is listed, the link 118's Check fit takes, and a rough class from
/// its weights. <see cref="Sizes"/> is Ollama's sizes of one model (<c>7b</c>, <c>14b</c>); a Hugging Face repo has one.
/// </summary>
/// <param name="Source"><see cref="CatalogSources"/>' names.</param>
/// <param name="Link">The size that fits best — what Check fit is opened with.</param>
/// <param name="Rank">Its place in the source's own order (0 first), before the fit reorders the list.</param>
public sealed record CatalogEntry(
    string Source,
    string Name,
    string Link,
    string? Description,
    IReadOnlyList<string> Capabilities,
    string? Architecture,
    bool Moe,
    int? TrainedContext,
    long? Downloads,
    long? Likes,
    DateTimeOffset? Updated,
    string? UpdatedText,
    bool Strata,
    IReadOnlyList<CatalogSize> Sizes,
    CatalogFit Fit,
    int Rank);

public sealed record CatalogSize(string Name, string Link, long? Parameters, CatalogFit Fit);

/// <summary>Where the weights alone would sit (119 D1) — never the estimate, which needs the context.</summary>
/// <param name="Class"><see cref="CatalogClasses"/>' names.</param>
public sealed record CatalogFit(string Class, long? WeightsMiB, string Why)
{
    /// <summary>Best first: on the card, then a split, then the CPU, then the unknown, then too big.</summary>
    public int Order => Class switch
    {
        CatalogClasses.Card => 0,
        CatalogClasses.CardAndRam or CatalogClasses.Ram => 1,
        CatalogClasses.Unknown => 2,
        _ => 3
    };

    public bool Fits => Class is CatalogClasses.Card or CatalogClasses.CardAndRam or CatalogClasses.Ram;
}

public static class CatalogClasses
{
    public const string Card = "card";
    public const string CardAndRam = "card-and-ram";
    public const string Ram = "ram";
    public const string Unknown = "unknown";
    public const string TooBig = "too-big";
}

public static class CatalogSources
{
    public const string HuggingFace = "huggingface";
    public const string Ollama = "ollama";
}

public static class CatalogSorts
{
    public const string Trending = "trending";
    public const string Downloads = "downloads";
    public const string Newest = "newest";

    public static readonly IReadOnlyList<string> All = [Trending, Downloads, Newest];
}

public static class CatalogKinds
{
    public const string Text = "text";
    public const string Vision = "vision";

    public static readonly IReadOnlyList<string> All = [Text, Vision];
}

/// <summary>
/// The catalogue's rough class (119 D1): parameters at a 4-bit quant against the usable VRAM and RAM the estimate
/// itself uses — the same machine, the same reserves, one opinion. It knows nothing of the context; it says so.
/// </summary>
public static class CatalogRough
{
    /// <summary>Q4_K_M, the quant <c>ollama pull</c> and most GGUF repos give by default, in bits a weight.</summary>
    public const double BitsPerWeight = 4.85;

    private const long Working = 1024;

    public static CatalogFit Classify(long? parameters, bool moe, MachineFacts machine)
    {
        if (parameters is not > 0)
        {
            return new CatalogFit(CatalogClasses.Unknown, null, "the source does not say how many parameters it has — Check fit reads the model itself");
        }

        var weights = (long)Math.Ceiling(parameters.Value * BitsPerWeight / 8 / (1024 * 1024));
        return Place(weights, moe, machine, $"{Billions(parameters.Value)} parameters ≈ {FitEstimator.Gib(weights)} at 4-bit");
    }

    /// <summary>The class of one file (or one quant's files) of known size — Hugging Face's quants, read from the repo's tree.</summary>
    public static CatalogFit ClassifyBytes(long bytes, bool moe, MachineFacts machine)
    {
        var weights = (long)Math.Ceiling(bytes / (1024.0 * 1024));
        return Place(weights, moe, machine, $"{FitEstimator.Gib(weights)} of weights");
    }

    private static CatalogFit Place(long weights, bool moe, MachineFacts machine, string size)
    {
        // The GGUF engines are what a link from the list installs to; the owner's limit for the tighter of them counts.
        var engine = machine.Engines.Where(e => e.Kind is FitEngines.LlamaCpp or FitEngines.Ollama && e.Installs).MinBy(e => e.VramLimitMiB ?? long.MaxValue)
                     ?? machine.Engines.FirstOrDefault(e => e.Kind is FitEngines.LlamaCpp or FitEngines.Ollama)
                     ?? new EngineFacts(FitEngines.LlamaCpp, null, false, null, null, null, null, null);
        var ignored = new List<string>();
        var vram = machine.Gpus.Count > 0 ? FitEstimator.Vram(machine, engine, ignored) : 0;
        var ram = FitEstimator.Ram(machine, engine, ignored);
        const string tail = "weights only — the context is chosen in Check fit";

        if (vram > 0 && weights + Working <= vram)
        {
            return new CatalogFit(CatalogClasses.Card, weights, $"{size}, on the card ({FitEstimator.Gib(vram)} usable); {tail}");
        }

        if (vram > 0 && weights + Working <= vram + ram)
        {
            var how = moe ? "a mixture of experts — llama.cpp keeps the experts in RAM" : "split between the card and RAM — slower";
            return new CatalogFit(CatalogClasses.CardAndRam, weights, $"{size}, more than the card's {FitEstimator.Gib(vram)}: {how}; {tail}");
        }

        if (vram == 0 && weights + Working <= ram)
        {
            return new CatalogFit(CatalogClasses.Ram, weights, $"{size}, in RAM on the CPU ({FitEstimator.Gib(ram)} usable, no card the node can use); {tail}");
        }

        return new CatalogFit(CatalogClasses.TooBig, weights,
            $"{size}, more than this machine's {FitEstimator.Gib(vram + ram)} of {(vram > 0 ? "VRAM and RAM" : "RAM")} — a smaller quant or Strata/colibri may still run it; Check fit says");
    }

    /// <summary>Entries fitting first, each class in the sources' own order, interleaved by rank.</summary>
    public static IReadOnlyList<CatalogEntry> Order(IEnumerable<CatalogEntry> entries) =>
        entries.OrderBy(e => e.Fit.Order).ThenBy(e => e.Rank).ThenBy(e => e.Source, StringComparer.Ordinal).ToArray();

    public static string Billions(long parameters) => parameters >= 1_000_000_000
        ? (parameters / 1e9).ToString(parameters >= 10_000_000_000 ? "0" : "0.#", CultureInfo.InvariantCulture) + "B"
        : (parameters / 1e6).ToString("0", CultureInfo.InvariantCulture) + "M";
}
