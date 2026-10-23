using InferHub.Shared.HuggingFace;

namespace InferHub.Shared.Strata;

/// <summary>
/// The models Strata's installer can set up (phase 99): Qwen3.8-Flash-Next in its families of Hugging
/// Face repos and their sizes, and since Strata 0.2 its smaller cousin Qwen3.6-35B-A3B — read from
/// Strata's <c>setup.py</c> (<c>FAMILIES</c>, <c>OWN_PATH_FAMILIES</c>, <c>MODELS</c>) at the release the
/// node was last built against (0.2.2, phase 111). Pure, so the hub and the node map a link to a family
/// and a size the same way.
/// </summary>
/// <remarks>
/// <b>This is a map, not the authority.</b> The node passes <c>--family</c> and <c>--model</c> to
/// Strata's own <c>setup.py</c>, whose argument parser and file table decide; a size a newer Strata
/// adds is refused here with the list in the sentence rather than guessed at, and a size Strata drops
/// is refused by Strata in its own words. A model installed by hand on the box is in the catalogue
/// whether or not it is in this table.
/// </remarks>
public static class StrataModels
{
    /// <summary>One Strata family: its <c>--family</c> value, its repo, its sizes, and its config prefix.</summary>
    public sealed record Family(string Name, string Repo, string Title, string Tag, IReadOnlyList<Size> Sizes);

    public sealed record Size(string Name, string About);

    public static readonly IReadOnlyList<Family> Families =
    [
        new("qwen", "ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-GGUF", "Qwen3.8-Flash-Next", "",
        [
            new("Q2_0", "2-bit, the fastest (~66 GB download, 48 GB RAM)"),
            new("IQ2_XS", "2-bit i-quant, Strata's recommended size (~68 GB, 48 GB RAM)"),
            new("IQ3_XXS", "3-bit i-quant, better and slower (~76 GB, 60 GB RAM)"),
            new("IQ3_S", "3.5-bit i-quant, the best quality (~84 GB, 64 GB RAM)")
        ]),
        new("swift", "ukisai/Swift-1.5-Qwen3.8-Flash-Next-GSQ-RCO-GGUF", "Swift 1.5", "swift-",
        [
            new("IQ2_XS", "UkisAI's fine-tune that thinks shorter (~68 GB, 48 GB RAM)"),
            new("IQ3_XXS", "UkisAI's fine-tune, 3-bit (~76 GB, 60 GB RAM)"),
            new("IQ3_S", "UkisAI's fine-tune, 3.5-bit (~84 GB, 62 GB RAM)")
        ]),
        new("coder", "ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-Coder-GGUF", "Qwen3.8-Flash-Next Coder", "coder-",
        [
            new("IQ1_M", "half the experts, for code; fits 32 GB of RAM (~58 GB download)")
        ]),
        new("unsloth", "unsloth/Qwen3.8-Flash-Next-GGUF", "Qwen3.8-Flash-Next (Unsloth)", "unsloth-",
        [
            new("UD-IQ4_XS", "~4-bit, between IQ3_S and UD-Q4_K_XL (~94 GB, 48 GB RAM; part read from the SSD below ~80 GB)"),
            new("UD-Q4_K_XL", "4-bit, K-quant experts (~111 GB, 48 GB RAM; most experts from the SSD on a 64 GB PC)"),
            new("UD-Q5_K_XL", "5-bit, close to Q8_0 (~158 GB; every expert in RAM from ~108 GB)"),
            new("UD-Q6_K_XL", "6-bit (~169 GB; every expert in RAM from ~118 GB)"),
            new("Q8_0", "8-bit, the reference quality (~188 GB; for a 192 GB PC)")
        ]),
        new("gyro", "agentionai/Qwen3.8-Flash-Next-Gyro-GGUF", "Qwen3.8-Flash-Next (Gyro)", "gyro-",
        [
            new("Gyro-S", "the smallest, for little memory (~58 GB download, 27.6 GB of VRAM + RAM together)"),
            new("Gyro-M", "about IQ3_XXS quality with ~20% less GPU memory (~92 GB, 35 GB together)")
        ]),
        // One repository per NVFP4 size (setup.py's NVFP4_REPOS): three families of one size, one --family.
        new("nvfp4", "Maximilian228/Qwen3.8-Flash-Next-NVFP4-GPTQ-Strata", "Qwen3.8-Flash-Next (NVFP4)", "nvfp4-",
        [
            new("NVFP4", "4-bit NVFP4 experts by GPTQ, NVIDIA cards (~128 GB, ~80 GB RAM)")
        ]),
        new("nvfp4", "Maximilian228/Qwen3.8-Flash-Next-NVIDIA-NVFP4-Strata", "Qwen3.8-Flash-Next (NVFP4, NVIDIA's)", "nvfp4-",
        [
            new("NVFP4-NVIDIA", "NVIDIA's own NVFP4 checkpoint, converted (~128 GB, ~80 GB RAM)")
        ]),
        new("nvfp4", "nvidia/Qwen3.8-Flash-Next-FP4-Local", "Qwen3.8-Flash-Next (NVFP4, QAD)", "nvfp4-",
        [
            new("NVFP4-QAD", "NVIDIA's FP4 trained for FP4, prepared by Strata's setup (~102 GB, ~80 GB RAM)")
        ]),
        // Strata 0.2's own-path family: a 35B MoE with 3B active that Strata's engine reads as it is.
        new("qwen36", "unsloth/Qwen3.6-35B-A3B-MTP-GGUF", "Qwen3.6-35B-A3B", "qwen36-",
        [
            new("UD-Q4_K_XL", "4-bit (Unsloth Dynamic), the smallest (~23 GB, 32 GB RAM)"),
            new("UD-Q6_K_XL", "6-bit, closer to the full model (~33 GB, 44 GB RAM)"),
            new("Q8_0", "8-bit, the closest to the full model (~38 GB, 52 GB RAM)")
        ])
    ];

