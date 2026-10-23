using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Node.Resources.Limits;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Backends.Strata;

/// <summary>
/// Tells Strata's <c>server.py</c> its limits (phase 111, D5). Strata's engine takes its options from the
/// run config's <c>args</c>, not from its server's command line, so the node starts it on a copy of the
/// config with those options changed — <c>.inferhub-limits-&lt;file&gt;</c> beside the original, so a
/// relative path in it means what it meant. The original is never written; with nothing to tell, the
/// original is what starts.
/// </summary>
/// <remarks>
/// <para>
/// <b>VRAM → a larger <c>--vram-reserve-mib</c>.</b> <c>--expert-cache auto</c> fills each card down
/// to the reserve, so a reserve raised by (card − limit per card) leaves the engine the limit, whatever
/// else holds the card. The card's size comes from NVML; unread, nothing is told and the watchdog holds
/// it alone. The base is the config's own reserve, else 1536 MiB — the engine's default on a card that
/// drives a display, the larger of its two — because an explicit flag also stops the server adding its
/// 700 for a GPU picture reader. A fixed <c>--expert-cache N</c> is the operator's number and is left.
/// </para>
/// <para>
/// <b>Cores and CPU share → <c>"pool_cores"</c></b>, the logical processors 1 … N−1 of the N allowed
/// (0 is the host thread's, which waits for the card): without it the pool starts one worker per
/// physical core of the machine whatever the affinity says.
/// </para>
/// <para>
/// <b>Memory</b> is told only to a model with a RAM budget (<c>--resident-budget-gib</c>), lowered to
/// the limit less 8 GiB. Every other model reads its experts through the OS file cache, which is not
/// the process's memory; the Job Object holds the rest, as in 102.
/// </para>
/// <para>
/// <b>No GPU</b> is not told: Strata's CUDA engine stops three seconds after starting without a card,
/// so <see cref="StrataProcessLauncher"/> does not start one and says why.
/// </para>
/// </remarks>
public sealed class StrataLimitTeller(IGpuCards cards, Func<int>? logicalProcessors = null) : IEngineLimitTeller
{
    public const string CopyPrefix = ".inferhub-limits-";

    /// <summary>The engine's own reserve on a card that drives a display (Strata 0.2.1's notes) — the larger of its defaults.</summary>
    public const int DefaultReserveMiB = 1536;

    private const int ResidentHeadroomGiB = 8;

    private readonly Func<int> logical = logicalProcessors ?? (() => Environment.ProcessorCount);

    /// <summary>
    /// Strata 0.2's server gives its token an owner-only default DACL (<c>strata_control/private_files.adopt_user_defaults</c>)
    /// and then cannot start its image encoder or its engine inside a job with an affinity limit: ERROR_ACCESS_DENIED from
    /// <c>CreateProcess</c>. Found live, on this phase's first Strata run with a core limit.
    /// </summary>
    public bool CoresOnTheJob => false;

    public EngineLimits Told(EngineLimits effective) =>
        effective with { Gpus = effective.VramMiB is null ? null : effective.Gpus };

    public IReadOnlyList<string> Tell(EngineLimits told, ProcessStartInfo info)
    {
        var at = info.ArgumentList.IndexOf("--config");

        if (at < 0 || at + 1 >= info.ArgumentList.Count)
        {
            return [];
        }

        var path = info.ArgumentList[at + 1];

        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject config)
        {
            return [];
        }

        var args = config["args"] as JsonArray ?? [];
        // Strata 0.2's "user_args" are applied over "args" at every start, the person's value winning for the same flag
        // (strata_control/user_args.py): a told value that is only in "args" loses to one there — found live, a
        // reserve of 13264 lost to the 964 a user had set, and the watchdog stopped Strata at 14943 of 12000 MiB.
        var user = config["user_args"] as JsonArray;
        string? InForce(string flag) => (user is null ? null : Value(user, flag)) ?? Value(args, flag);
        void Tell(string flag, string value)
        {
            Set(args, flag, value);

            if (user is not null && Value(user, flag) is not null)
            {
                Set(user, flag, value);
            }
        }

