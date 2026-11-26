using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InferHub.Node.Backends.HuggingFace;
using InferHub.Node.Configuration;
using InferHub.Node.Tools;
using InferHub.Shared.Fit;
using InferHub.Shared.HuggingFace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Fit;

/// <summary>
/// Phase 121: a picture or video model from a link, answered by the node's recipes — never by an estimate. A recipe here
/// that pins the repository is the option as it stands; a repository whose pipeline class one of the recipes here runs
/// gets a recipe written from that one (its sizes, steps and dtype, this repository's commit, licence and weights); any
/// other is said, with the reason. The install is phase 48's pull, so nothing downloads that a recipe does not name.
/// </summary>
/// <remarks>
/// The node still does not learn diffusion (41 D1, 48): it compares two strings (<c>repo</c>, <c>pipeline</c>), copies a
/// document, and scales one number (<c>vramMiB</c>) by the weights' bytes. What runs the recipe is the worker.
/// </remarks>
public sealed partial class RecipeFit(
    IToolRuntime runtime,
    IOptionsMonitor<ToolOptions> tools,
    IOptionsMonitor<NodeOptions> node,
    Func<HuggingFaceClient>? huggingFace,
    ILogger<RecipeFit> logger,
    TimeProvider? time = null)
{
    public const string Tool = "diffusion";

    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);

    /// <summary>Licences permissive in this catalogue's sense whether or not a recipe here already names them.</summary>
    private static readonly HashSet<string> AlwaysPermissive = new(StringComparer.OrdinalIgnoreCase) { "apache-2.0", "mit" };

    /// <summary>The Hub's licence ids beside the ones the shipped recipes spell (a recipe's id is what the owner accepts).</summary>
    private static readonly Dictionary<string, string> LicenceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["creativeml-openrail-m"] = "CreativeML-OpenRAIL-M",
        ["openrail++"] = "CreativeML-OpenRAIL++-M",
        ["apache-2.0"] = "Apache-2.0",
        ["mit"] = "MIT"
    };

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, long? Bytes)> templateBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotId();

    /// <summary>One answer for a link: the options, and why there are none when there are none.</summary>
    public sealed record Answer(IReadOnlyList<FitOption> Options, IReadOnlyList<FitService.Refusal> Refused);

    /// <summary>What installing an option needs: the recipe to write first (null for one already here), and its id.</summary>
    public sealed record Install(string RecipeId, JsonObject? Recipe);

    /// <summary>The recipes on this box as documents, with the directory each came from.</summary>
    internal IReadOnlyList<(JsonObject Recipe, string Directory)> Recipes()
    {
        var found = new List<(JsonObject, string)>();

        foreach (var directory in tools.CurrentValue.Image.RecipeDirectories())
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject recipe
                        && Str(recipe, "id") is not null && Str(recipe, "revision") is not null)
                    {
                        found.Add((recipe, directory));
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    logger.LogDebug(ex, "Not reading recipe {Path}", path);
                }
            }
        }

        return found;
    }

    /// <summary>The recipes as an engine, for the estimate's machine facts: whether it can install here, where, the free disk.</summary>
    public EngineFacts Facts()
    {
        var options = tools.CurrentValue;
        var added = options.Image.AddedRecipeDirectory;
        long? free = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(added))
            {
                Directory.CreateDirectory(added);
                free = new DriveInfo(Path.GetFullPath(added)).AvailableFreeSpace / (1024 * 1024);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogDebug(ex, "Could not read the free disk at {Directory}", added);
        }

        var vram = node.CurrentValue.Vram;
        return new EngineFacts(FitEngines.Diffusion, Tool, Absent() is null, Absent(), added, free,
            vram.BudgetMiB > 0 ? vram.BudgetMiB - Math.Max(0, vram.ReserveMiB) : null, null);
    }

    /// <summary>Why this node cannot install a recipe at all, in a sentence; null when it can.</summary>
    private string? Absent()
    {
        var options = tools.CurrentValue;

        if (!runtime.Enabled || !runtime.ToolIds.Contains(Tool, StringComparer.OrdinalIgnoreCase))
        {
            return $"this node runs no '{Tool}' tool ({ToolOptions.SectionName}:Enabled, and '{Tool}' in {ToolOptions.SectionName}:Allowed — the :diffusion image has both)";
        }

        return options.AllowModelDownload
            ? null
            : $"{ToolOptions.SectionName}:AllowModelDownload is false, so the worker fetches no weights";
    }

    public async Task<Answer> OptionsAsync(ModelFacts model, MachineFacts machine, CancellationToken cancellationToken)
    {
        var pipeline = model.Pipeline!;
        var recipes = Recipes();
        var engine = machine.Engines.FirstOrDefault(e => e.Kind == FitEngines.Diffusion) ?? Facts();
        var options = new List<FitOption>();

        // A recipe here that pins this repository is the answer as it stands (qwen-image and qwen-360 share one: two options).
        foreach (var (recipe, _) in recipes.Where(r => string.Equals(Str(r.Recipe, "repo"), model.Name, StringComparison.OrdinalIgnoreCase)))
        {
            var why = new List<string> { $"recipe '{Str(recipe, "id")}' on this node pins {model.Name} at {Short(Str(recipe, "revision"))}" };

            if (!string.Equals(Str(recipe, "revision"), pipeline.Commit, StringComparison.OrdinalIgnoreCase))
            {
                why.Add($"the repository is at {Short(pipeline.Commit)} now; the recipe's own pin is what installs");
            }

            var bytes = Str(recipe, "variant") == "fp16" && pipeline.Fp16Bytes is { } half ? half : pipeline.Bytes;
            options.Add(Option(recipe, recipes, engine, machine, bytes, why, score: 200));
        }

        if (options.Count > 0)
        {
            return new Answer(options, []);
        }

        if (pipeline.Refusal is { } refusal)
        {
            return new Answer([], [new FitService.Refusal(FitEngines.Diffusion, refusal)]);
        }

        var templates = recipes
            .Where(r => string.Equals(Str(r.Recipe, "pipeline"), pipeline.ClassName, StringComparison.Ordinal) && r.Recipe["adapters"] is null)
            .Select(r => r.Recipe)
            .ToArray();

        if (templates.Length == 0)
        {
            var known = recipes.Select(r => Str(r.Recipe, "pipeline")).Where(p => p is not null).Distinct().Order().ToArray();
            return new Answer([], [new FitService.Refusal(FitEngines.Diffusion,
                $"no recipe on this node runs {pipeline.ClassName}" + (known.Length == 0 ? " (this node has no recipes)" : $"; the recipes here run {string.Join(", ", known)}"))]);
        }

        var memory = pipeline.Fp16Bytes ?? pipeline.Bytes;
        var template = await TemplateAsync(templates, pipeline, memory, cancellationToken);
        var derived = Derive(template.Recipe, model.Name, pipeline, recipes, template.Bytes);
        var derivedWhy = new List<string>
        {
            $"a recipe written from '{Str(template.Recipe, "id")}' ({pipeline.ClassName}" + (template.ByBase ? $", the card's base model" : "") + "): its sizes, steps and dtype, this repository's commit, licence and weights",
            template.Bytes is { } t && t > 0
                ? $"VRAM: {Str(template.Recipe, "id")}'s {Int(template.Recipe, "vramMiB")} MiB × {Mib(memory)} / {Mib(t)} of weights = {Int(derived, "vramMiB")} MiB (counted at the files' stored size — a repository without fp16 files may need less)"
                : $"VRAM: {Str(template.Recipe, "id")}'s {Int(template.Recipe, "vramMiB")} MiB, not scaled — its own weights could not be read"
        };

        if (string.IsNullOrWhiteSpace(tools.CurrentValue.Image.AddedRecipeDirectory))
        {
            derivedWhy.Insert(0, $"{ToolOptions.SectionName}:Image:AddedRecipeDirectory is not set, so this node has nowhere to write a recipe");
        }

        options.Add(Option(derived, recipes, engine, machine, memory, derivedWhy, score: 100));
        return new Answer(options, []);
    }

    /// <summary>The recipe to write for an option (null for one already here), and the id the pull names.</summary>
    public async Task<Install> InstallAsync(ModelFacts model, MachineFacts machine, string variant, CancellationToken cancellationToken)
    {
        if (Recipes().FirstOrDefault(r => string.Equals(Str(r.Recipe, "id"), variant, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Str(r.Recipe, "repo"), model.Name, StringComparison.OrdinalIgnoreCase)) is { Recipe: { } here })
        {
            return new Install(Str(here, "id")!, null);
        }

        var pipeline = model.Pipeline!;
        var recipes = Recipes();
        var templates = recipes.Where(r => string.Equals(Str(r.Recipe, "pipeline"), pipeline.ClassName, StringComparison.Ordinal) && r.Recipe["adapters"] is null).Select(r => r.Recipe).ToArray();
        var template = await TemplateAsync(templates, pipeline, pipeline.Fp16Bytes ?? pipeline.Bytes, cancellationToken);
        var derived = Derive(template.Recipe, model.Name, pipeline, recipes, template.Bytes);

        if (string.IsNullOrWhiteSpace(tools.CurrentValue.Image.AddedRecipeDirectory))
        {
            throw new FitRefusedException(409, $"{ToolOptions.SectionName}:Image:AddedRecipeDirectory is not set, so this node has nowhere to write a recipe");
        }

        return new Install(Str(derived, "id")!, derived);
    }

    /// <summary>Writes a derived recipe where the worker and the node read added ones, and has the node read its catalogue again.</summary>
    public string Write(JsonObject recipe)
    {
        var directory = tools.CurrentValue.Image.AddedRecipeDirectory
                        ?? throw new FitRefusedException(409, $"{ToolOptions.SectionName}:Image:AddedRecipeDirectory is not set");
        Directory.CreateDirectory(directory);
        var id = Str(recipe, "id")!;
        var path = Path.Combine(directory, id + ".json");
        var temporary = path + ".tmp";

        // Relaxed escaping: the owner reads this file, and "sd15's" is clearer than "sd15's".
        File.WriteAllText(temporary, recipe.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.Move(temporary, path, overwrite: true);
        runtime.ReloadRecipes();
        logger.LogInformation("Wrote recipe '{Recipe}' for {Repo} at {Revision} to {Path}", id, Str(recipe, "repo"), Str(recipe, "revision"), path);
        return path;
    }

    // ------------------------------------------------------------------ the option

    private FitOption Option(JsonObject recipe, IReadOnlyList<(JsonObject Recipe, string Directory)> recipes, EngineFacts engine, MachineFacts machine, long bytes, List<string> why, int score)
    {
        var options = tools.CurrentValue;
        var vramNeed = Int(recipe, "vramMiB");
        var licence = recipe["license"] as JsonObject;
        var licenceId = licence is null ? "unknown" : Str(licence, "id") ?? "unknown";
        var permissive = licence?["permissive"] is JsonValue p && p.TryGetValue<bool>(out var allowed) && allowed;
        var accepted = permissive || options.Image.AcceptsLicense(licenceId);
        var gated = recipe["gated"] is JsonValue g && g.TryGetValue<bool>(out var isGated) && isGated;
        var cpuViable = recipe["cpuViable"] is JsonValue c && c.TryGetValue<bool>(out var viable) && viable;
        var media = Str(recipe, "media") ?? "image";
        var card = machine.Gpus.Count == 0 ? (long?)null : machine.Gpus.Max(gpu => gpu.TotalMiB);
        var vram = node.CurrentValue.Vram;
        bool fits;
        string placement;

        if (vram.BudgetMiB > 0)
        {
            fits = VramBudget.Fits(vram.BudgetMiB, vram.ReserveMiB, vramNeed);
            placement = FitPlacements.Gpu;
            why.Add($"needs {vramNeed} MiB of the card; this node budgets {vram.BudgetMiB - Math.Max(0, vram.ReserveMiB)} MiB for models (Node:Vram:BudgetMiB less ReserveMiB)");
        }
        else if (card is { } total)
        {
            fits = vramNeed <= total;
            placement = FitPlacements.Gpu;
            why.Add($"needs {vramNeed} MiB of the card; the largest here has {total} MiB (no Node:Vram:BudgetMiB is set, so nothing else on it is counted)");
        }
        else
        {
            fits = cpuViable && (!options.Image.RequireGpu || options.Image.AllowSlowCpu);
            placement = FitPlacements.Cpu;
            why.Add(cpuViable
                ? (fits ? "no NVIDIA card seen; this recipe runs on the CPU (slowly)" : "no NVIDIA card seen, and Tools:Image:RequireGpu is true — the worker does not start without a card")
                : "no NVIDIA card seen, and this recipe is not one that runs on the CPU");
        }

        var available = engine.Installs;

        if (engine.Absent is { } absent)
        {
            why.Insert(0, absent);
        }

        if (!accepted)
        {
            available = false;
            why.Insert(0, $"licensed under '{licenceId}', which this node has not accepted: read {Str(licence ?? new JsonObject(), "url") ?? "the model card"} and, if you accept it, add \"{licenceId}\" to {ToolOptions.SectionName}:Image:AcceptedLicenses");
        }

        if (gated && string.IsNullOrWhiteSpace(options.Image.HuggingFaceToken))
        {
            available = false;
            why.Insert(0, $"gated on Hugging Face: accept its terms at https://huggingface.co/{Str(recipe, "repo")} and set {ToolOptions.SectionName}:Image:HuggingFaceToken to a read token");
        }

        if (score < 200 && string.IsNullOrWhiteSpace(options.Image.AddedRecipeDirectory))
        {
            available = false;
        }

        var mib = (bytes + (1 << 20) - 1) >> 20;

        return new FitOption(
            FitEngines.Diffusion,
            Tool,
            Str(recipe, "id")!,
            Str(recipe, "repo"),
            available,
            fits,
            placement,
            mib,
            0,
            placement == FitPlacements.Gpu ? vramNeed : 0,
            placement == FitPlacements.Cpu ? mib : 0,
            mib,
            0,
            null,
            null,
            media,
            false,
            why,
            score + (fits ? 20 : 0) + (available ? 10 : 0));
    }

    // ------------------------------------------------------------------ the derived recipe

    /// <summary>
    /// The template for a repository: the recipe whose repo the card names as its base model, else the one whose weights
    /// are closest in size, then the one with more default steps (a distilled model's few steps would spoil a full one).
    /// </summary>
    private async Task<(JsonObject Recipe, long? Bytes, bool ByBase)> TemplateAsync(IReadOnlyList<JsonObject> templates, PipelineFacts pipeline, long memory, CancellationToken cancellationToken)
    {
        if (templates.Count == 0)
        {
            throw new FitRefusedException(400, $"no recipe on this node runs {pipeline.ClassName}");
        }

        if (pipeline.BaseModel is { } baseModel && templates.FirstOrDefault(t => string.Equals(Str(t, "repo"), baseModel, StringComparison.OrdinalIgnoreCase)) is { } byBase)
        {
            return (byBase, await BytesAsync(byBase, cancellationToken), true);
        }

        var sized = new List<(JsonObject Recipe, long? Bytes)>();

        foreach (var template in templates)
        {
            sized.Add((template, await BytesAsync(template, cancellationToken)));
        }

        var chosen = sized
            .OrderBy(s => s.Bytes is { } b && b > 0 && memory > 0 ? Math.Abs(Math.Log((double)memory / b)) : double.MaxValue)
            .ThenByDescending(s => (s.Recipe["defaults"] as JsonObject)?["steps"] is JsonValue v && v.TryGetValue<int>(out var steps) ? steps : 0)
            .ThenBy(s => Str(s.Recipe, "id"), StringComparer.Ordinal)
            .First();

        return (chosen.Recipe, chosen.Bytes, false);
    }

    /// <summary>A template's own weights at its pin, as the link's are counted (its fp16 files when it loads those).</summary>
    private async Task<long?> BytesAsync(JsonObject template, CancellationToken cancellationToken)
    {
        var repo = Str(template, "repo");
        var revision = Str(template, "revision");

        if (repo is null || revision is null || huggingFace is null)
        {
            return null;
        }

        var key = $"{repo}@{revision}";
        var now = clock.GetUtcNow();

        if (templateBytes.TryGetValue(key, out var kept) && now - kept.At < Keep)
        {
            return kept.Bytes;
        }

        long? bytes = null;

        try
        {
            var hf = huggingFace();
            var slash = repo.IndexOf('/');
            var reference = new HfReference(repo[..slash], repo[(slash + 1)..], revision);
            var tree = await hf.TreeAsync(reference, revision, cancellationToken);
            using var response = await hf.RangeAsync(reference, revision, DiffusersRepo.ModelIndex, 1 << 20, cancellationToken);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var index = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var facts = DiffusersRepo.Read(index.RootElement, tree.Select(f => (f.Path, f.Size)).ToArray(), revision, null, false, null);
            bytes = Str(template, "variant") == "fp16" && facts.Fp16Bytes is { } half ? half : facts.Bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or IOException or ArgumentException
                                       || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Could not read recipe {Recipe}'s weights at {Repo}@{Revision}: {Reason}", Str(template, "id"), repo, revision, ex.Message);
        }

        templateBytes[key] = (now, bytes);
        return bytes;
    }

    /// <summary>The recipe written for a repository from a template (D2) — pure, so a test reads exactly what lands on disk.</summary>
    internal static JsonObject Derive(JsonObject template, string repo, PipelineFacts pipeline, IReadOnlyList<(JsonObject Recipe, string Directory)> recipes, long? templateBytes)
    {
        var recipe = (JsonObject)template.DeepClone();

        foreach (var key in new[] { "id", "notes", "repo", "revision", "license", "gated", "variant", "vramMiB", "vramUnquantizedMiB", "adapters", "derivedFrom", "useSafetensors" })
        {
            recipe.Remove(key);
        }

        var id = IdFor(repo, recipes.Select(r => Str(r.Recipe, "id")!).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var memory = pipeline.Fp16Bytes ?? pipeline.Bytes;
        var vram = Int(template, "vramMiB");

        if (templateBytes is { } t && t > 0 && memory > 0 && vram > 0)
        {
            vram = (int)(Math.Ceiling(vram * (double)memory / t / 100) * 100);
        }

        var licenceId = pipeline.License is { } l ? LicenceNames.GetValueOrDefault(l, l) : "unknown";
        var permissive = AlwaysPermissive.Contains(licenceId)
                         || recipes.Any(r => r.Recipe["license"] is JsonObject other
                                             && string.Equals(Str(other, "id"), licenceId, StringComparison.OrdinalIgnoreCase)
                                             && other["permissive"] is JsonValue p && p.TryGetValue<bool>(out var yes) && yes);

        var ordered = new JsonObject
        {
            ["id"] = id,
            ["notes"] = $"Written by the node (phase 121) from recipe '{Str(template, "id")}' for {repo} at {pipeline.Commit}: the template's pipeline, sizes, steps, dtype and quantization; this repository's commit, licence and weights. vramMiB is the template's scaled by the weights' bytes, not measured.",
            ["derivedFrom"] = Str(template, "id"),
            ["repo"] = repo,
            ["revision"] = pipeline.Commit,
            ["license"] = new JsonObject
            {
                ["id"] = licenceId,
                ["permissive"] = permissive,
                ["url"] = $"https://huggingface.co/{repo}"
            }
        };

        if (pipeline.Gated)
        {
            ordered["gated"] = true;
        }

        if (pipeline.Fp16Bytes is not null)
        {
            ordered["variant"] = "fp16";
        }

        ordered["useSafetensors"] = true;
        ordered["vramMiB"] = vram;

        foreach (var (key, value) in recipe.ToArray())
        {
            recipe.Remove(key);
            ordered[key] = value;
        }

        return ordered;
    }

    /// <summary><c>Lykon/dreamshaper-8</c> → <c>dreamshaper-8</c>, or <c>lykon-dreamshaper-8</c> when a recipe here has that id.</summary>
    internal static string IdFor(string repo, IReadOnlySet<string> taken)
    {
        var parts = repo.Split('/');
        var name = NotId().Replace(parts[^1].ToLowerInvariant(), "-").Trim('-');
        var full = NotId().Replace(repo.ToLowerInvariant(), "-").Trim('-');

        if (!taken.Contains(name))
        {
            return name;
        }

        if (!taken.Contains(full))
        {
            return full;
        }

        for (var i = 2; ; i++)
        {
            if (!taken.Contains($"{full}-{i}"))
            {
                return $"{full}-{i}";
            }
        }
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static int Int(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    private static string Short(string? sha) => sha is { Length: > 12 } ? sha[..12] : sha ?? "?";

    private static string Mib(long bytes) => (bytes / (1024.0 * 1024)).ToString("0", CultureInfo.InvariantCulture) + " MiB";
}
