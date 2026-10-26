using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using InferHub.Node.Backends.Colibri;
using InferHub.Node.Backends.HuggingFace;
using InferHub.Node.Backends.Strata;
using InferHub.Shared.Fit;
using InferHub.Shared.HuggingFace;
using InferHub.Shared.Strata;

namespace InferHub.Node.Fit;

/// <summary>
/// Phase 118 D6: an engine that plans its own placement is asked, not second-guessed. Strata answers with its own size
/// table and recommendation for this PC (<c>tools/strata_mcp.py</c>, the rules its setup and app use); colibri with
/// <c>coli plan --json</c> for a model it already has. A planner that cannot answer returns null and the estimate's own
/// option stands, with the reason.
/// </summary>
public interface IEnginePlanner
{
    string Engine { get; }

    Task<PlannerAnswer?> PlanAsync(ModelFacts model, MachineFacts machine, EngineFacts engine, int context, CancellationToken cancellationToken);
}

/// <summary>The planner's options (replacing the estimate's for its engine), or the sentence why it could not plan.</summary>
public sealed record PlannerAnswer(IReadOnlyList<FitOption>? Options, string? Unavailable = null);

internal static class PlannerProcess
{
    /// <summary>Runs a planner and parses its stdout as JSON; stderr is kept for the sentence when it fails.</summary>
    public static async Task<(JsonDocument? Json, string? Error)> RunAsync(ProcessStartInfo info, TimeSpan timeout, CancellationToken cancellationToken)
    {
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;

        using var process = new Process { StartInfo = info };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (null, $"could not start {Path.GetFileName(info.FileName)}: {ex.Message}");
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timer.Token);
        var stderr = process.StandardError.ReadToEndAsync(timer.Token);

        try
        {
            await process.WaitForExitAsync(timer.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return (null, $"the planner took longer than {timeout.TotalSeconds:0} s");
        }

        var text = await stdout;
        var errors = await stderr;

        if (process.ExitCode != 0)
        {
            var last = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
            return (null, $"the planner exited with code {process.ExitCode}: {(last.Length > 240 ? last[..240] : last)}");
        }

        try
        {
            return (JsonDocument.Parse(text), null);
        }
        catch (JsonException)
        {
            return (null, "the planner's answer was not JSON");
        }
    }

    internal static long Mib(double gb) => (long)Math.Round(gb * 1e9 / (1 << 20));
}

/// <summary>Strata's own fit for this PC: <c>Strata.model_table(hw)</c> and <c>recommend(hw)</c>, in Strata's own Python.</summary>
public sealed class StrataPlanner(StrataOptions options) : IEnginePlanner
{
    // stdout carries only the JSON; strata_mcp logs to stderr. RAM and VRAM are lowered to the owner's limits before
    // Strata judges, so its verdict is about what this node may use.
    internal const string Script = """
        import json, sys
        from pathlib import Path
        root = Path(sys.argv[1]); ram = float(sys.argv[2]); vram = float(sys.argv[3])
        sys.path.insert(0, str(root / "tools"))
        import strata_mcp as M
        s = M.Strata(root, root)
        hw = s.hardware()
        if ram > 0 and hw.get("ram_gb"):
            hw["ram_gb"] = min(hw["ram_gb"], ram)
        if vram >= 0:
            for g in hw.get("gpus") or []:
                g["vram_gb"] = min(g.get("vram_gb") or 0, vram)
        sys.stdout.write(json.dumps({"recommend": s.recommend(hw), "table": s.model_table(hw), "ram_gb": hw.get("ram_gb")}, default=str))
        """;

    public string Engine => FitEngines.Strata;