        var lines = new List<string>();
        var changed = false;

        if (EngineLimitTelling.Threads(told, logical()) is { } threads)
        {
            var pool = threads == 1 ? "0" : string.Join(',', Enumerable.Range(1, threads - 1));
            config["pool_cores"] = pool;
            lines.Add($"pool_cores {(threads == 1 ? "0" : threads == 2 ? "1" : $"1-{threads - 1}")}");
            changed = true;
        }

        if (told.VramMiB is { } vram)
        {
            var visible = Cards(config["gpu"]);
            var physical = visible.Select(i => told.Gpus is { Count: > 0 } list && i < list.Count ? list[i] : i).ToArray();
            var totals = physical.Select(cards.TotalMiB).ToArray();

            if (totals.Any(t => t is null))
            {
                lines.Add($"vram: the size of card {string.Join(',', physical)} could not be read; held by the watchdog only");
            }
            else
            {
                var current = InForce("--vram-reserve-mib") is { } text && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : (int?)null;
                var basis = current ?? DefaultReserveMiB;
                var perCard = vram / physical.Length;
                var reserve = basis + (int)Math.Max(0, totals.Max(t => t!.Value) - perCard);
                Tell("--vram-reserve-mib", reserve.ToString(CultureInfo.InvariantCulture));
                lines.Add($"--vram-reserve-mib {reserve} (card {totals.Max()} MiB, {perCard} MiB allowed)");
                changed = true;

                if (InForce("--expert-cache") is { } cache && cache != "auto")
                {
                    lines.Add($"--expert-cache {cache} is the config's own number and was left");
                }
            }
        }

        if (told.MemoryMiB is { } memory
            && InForce("--resident-budget-gib") is { } budgetText
            && double.TryParse(budgetText, NumberStyles.Float, CultureInfo.InvariantCulture, out var budget))
        {
            var allowed = Math.Max(1, memory / 1024 - ResidentHeadroomGiB);

            if (budget > allowed)
            {
                Tell("--resident-budget-gib", allowed.ToString(CultureInfo.InvariantCulture));
                lines.Add($"--resident-budget-gib {allowed} (was {budgetText})");
                changed = true;
            }
        }

        if (!changed)
        {
            return lines;
        }

        config["args"] = args;
        var copy = Path.Combine(Path.GetDirectoryName(path) ?? ".", CopyPrefix + Path.GetFileName(path));
        File.WriteAllText(copy, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        info.ArgumentList[at + 1] = copy;
        lines.Add($"config {Path.GetFileName(copy)}");
        return lines;
    }

    /// <summary>The config's <c>"gpu"</c>: a number, a list, or "0,2"; absent is card 0 — the visible numbering.</summary>
    internal static int[] Cards(JsonNode? gpu)
    {
        var cards = gpu switch
        {
            JsonValue value when value.TryGetValue<int>(out var one) => [one],
            JsonValue value when value.TryGetValue<string>(out var text) => text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1)
                .Where(n => n >= 0)
                .ToArray(),
            JsonArray list => list
                .Select(n => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : -1)
                .Where(n => n >= 0)
                .ToArray(),
            _ => Array.Empty<int>()
        };

        return cards.Length == 0 ? [0] : cards;
    }

    private static string? Value(JsonArray args, string flag)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (Str(args[i]) == flag)
            {
                return Str(args[i + 1]);
            }
        }

        return null;
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text
        : node is JsonValue number ? number.ToJsonString()
        : null;

    private static void Set(JsonArray args, string flag, string value)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (Str(args[i]) == flag)
            {
                args[i + 1] = value;
                return;
            }
        }

        args.Add(flag);
        args.Add(value);
    }
}
