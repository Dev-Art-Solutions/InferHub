using System.Text.Json;

namespace InferHub.Shared.Fit;

/// <summary>
/// Phase 121: a diffusers repository read from its <c>model_index.json</c> and its file list — which pipeline class it
/// is, what its components weigh, and whether anything in it would run code when loaded. Pure, so the node and its tests
/// read a repository the same way.
/// </summary>
public static class DiffusersRepo
{
    public const string ModelIndex = "model_index.json";

    /// <summary>
    /// A component's library as diffusers resolves it: <c>diffusers</c>, <c>transformers</c>, or one of diffusers' own pipeline
    /// modules by its short name (<c>stable_diffusion</c>, <c>kolors</c>, <c>wuerstchen</c>). Found live: refusing every
    /// other name refused Kolors and Stable Cascade for code they do not carry. Code a repository brings is a <c>.py</c> file.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex Library = new("^[a-z][a-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Components a recipe never loads: the worker passes <c>safety_checker=None</c> (46 D8), so its weights are not fetched.</summary>
    private static readonly HashSet<string> NotLoaded = new(StringComparer.Ordinal) { "safety_checker" };

    private static readonly string[] Pickles = [".bin", ".ckpt", ".pt", ".pth", ".pkl"];

    /// <param name="modelIndex">The repository's <c>model_index.json</c>.</param>
    /// <param name="files">Every file in the tree, with its size.</param>
    /// <param name="commit">The commit the tree was read at.</param>
    /// <param name="license">The card's licence id, already resolved (<c>license</c>, else <c>license_name</c>).</param>
    public static PipelineFacts Read(JsonElement modelIndex, IReadOnlyList<(string Path, long Size)> files, string commit, string? license, bool gated, string? baseModel)
    {
        var className = modelIndex.TryGetProperty("_class_name", out var cls) && cls.ValueKind == JsonValueKind.String ? cls.GetString() : null;
        var refusals = new List<string>();
        var components = new List<string>();

        foreach (var property in modelIndex.EnumerateObject())
        {
            if (property.Name.StartsWith('_') || property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() != 2)
            {
                continue;
            }

            var library = property.Value[0].ValueKind == JsonValueKind.String ? property.Value[0].GetString() : null;

            if (library is null)
            {
                // [null, null]: an optional component this repository leaves out.
                continue;
            }

            if (NotLoaded.Contains(property.Name))
            {
                continue;
            }

            if (!Library.IsMatch(library))
            {
                refusals.Add($"its component '{property.Name}' names the library '{library}', which is not diffusers, transformers or a diffusers pipeline module");
                continue;
            }

            components.Add(property.Name);
        }

        if (files.FirstOrDefault(f => f.Path.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) is { Path: { } code })
        {
            refusals.Add($"it carries Python code ({code}); a recipe loads weights and configuration, never a repository's code");
        }

        long full = 0, half = 0;
        var halfEverywhere = true;
        var weighted = 0;
        string? missingFull = null;

        foreach (var component in components)
        {
            var inside = files.Where(f => f.Path.StartsWith(component + "/", StringComparison.Ordinal)).ToArray();
            var tensors = inside.Where(f => f.Path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)).ToArray();
            var plain = tensors.Where(f => Variant(f.Path) is null).Sum(f => f.Size);
            var fp16 = tensors.Where(f => Variant(f.Path) == "fp16").Sum(f => f.Size);
            var pickled = inside.Any(f => Pickles.Any(p => f.Path.EndsWith(p, StringComparison.OrdinalIgnoreCase)));

            if (tensors.Length == 0)
            {
                if (pickled)
                {
                    refusals.Add($"its component '{component}' has only pickled weights (.bin), which can run code when they are loaded; a recipe loads safetensors only");
                }

                continue;
            }

            weighted++;
            full += plain;
            missingFull ??= plain == 0 && fp16 > 0 ? component : null;
            half += fp16 > 0 ? fp16 : 0;
            halfEverywhere &= fp16 > 0;

            if (plain == 0 && fp16 == 0)
            {
                refusals.Add($"its component '{component}' has safetensors only in a variant other than fp16; a recipe loads the full files or the fp16 ones");
            }
        }

        if (!halfEverywhere && missingFull is { } partial)
        {
            refusals.Add($"its component '{partial}' has only fp16 files while another has none, so neither the full files nor the fp16 variant loads the whole pipeline");
        }

        if (className is null)
        {
            refusals.Add("its model_index.json names no pipeline class (_class_name)");
        }
        else if (weighted == 0 && refusals.Count == 0)
        {
            refusals.Add("none of its components has weights in safetensors");
        }

        return new PipelineFacts(
            className,
            commit,
            license,
            gated,
            baseModel,
            full,
            halfEverywhere && weighted > 0 ? half : null,
            refusals.Count == 0 ? null : refusals[0]);
    }

    /// <summary>
    /// <c>model.fp16.safetensors</c> → fp16, <c>diffusion_pytorch_model.fp16-00001-of-00002.safetensors</c> → fp16,
    /// <c>model.safetensors</c> → null (the full files). The variant is what diffusers' <c>variant=</c> picks.
    /// </summary>
    public static string? Variant(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var stem = name.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ? name[..^".safetensors".Length] : name;
        var dot = stem.LastIndexOf('.');

        if (dot < 0)
        {
            return null;
        }

        var variant = stem[(dot + 1)..];
        var dash = variant.IndexOf('-');
        return (dash < 0 ? variant : variant[..dash]).ToLowerInvariant();
    }

    /// <summary>The card's licence: <c>license</c>, and for <c>other</c> the <c>license_name</c> it gives; null when none.</summary>
    public static string? License(JsonElement card)
    {
        var license = card.TryGetProperty("license", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        var name = card.TryGetProperty("license_name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;

        return license is null or "other" ? name ?? license : license;
    }

    /// <summary>The card's <c>base_model</c>, when it names exactly one repository.</summary>
    public static string? BaseModel(JsonElement card)
    {
        if (!card.TryGetProperty("base_model", out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array when value.GetArrayLength() == 1 && value[0].ValueKind == JsonValueKind.String => value[0].GetString(),
            _ => null
        };
    }
}
