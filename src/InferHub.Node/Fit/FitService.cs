using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using InferHub.Shared.Contracts;
using InferHub.Shared.Fit;

namespace InferHub.Node.Fit;

/// <summary>
/// Phase 118: a link and a context in, this node's options out — and an install of the one the owner chose, as the
/// pull the node already runs (D5). The model's facts are kept for a few minutes so a dialog that tries contexts
/// does not ask Hugging Face again each time.
/// </summary>
public sealed class FitService(ModelFactsReader reader, Func<MachineFacts> machine, TimeProvider? time = null, IReadOnlyList<IEnginePlanner>? planners = null, RecipeFit? recipes = null)
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, ModelFacts Facts)> facts = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    public sealed record Answer(
        string Link,
        int Context,
        ModelFacts Model,
        MachineFacts Machine,
        IReadOnlyList<FitOption> Options,
        IReadOnlyList<Refusal> Refused);

    public sealed record Refusal(string Engine, string Reason);

    /// <summary>This machine as the estimate sees it — the catalogue (119) classes its list against the same facts.</summary>
    public MachineFacts Machine() => Here();

    /// <summary>The machine with the recipes as one more engine (121), so the hub's disk terms (120) read its free space too.</summary>
    private MachineFacts Here()
    {
        var here = machine();
        return recipes is null ? here : here with { Engines = [.. here.Engines, recipes.Facts()] };
    }

    public async Task<Answer> EstimateAsync(string? link, int? context, CancellationToken cancellationToken)
    {
        var (model, canonical) = await FactsAsync(link, cancellationToken);
        var asked = context is > 0 ? context.Value : FitEstimator.DefaultContext;
        var here = Here();

        // Phase 121: a picture or video pipeline is answered by the node's recipes, never by the estimate.
        if (model.Source == ModelFacts.SourceDiffusers)
        {
            if (recipes is null)
            {
                return new Answer(canonical, asked, model, here, [], [new Refusal(FitEngines.Diffusion, "this node has no image or video recipes (no tool runtime)")]);
            }

            var pictures = await recipes.OptionsAsync(model, here, cancellationToken);
            return new Answer(canonical, asked, model, here, pictures.Options.OrderByDescending(o => o.Score).ToArray(), pictures.Refused);
        }

        var options = FitEstimator.Estimate(model, here, asked).ToList();

        // 118 D6: an engine that plans its own placement answers for itself.
        foreach (var planner in planners ?? [])
        {
            if (!options.Any(o => o.Engine == planner.Engine) || here.Engines.FirstOrDefault(e => e.Kind == planner.Engine) is not { } engine)
            {
                continue;
            }

            var answer = await planner.PlanAsync(model, here, engine, asked, cancellationToken);

            if (answer?.Options is { Count: > 0 } planned)
            {
                options.RemoveAll(o => o.Engine == planner.Engine);
                options.AddRange(planned);
            }
            else if (answer?.Unavailable is { } why)
            {
                options = options.Select(o => o.Engine == planner.Engine ? o with { Why = [why, .. o.Why] } : o).ToList();
            }
        }

        return new Answer(
            canonical,
            asked,
            model,
            here,
            options.OrderByDescending(o => o.Score).ThenBy(o => o.WeightsMiB).ToArray(),
            FitEstimator.Refused(model).Select(r => new Refusal(r.Engine, r.Reason)).ToArray());
    }

    /// <summary>One option chosen for an install: the estimate it came from, the option, and the pull that installs it.</summary>
    /// <param name="Recipe">Phase 121: a recipe the node writes before the pull (one derived for this repository); null otherwise.</param>
    public sealed record Plan(Answer Answer, FitOption Option, ModelCommand Command, JsonObject? Recipe = null);

    /// <summary>
    /// What has to be on disk before the plan's command runs — a derived recipe (121). Called after every check the caller
    /// makes (the hub's disk terms), so a refused install leaves nothing behind.
    /// </summary>
    public void Prepare(Plan plan)
    {
        if (plan.Recipe is { } recipe)
        {
            (recipes ?? throw new FitRefusedException(409, "this node has no image or video recipes")).Write(recipe);
        }
    }

    /// <summary>The pull that installs one option: the engine's own command, with the chosen context (D4).</summary>
    public async Task<ModelCommand> CommandAsync(string? link, string? engine, string? variant, int? context, bool force, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(link, engine, variant, context, force, cancellationToken);
        Prepare(plan);
        return plan.Command;
    }

    /// <summary>
    /// <see cref="CommandAsync"/> with what it was decided from — the hub's install (120) checks the owner's disk terms
    /// against the same option.
    /// </summary>
    public async Task<Plan> PlanAsync(string? link, string? engine, string? variant, int? context, bool force, CancellationToken cancellationToken)
    {
        var answer = await EstimateAsync(link, context, cancellationToken);
        var option = answer.Options.FirstOrDefault(o =>
                         string.Equals(o.Engine, engine, StringComparison.OrdinalIgnoreCase)
                         && (variant is null || string.Equals(o.Variant, variant, StringComparison.OrdinalIgnoreCase)))
                     ?? throw new FitRefusedException(400, answer.Refused.FirstOrDefault(r => string.Equals(r.Engine, engine, StringComparison.OrdinalIgnoreCase)) is { } refusal
                         ? $"{engine} cannot run this model: {refusal.Reason}"
                         : $"no option '{engine}'{(variant is null ? "" : $" / '{variant}'")} for this model; the estimate offers {string.Join(", ", answer.Options.Select(o => $"{o.Engine}/{o.Variant}").Distinct())}");

        if (!option.Available)
        {
            throw new FitRefusedException(409, $"{option.Engine} cannot install here: {option.Why.FirstOrDefault()}");
        }

        if (!option.Fits && !force)
        {
            throw new FitRefusedException(409, $"the estimate says {option.Engine} / {option.Variant} does not fit at {answer.Context} tokens ({option.Why.FirstOrDefault()}); send force=true to install it anyway");
        }

        if (option.Engine == FitEngines.Diffusion)
        {
            // 121: phase 48's pull of a recipe — the one here, or the one written for this repository first.
            var install = await recipes!.InstallAsync(answer.Model, answer.Machine, option.Variant, cancellationToken);
            return new Plan(answer, option, new ModelCommand(Guid.NewGuid(), ModelCommand.KindPull, install.RecipeId, Tool: RecipeFit.Tool), install.Recipe);
        }

        var chosen = answer.Model.Variants.First(v => v.Name == option.Variant);
        var ctx = context is > 0 ? context : null;

        if (option.Engine == FitEngines.Ollama)
        {
            var name = answer.Model.Source == ModelFacts.SourceOllama
                ? chosen.Link!
                : OllamaHfName(chosen.Link) ?? throw new FitRefusedException(400, $"Ollama pulls a Hugging Face GGUF by its quant name, and '{chosen.Name}' has none it can name; install it with llama.cpp");

            return new Plan(answer, option, new ModelCommand(Guid.NewGuid(), ModelCommand.KindPull, name, Engine: option.EngineName, Context: ctx));
        }

        return new Plan(answer, option, new ModelCommand(Guid.NewGuid(), ModelCommand.KindPull, chosen.Link!, Engine: ModelCommand.EngineHuggingFace, Context: ctx));
    }

    /// <summary><c>owner/repo:Q4_K_M</c> → <c>hf.co/owner/repo:Q4_K_M</c>, what <c>ollama pull</c> takes; null for a file link.</summary>
    internal static string? OllamaHfName(string? link) =>
        link is null || link.Contains("//", StringComparison.Ordinal) || link.Contains('@') ? null : "hf.co/" + link;

    private async Task<(ModelFacts Facts, string Link)> FactsAsync(string? link, CancellationToken cancellationToken)
    {
        if (!ModelLink.TryParse(link, out var parsed, out var error))
        {
            throw new FitRefusedException(400, error!);
        }

        var key = parsed!.HuggingFace?.ToString() ?? parsed.Ollama!.PullName;
        var now = clock.GetUtcNow();

        if (facts.TryGetValue(key, out var kept) && now - kept.At < Keep)
        {
            return (kept.Facts, key);
        }

        var read = await reader.ReadAsync(parsed, cancellationToken);
        facts[key] = (now, read);

        foreach (var old in facts.Where(p => now - p.Value.At >= Keep).Select(p => p.Key).ToArray())
        {
            facts.TryRemove(old, out _);
        }

        return (read, key);
    }
}