    public async Task<PlannerAnswer?> PlanAsync(ModelFacts model, MachineFacts machine, EngineFacts engine, int context, CancellationToken cancellationToken)
    {
        if (model.Source != ModelFacts.SourceStrata || string.IsNullOrWhiteSpace(options.Root) || !File.Exists(Path.Combine(options.Root.Trim(), "tools", "strata_mcp.py")))
        {
            return null;
        }

        var info = new ProcessStartInfo(options.ResolvedPython()) { WorkingDirectory = options.Root.Trim() };

        foreach (var argument in new[]
                 {
                     "-c", Script, options.Root.Trim(),
                     (engine.RamLimitMiB is { } ram ? ram * (1 << 20) / 1e9 : 0).ToString("0.0", CultureInfo.InvariantCulture),
                     (engine.VramLimitMiB is { } vram ? vram * (1 << 20) / 1e9 : -1).ToString("0.0", CultureInfo.InvariantCulture)
                 })
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["PYTHONIOENCODING"] = "utf-8";
        var (json, error) = await PlannerProcess.RunAsync(info, TimeSpan.FromSeconds(90), cancellationToken);

        using (json)
        {
            return json is null
                ? new PlannerAnswer(null, $"Strata's own planner did not answer ({error}); the sizes are judged by its table alone")
                : new PlannerAnswer(Map(json.RootElement, model, machine, engine, context));
        }
    }

    /// <summary>Strata's rows for the sizes this link names, as options — pure, so the tests read it from a canned answer.</summary>
    internal static IReadOnlyList<FitOption> Map(JsonElement answer, ModelFacts model, MachineFacts machine, EngineFacts engine, int context)
    {
        var recommend = answer.TryGetProperty("recommend", out var r) ? r : default;
        string? Rec(string key) => recommend.ValueKind == JsonValueKind.Object && recommend.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? recContext = recommend.ValueKind == JsonValueKind.Object && recommend.TryGetProperty("context", out var c) && c.TryGetInt32(out var n) ? n : null;
        var options = new List<FitOption>();

        // Strata fills the card down to its reserve (--vram-reserve-mib, 1536 by default), or to the owner's limit (111 D5).
        var card = Math.Max(0, machine.Gpus.Sum(g => g.TotalMiB) - 1536);

        if (engine.VramLimitMiB is { } limit)
        {
            card = Math.Min(card, limit);
        }

        foreach (var variant in model.Variants)
        {
            if (variant.Link is null || !HfReference.TryParse(variant.Link, null, out var reference, out _)
                || !StrataModels.TryMatch(reference!, out var family, out var size, out _))
            {
                continue;
            }

            var row = Rows(answer).FirstOrDefault(x => x.Family == family!.Name && x.Size.TryGetProperty("model", out var m) && m.GetString() == size!.Name).Size;

            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var verdict = row.TryGetProperty("on_this_pc", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "not judged";
            double Gb(string key) => row.TryGetProperty(key, out var x) && x.TryGetDouble(out var d) ? d : 0;
            var (fits, placement, speed) = Verdict(verdict);
            var recommended = Rec("family") == family!.Name && Rec("model") == size!.Name;
            var why = new List<string>();

            if (!engine.Installs && engine.Absent is { } absent)
            {
                why.Add(absent);
            }

            why.Add($"Strata's own verdict for this PC: {verdict}");
            why.Add($"it keeps the dense part, the KV cache and as many experts as fit on the card, and needs about {Gb("memory_gb"):0.#} GB of RAM + VRAM for its weights ({Gb("experts_gb"):0.#} GB of experts); its RAM table says {Gb("ram_needed_gb"):0} GB of RAM");

            if (recommended && Rec("why") is { } because)
            {
                why.Add("Strata recommends this size here: " + because);
            }

            if (recContext is { } pick && context > pick)
            {
                why.Add($"Strata pre-selects a {pick:N0}-token context for this card; more takes VRAM from the experts it keeps there");
            }

            why.Add("the context goes to Strata's setup (--context)");
            var score = FitEstimator.ScoreOf(model, variant, placement, speed, fits, engine.Installs) + (recommended ? 2 : 0);

            options.Add(new FitOption(
                FitEngines.Strata, engine.Name, variant.Name, variant.Link, engine.Installs, fits, fits ? placement : FitPlacements.None,
                PlannerProcess.Mib(Gb("download_gb")), 0, fits ? card : 0, PlannerProcess.Mib(Gb("memory_gb")), PlannerProcess.Mib(Gb("download_gb")),
                context, null, null, fits ? speed : "—", true, why, score));
        }

        return options;
    }

    private static IEnumerable<(string Family, JsonElement Size)> Rows(JsonElement answer)
    {
        if (!answer.TryGetProperty("table", out var table) || table.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var family in table.EnumerateArray())
        {
            var name = family.TryGetProperty("family", out var f) ? f.GetString() ?? "" : "";

            if (family.TryGetProperty("sizes", out var sizes) && sizes.ValueKind == JsonValueKind.Array)
            {
                foreach (var size in sizes.EnumerateArray())
                {
                    yield return (name, size);
                }
            }
        }
    }

    /// <summary>Strata's words → fits, placement, speed: "fits", "tight", "fits in the low-RAM mode", "…read from the SSD", "does not fit".</summary>
    internal static (bool Fits, string Placement, string Speed) Verdict(string verdict)
    {
        var text = verdict.ToLowerInvariant();

        if (text.Contains("does not fit") || text == "not judged")
        {
            return (false, FitPlacements.None, "—");
        }

        if (text.Contains("ssd"))
        {
            return (true, FitPlacements.StreamFromDisk, text.StartsWith("slow", StringComparison.Ordinal) ? "very slow" : "slow");
        }

        if (text.Contains("low-ram") || text.StartsWith("tight", StringComparison.Ordinal))
        {
            return (true, FitPlacements.Strata, "slow");
        }

        return (true, FitPlacements.Strata, "medium");
    }
}

/// <summary>colibri's own plan for a model it already has: <c>coli plan --json --model dir --ctx N</c>.</summary>
public sealed class ColibriPlanner(ColibriOptions options) : IEnginePlanner
{
    public string Engine => FitEngines.Colibri;

