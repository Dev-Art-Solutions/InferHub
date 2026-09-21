using System.Globalization;

namespace InferHub.Coordinator.Observability;

/// <summary>One sample line of the Prometheus text exposition format, parsed back out.</summary>
public sealed record ExpositionSample(string Name, IReadOnlyDictionary<string, string> Labels, double Value);

/// <summary>
/// Reads <see cref="PrometheusFormatter"/>'s own output back into rows (phase 81, D2). This is the
/// single source of truth the OTLP exporter reads from, rather than a second walk over every
/// registry <c>PrometheusFormatter</c> already knows how to read — one string serialized twice
/// instead of one set of facts computed twice. A production cousin of
/// <c>PrometheusMetricsTests.Exposition</c>, which parses this exact grammar for the test suite;
/// this one throws instead of asserting, because its input is always this coordinator's own
/// formatter, never operator-supplied text.
/// </summary>
public static class ExpositionReader
{
    public static ExpositionDocument Parse(string text)
    {
        var samples = new List<ExpositionSample>();
        var types = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line.StartsWith("# HELP ", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("# TYPE ", StringComparison.Ordinal))
            {
                var parts = line["# TYPE ".Length..].Split(' ');
                if (parts.Length != 2)
                {
                    throw new FormatException($"malformed TYPE line: {line}");
                }

                types[parts[0]] = parts[1];
                continue;
            }

            if (line.StartsWith('#'))
            {
                throw new FormatException($"unrecognised comment line: {line}");
            }

            var valueSeparator = line.LastIndexOf(' ');
            if (valueSeparator <= 0)
            {
                throw new FormatException($"sample line has no value: {line}");
            }

            var series = line[..valueSeparator];
            var value = double.Parse(line[(valueSeparator + 1)..], CultureInfo.InvariantCulture);

            var brace = series.IndexOf('{');
            var name = brace < 0 ? series : series[..brace];
            var labels = brace < 0
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : ParseLabels(series[(brace + 1)..^1]);

            samples.Add(new ExpositionSample(name, labels, value));
        }

        return new ExpositionDocument(samples, types);
    }

    private static Dictionary<string, string> ParseLabels(string body)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (body.Length == 0) return labels;

        var i = 0;
        while (i < body.Length)
        {
            var eq = body.IndexOf('=', i);
            if (eq < 0)
            {
                throw new FormatException($"malformed label list: {body}");
            }

            var key = body[i..eq];
            if (eq + 1 >= body.Length || body[eq + 1] != '"')
            {
                throw new FormatException($"malformed label value for {key}: {body}");
            }

            var valueBuilder = new System.Text.StringBuilder();
            var j = eq + 2;
            while (j < body.Length && body[j] != '"')
            {
                if (body[j] == '\\' && j + 1 < body.Length)
                {
                    j++;
                    valueBuilder.Append(body[j] switch
                    {
                        'n' => '\n',
                        '"' => '"',
                        '\\' => '\\',
                        var other => other,
                    });
                }
                else
                {
                    valueBuilder.Append(body[j]);
                }

                j++;
            }

            labels[key] = valueBuilder.ToString();

            // Skip the closing quote, then a comma if there is one.
            i = j + 1;
            if (i < body.Length && body[i] == ',') i++;
        }

        return labels;
    }
}

/// <summary>Everything <see cref="ExpositionReader"/> recovered from one scrape body.</summary>
public sealed record ExpositionDocument(
    IReadOnlyList<ExpositionSample> Samples,
    IReadOnlyDictionary<string, string> Types)
{
    /// <summary>Prometheus type (<c>counter</c>, <c>gauge</c>, <c>histogram</c>) for a metric family, or null if unknown.</summary>
    public string? TypeOf(string family) => Types.TryGetValue(family, out var type) ? type : null;
}
