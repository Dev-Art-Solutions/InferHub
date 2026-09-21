using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Coordinator.Observability;

/// <summary>
/// Turns an <see cref="ExpositionDocument"/> — <see cref="PrometheusFormatter"/>'s own output, parsed
/// back out by <see cref="ExpositionReader"/> — into an OTLP/HTTP metrics payload (phase 81, D1/D2).
/// A pure function: no clock reads beyond the two timestamps it is handed, no I/O.
/// </summary>
public static class OtlpFormatter
{
    private const long CumulativeTemporality = 2; // AGGREGATION_TEMPORALITY_CUMULATIVE

    public static OtlpBuildResult Build(
        ExpositionDocument document,
        string coordinatorVersion,
        DateTimeOffset startedAtUtc,
        DateTimeOffset now)
    {
        var startNanos = ToUnixNanos(startedAtUtc);
        var nowNanos = ToUnixNanos(now);

        var skipped = new SortedSet<string>(StringComparer.Ordinal);
        var byFamily = new Dictionary<string, List<ExpositionSample>>(StringComparer.Ordinal);
        var typeOfFamily = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var sample in document.Samples)
        {
            if (document.TypeOf(sample.Name) is { } directType)
            {
                Add(sample, sample.Name, directType);
                continue;
            }

            // Histogram families' own samples carry a suffix the TYPE line does not (phase 51 D2's
            // _bucket/_sum/_count). D3: recorded as skipped rather than silently dropped.
            var baseName = StripHistogramSuffix(sample.Name);
            if (baseName is not null && document.TypeOf(baseName) == "histogram")
            {
                skipped.Add(baseName);
                continue;
            }

            throw new InvalidOperationException(
                $"'{sample.Name}' has no TYPE line and is not a recognised histogram suffix — " +
                "PrometheusFormatter emitted a sample OtlpFormatter does not know how to classify.");
        }

        var metrics = new List<OtlpMetric>();

        foreach (var (family, samples) in byFamily.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var type = typeOfFamily[family];
            var dataPoints = samples
                .Select(sample => new OtlpNumberDataPoint(
                    Attributes: sample.Labels
                        .OrderBy(label => label.Key, StringComparer.Ordinal)
                        .Select(label => OtlpAttribute.OfString(label.Key, label.Value))
                        .ToArray(),
                    StartTimeUnixNano: type == "counter" ? startNanos : null,
                    TimeUnixNano: nowNanos,
                    AsDouble: sample.Value))
                .ToArray();

            metrics.Add(type == "counter"
                ? new OtlpMetric(family, Sum: new OtlpSum(dataPoints, CumulativeTemporality, IsMonotonic: true))
                : new OtlpMetric(family, Gauge: new OtlpGauge(dataPoints)));
        }

        var payload = new OtlpMetricsPayload([
            new OtlpResourceMetrics(
                Resource: new OtlpResource([
                    OtlpAttribute.OfString("service.name", "inferhub-coordinator"),
                    OtlpAttribute.OfString("service.version", coordinatorVersion),
                ]),
                ScopeMetrics: [new OtlpScopeMetrics(new OtlpScope("inferhub"), metrics)])
        ]);

        return new OtlpBuildResult(payload, skipped.ToArray());

        void Add(ExpositionSample sample, string family, string type)
        {
            if (type is not ("counter" or "gauge"))
            {
                // Nothing in PrometheusFormatter emits a third TYPE today; a future one would land
                // here rather than be silently mis-shaped.
                throw new InvalidOperationException($"'{family}' has TYPE '{type}', which OtlpFormatter does not map.");
            }

            typeOfFamily[family] = type;
            (byFamily.TryGetValue(family, out var list) ? list : byFamily[family] = []).Add(sample);
        }
    }

    private static string? StripHistogramSuffix(string name)
    {
        foreach (var suffix in (string[])["_bucket", "_sum", "_count"])
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return name[..^suffix.Length];
            }
        }

        return null;
    }

    private static string ToUnixNanos(DateTimeOffset at) =>
        (at.ToUnixTimeMilliseconds() * 1_000_000L).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ToJson(OtlpMetricsPayload payload) => JsonSerializer.Serialize(payload, SerializerOptions);
}

/// <summary>What <see cref="OtlpFormatter.Build"/> produced, and what it deliberately left out (D3).</summary>
public sealed record OtlpBuildResult(OtlpMetricsPayload Payload, IReadOnlyList<string> SkippedFamilies);

public sealed record OtlpMetricsPayload(IReadOnlyList<OtlpResourceMetrics> ResourceMetrics);

public sealed record OtlpResourceMetrics(OtlpResource Resource, IReadOnlyList<OtlpScopeMetrics> ScopeMetrics);

public sealed record OtlpResource(IReadOnlyList<OtlpAttribute> Attributes);

public sealed record OtlpScopeMetrics(OtlpScope Scope, IReadOnlyList<OtlpMetric> Metrics);

public sealed record OtlpScope(string Name);

public sealed record OtlpMetric(string Name, OtlpSum? Sum = null, OtlpGauge? Gauge = null);

public sealed record OtlpSum(
    IReadOnlyList<OtlpNumberDataPoint> DataPoints,
    long AggregationTemporality,
    bool IsMonotonic);

public sealed record OtlpGauge(IReadOnlyList<OtlpNumberDataPoint> DataPoints);

public sealed record OtlpNumberDataPoint(
    IReadOnlyList<OtlpAttribute> Attributes,
    string? StartTimeUnixNano,
    string TimeUnixNano,
    double AsDouble);

public sealed record OtlpAttribute(string Key, OtlpAnyValue Value)
{
    public static OtlpAttribute OfString(string key, string value) => new(key, new OtlpAnyValue(value));
}

public sealed record OtlpAnyValue(string StringValue);