    public async Task<PlannerAnswer?> PlanAsync(ModelFacts model, MachineFacts machine, EngineFacts engine, int context, CancellationToken cancellationToken)
    {
        if (model.Source != ModelFacts.SourceCheckpoint || ConvertedDirectory(model.Name) is not { } directory)
        {
            return null;
        }

        var serve = options.Serve;
        var info = new ProcessStartInfo(serve.Python);

        foreach (var argument in new[] { serve.Launcher, "plan", "--json", "--model", directory, "--ctx", context.ToString(CultureInfo.InvariantCulture) })
        {
            info.ArgumentList.Add(argument);
        }

        // The owner's 102 limits, in coli's own flags for a plan (a plan launches nothing).
        if (engine.RamLimitMiB is { } ram)
        {
            info.ArgumentList.Add("--ram");
            info.ArgumentList.Add(Math.Max(1, ram / 1024).ToString(CultureInfo.InvariantCulture));
        }

        if (engine.VramLimitMiB is { } vram)
        {
            info.ArgumentList.Add("--vram");
            info.ArgumentList.Add((vram / 1024.0).ToString("0.0", CultureInfo.InvariantCulture));
        }

        if (serve.Vulkan is { } vulkan)
        {
            info.Environment["COLI_VULKAN"] = vulkan ? "1" : "0";
        }

        info.Environment["PYTHONIOENCODING"] = "utf-8";
        var (json, error) = await PlannerProcess.RunAsync(info, TimeSpan.FromSeconds(120), cancellationToken);

        using (json)
        {
            return json is null
                ? new PlannerAnswer(null, $"colibri's own planner did not answer ({error}); this is the node's estimate")
                : new PlannerAnswer([Map(json.RootElement, model, engine, context, directory)]);
        }
    }

    /// <summary>The catalogue directory a link's checkpoint was (or would be) converted into, when it exists.</summary>
    internal string? ConvertedDirectory(string repoId)
    {
        var name = HuggingFaceStore.Name(repoId.Split('/').Last().ToLowerInvariant());
        var serve = options.Serve;

        if (serve.Models.TryGetValue(name, out var named) && Directory.Exists(named))
        {
            return named;
        }

        return serve.ModelsDir is { Length: > 0 } root && File.Exists(Path.Combine(root.Trim(), name, "config.json"))
            ? Path.Combine(root.Trim(), name)
            : null;
    }