    /// <summary>
    /// The catalogue name an install gets: Strata writes <c>strata-&lt;family tag&gt;&lt;size&gt;.json</c>
    /// (lowercase), and the node lists a config by its file name without <c>.json</c>.
    /// </summary>
    public static string CatalogName(Family family, Size size) => $"strata-{family.Tag}{size.Name.ToLowerInvariant()}";

    /// <summary>The link the hub sends for this size, in <see cref="HfReference"/>'s canonical form.</summary>
    public static string Link(Family family, Size size) => $"{family.Repo}:{size.Name}";

    /// <summary>Every family and size, in the installer's order.</summary>
    public static IEnumerable<(Family Family, Size Size)> All() =>
        Families.SelectMany(family => family.Sizes.Select(size => (family, size)));

    /// <summary>Whether the link names one of Strata's repos at all — the question the node asks before handing it to the GGUF store.</summary>
    public static bool IsStrataRepo(HfReference reference) => FamilyOf(reference) is not null;

    /// <summary>
    /// The family and size a link names. The size comes from <see cref="HfReference.Quant"/>, or from a
    /// file's name (<c>…-IQ1_M-00001-of-00002.gguf</c>, or the size folder ISTA-DASLab's repos keep them
    /// in); a repo of one size needs neither.
    /// </summary>
    public static bool TryMatch(HfReference reference, out Family? family, out Size? size, out string? error)
    {
        family = FamilyOf(reference);
        size = null;
        error = null;

        if (family is null)
        {
            error = $"'{reference.RepoId}' is not one of Strata's repos ({string.Join(", ", Families.Select(f => f.Repo))})";
            return false;
        }

        if (reference.Revision != HfReference.DefaultRevision)
        {
            error = $"Strata installs each repo at the commit its setup.py pins; a revision ('{reference.Revision}') cannot be chosen";
            family = null;
            return false;
        }

        var wanted = reference.Quant ?? SizeInFile(family, reference.File);

        if (wanted is null)
        {
            if (family.Sizes.Count == 1)
            {
                size = family.Sizes[0];
                return true;
            }

            error = $"{family.Title} comes in several sizes; name one ({string.Join(", ", family.Sizes.Select(s => s.Name))})";
            family = null;
            return false;
        }

        size = family.Sizes.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase));

        if (size is null)
        {
            error = $"Strata does not install {family.Title} in size '{wanted}'; it has {string.Join(", ", family.Sizes.Select(s => s.Name))}";
            family = null;
            return false;
        }

        return true;
    }

    private static Family? FamilyOf(HfReference reference) =>
        Families.FirstOrDefault(f => string.Equals(f.Repo, reference.RepoId, StringComparison.OrdinalIgnoreCase));

    private static string? SizeInFile(Family family, string? file)
    {
        if (string.IsNullOrEmpty(file))
        {
            return null;
        }

        // Longest first: "IQ3_XXS" must not be read as "IQ3_S" or "IQ2_XS" as anything shorter.
        var tokens = file.Split('/', '-', '.');

        return family.Sizes
            .Select(s => s.Name)
            .OrderByDescending(name => name.Length)
            .FirstOrDefault(name => tokens.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase))
                                    || file.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase)
                                    || file.Contains("-" + name + "-", StringComparison.OrdinalIgnoreCase)
                                    || file.Contains("-" + name + ".", StringComparison.OrdinalIgnoreCase));
    }
}
