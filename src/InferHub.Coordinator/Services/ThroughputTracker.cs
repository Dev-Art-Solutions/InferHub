using System.Collections.Concurrent;
using System.Text.Json;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Measured throughput per (node, model), as an exponentially-weighted moving average of
/// tokens/second (phase 26). It is fed from the <c>eval_count</c> and <c>eval_duration</c> that
/// every completed Ollama-shaped response already carries — no new measurement plumbing, and no
/// message content is read.
///
/// <para>A node with no measurement is reported as <c>null</c>, and the router treats that as
/// <em>average</em>, never as slow: a pessimistic default would starve a fresh node of the very
/// requests it needs to earn a measurement, which is a load balancer that has quietly stopped
/// balancing (D4).</para>
/// </summary>
public sealed class ThroughputTracker
{
    // Smoothing factor: weight of the newest sample. 0.3 reacts within a few requests without
    // letting one slow cold-start dominate.
    private const double Alpha = 0.3;

    private readonly ConcurrentDictionary<(string Node, string Model), double> ewma = new();

    /// <summary>
    /// Parse a completed response's token count and duration and fold the resulting tokens/second
    /// into the EWMA for (node, model). Never throws — a malformed body simply records nothing.
    /// </summary>
    public void RecordFromResponse(string nodeId, string? responseJson)
    {
        if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(responseJson))
        {
            return;
        }

        if (!TryParse(responseJson, out var model, out var evalCount, out var evalDurationNs))
        {
            return;
        }

        // Need real work and real time to have a rate; a load/empty response has neither.
        if (evalCount <= 0 || evalDurationNs <= 0)
        {
            return;
        }

        var tokensPerSecond = evalCount / (evalDurationNs / 1_000_000_000.0);
        if (double.IsNaN(tokensPerSecond) || double.IsInfinity(tokensPerSecond) || tokensPerSecond <= 0)
        {
            return;
        }

        ewma.AddOrUpdate(
            (nodeId, model),
            tokensPerSecond,
            (_, previous) => (Alpha * tokensPerSecond) + ((1 - Alpha) * previous));
    }

    /// <summary>Measured tokens/second for (node, model), or <c>null</c> if unmeasured.</summary>
    public double? GetTokensPerSecond(string nodeId, string model) =>
        ewma.TryGetValue((nodeId, model), out var value) ? value : null;

    /// <summary>
    /// Mean measured tokens/second across the nodes that <em>do</em> have a measurement for this
    /// model — the value an unmeasured node is treated as, so it is neither favoured nor starved.
    /// <c>null</c> when nothing has been measured for the model at all.
    /// </summary>
    public double? AverageForModel(string model)
    {
        double sum = 0;
        var count = 0;
        foreach (var pair in ewma)
        {
            if (string.Equals(pair.Key.Model, model, StringComparison.OrdinalIgnoreCase))
            {
                sum += pair.Value;
                count++;
            }
        }

        return count == 0 ? null : sum / count;
    }

    /// <summary>The node's mean measured tokens/second across every model it has served — for the status page.</summary>
    public double? NodeAverage(string nodeId)
    {
        double sum = 0;
        var count = 0;
        foreach (var pair in ewma)
        {
            if (string.Equals(pair.Key.Node, nodeId, StringComparison.Ordinal))
            {
                sum += pair.Value;
                count++;
            }
        }

        return count == 0 ? null : sum / count;
    }

    private static bool TryParse(string responseJson, out string model, out long evalCount, out double evalDurationNs)
    {
        model = string.Empty;
        evalCount = 0;
        evalDurationNs = 0;

        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("model", out var m) && m.ValueKind is JsonValueKind.String)
            {
                model = m.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("eval_count", out var c) && c.ValueKind is JsonValueKind.Number)
            {
                evalCount = c.GetInt64();
            }

            if (root.TryGetProperty("eval_duration", out var d) && d.ValueKind is JsonValueKind.Number)
            {
                evalDurationNs = d.GetDouble();
            }

            return !string.IsNullOrEmpty(model);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
