using System.Collections.Concurrent;
using InferHub.Shared.Contracts;
using InferHub.Shared.Fit;

namespace InferHub.Node.Fit;

/// <summary>
/// Phase 118: a link and a context in, this node's options out — and an install of the one the owner chose, as the
/// pull the node already runs (D5). The model's facts are kept for a few minutes so a dialog that tries contexts
/// does not ask Hugging Face again each time.
/// </summary>
public sealed class FitService(ModelFactsReader reader, Func<MachineFacts> machine, TimeProvider? time = null)
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

    public async Task<Answer> EstimateAsync(string? link, int? context, CancellationToken cancellationToken)
    {
        var (model, canonical) = await FactsAsync(link, cancellationToken);
        var asked = context is > 0 ? context.Value : FitEstimator.DefaultContext;
        var here = machine();

        return new Answer(
            canonical,
            asked,
            model,
            here,
            FitEstimator.Estimate(model, here, asked),
            FitEstimator.Refused(model).Select(r => new Refusal(r.Engine, r.Reason)).ToArray());
    }

    /// <summary>The pull that installs one option: the engine's own command, with the chosen context (D4).</summary>
    public async Task<ModelCommand> CommandAsync(string? link, string? engine, string? variant, int? context, bool force, CancellationToken cancellationToken)
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

        var chosen = answer.Model.Variants.First(v => v.Name == option.Variant);
        var ctx = context is > 0 ? context : null;

        if (option.Engine == FitEngines.Ollama)
        {
            var name = answer.Model.Source == ModelFacts.SourceOllama
                ? chosen.Link!
                : OllamaHfName(chosen.Link) ?? throw new FitRefusedException(400, $"Ollama pulls a Hugging Face GGUF by its quant name, and '{chosen.Name}' has none it can name; install it with llama.cpp");

            return new ModelCommand(Guid.NewGuid(), ModelCommand.KindPull, name, Engine: option.EngineName, Context: ctx);
        }

        return new ModelCommand(Guid.NewGuid(), ModelCommand.KindPull, chosen.Link!, Engine: ModelCommand.EngineHuggingFace, Context: ctx);
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