    /// <summary>coli's plan → one option — pure, so the tests read it from a canned plan.</summary>
    internal static FitOption Map(JsonElement plan, ModelFacts model, EngineFacts engine, int context, string directory)
    {
        var tiers = plan.GetProperty("tiers");
        long Bytes(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? n : 0;
        var ramTier = tiers.GetProperty("ram");
        var vramTier = tiers.TryGetProperty("vram", out var vt) ? vt : default;
        var disk = tiers.TryGetProperty("disk", out var dt) ? dt : default;
        var mi = 1L << 20;

        var denseOnCard = Bytes(ramTier, "dense_on_device_bytes");
        var hot = vramTier.ValueKind == JsonValueKind.Object ? Bytes(vramTier, "hot_expert_bytes") : 0;
        var budget = vramTier.ValueKind == JsonValueKind.Object ? Bytes(vramTier, "budget_bytes") : 0;
        // The plan counts the dense chain and the hot tier apart. On the card the tier grows until the device is full less
        // its 1 GiB reserve (measured: GLM-5.3-Flash, 22.1 GB of a 3090 Ti after four answers, where the plan's budget said 20.8).
        var free = vramTier.ValueKind == JsonValueKind.Object && vramTier.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array
            ? devices.EnumerateArray().Sum(d => Math.Max(0, Bytes(d, "free_bytes") - (1L << 30)))
            : 0;
        var cap = free > 0 ? free : budget;
        var onCard = (cap > 0 ? Math.Min(denseOnCard + hot, cap) : denseOnCard + hot) / mi;
        var kv = Bytes(ramTier, "sequence_state_bytes") / mi;
        var inRam = Bytes(ramTier, "budget_bytes") / mi;
        var cold = disk.ValueKind == JsonValueKind.Object ? Bytes(disk, "cold_expert_bytes") : 0;
        var modelBytes = plan.TryGetProperty("model", out var m) ? Bytes(m, "model_bytes") : 0;
        var hit = plan.TryGetProperty("projected_hit_rate", out var h) && h.TryGetDouble(out var rate) ? rate : (double?)null;
        var bottleneck = plan.TryGetProperty("expected_bottleneck", out var b) ? b.GetString() : null;

        var gpu = onCard > 0 && (engine.Accelerator is not null);
        var placement = cold > 0 ? FitPlacements.StreamFromDisk : FitPlacements.Ram;
        var speed = cold > 0 ? (gpu ? "slow" : "very slow") : (gpu ? "medium" : "slow");
        var why = new List<string>();

        if (!engine.Installs && engine.Absent is { } absent)
        {
            why.Add(absent);
        }

        why.Add($"already on this node: {directory} — nothing to download");
        why.Add($"colibri's own plan (coli plan) for {directory}: {Gib(modelBytes / mi)} on disk, {Gib(inRam)} of RAM for the resident part and the warm experts" +
                (cold > 0 ? $", {Gib(cold / mi)} of cold experts read from disk" : ", nothing left on disk"));

        if (denseOnCard > 0)
        {
            why.Add($"the dense chain on the card: {Gib(denseOnCard / mi)}");
        }

        if (hot > 0)
        {
            why.Add(engine.Accelerator is null
                ? $"its plan has a {Gib(hot / mi)} hot-expert tier for the card, and this node runs colibri on the CPU: set Colibri:Serve:Vulkan=true (or install the CUDA build) to use it"
                : $"hot experts on the card: up to {Gib(hot / mi)}");
        }

        if (bottleneck is not null)
        {
            why.Add($"expected bottleneck: {bottleneck}" + (hit is { } r ? $"; projected hit rate {r:P0}" : ""));
        }

        if (plan.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
        {
            foreach (var warning in warnings.EnumerateArray())
            {
                why.Add("colibri warns: " + (warning.ValueKind == JsonValueKind.String ? warning.GetString() : warning.ToString()));
            }
        }

        why.Add("the context goes to coli serve as --ctx");
        var variant = model.Variants.FirstOrDefault() ?? new ModelVariant("checkpoint", modelBytes);

        return new FitOption(
            FitEngines.Colibri, engine.Name, variant.Name, variant.Link, engine.Installs, true, placement,
            modelBytes / mi, kv, gpu ? onCard : 0, inRam, 0, context, null, null, speed, true, why,
            FitEstimator.ScoreOf(model, variant, placement, speed, true, engine.Installs));
    }

    private static string Gib(long mib) => FitEstimator.Gib(mib);
}
