using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;

namespace InferHub.Tests;

/// <summary>
/// Phase 81. <see cref="OtlpFormatter"/> is a pure function over what
/// <see cref="ExpositionReader"/> recovers from <see cref="PrometheusFormatter"/>'s own output —
/// these tests exercise the three shapes that matter: a counter becomes a monotonic cumulative
/// Sum, a gauge becomes a plain Gauge, and a histogram family is skipped rather than mis-shaped
/// (D3).
/// </summary>
public class OtlpFormatterTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = StartedAt.AddMinutes(5);

    private static ExpositionDocument DocumentFor(PrometheusScrape scrape) =>
        ExpositionReader.Parse(PrometheusFormatter.Format(scrape));

    [Fact]
    public void CounterBecomesAMonotonicCumulativeSumWithTheCoordinatorsStartTime()
    {
        var metrics = new Metrics();
        metrics.RecordRequestStart("gpu-1");

        var scrape = new PrometheusScrape(
            "3.46.0", metrics.Snapshot(Now), [], [], new QueueSnapshot(0, 0, 0, 0, 0, null), [], AffinityEntries: 0);

        var result = OtlpFormatter.Build(DocumentFor(scrape), "3.46.0", StartedAt, Now);

        var metric = result.Payload.ResourceMetrics[0].ScopeMetrics[0].Metrics
            .Single(m => m.Name == "inferhub_requests_total");

        Assert.NotNull(metric.Sum);
        Assert.Null(metric.Gauge);
        Assert.True(metric.Sum!.IsMonotonic);
        Assert.Equal(2, metric.Sum.AggregationTemporality);

        var point = metric.Sum.DataPoints.Single();
        Assert.Equal(1, point.AsDouble);
        Assert.Equal((StartedAt.ToUnixTimeMilliseconds() * 1_000_000L).ToString(), point.StartTimeUnixNano);
        Assert.Equal((Now.ToUnixTimeMilliseconds() * 1_000_000L).ToString(), point.TimeUnixNano);
    }

    [Fact]
    public void GaugeBecomesAGaugeWithNoStartTime()
    {
        var scrape = new PrometheusScrape(
            "3.46.0", new Metrics().Snapshot(Now), [], [], new QueueSnapshot(3, 0, 0, 0, 0, null), [], AffinityEntries: 0);

        var result = OtlpFormatter.Build(DocumentFor(scrape), "3.46.0", StartedAt, Now);

        var metric = result.Payload.ResourceMetrics[0].ScopeMetrics[0].Metrics
            .Single(m => m.Name == "inferhub_queue_depth");

        Assert.Null(metric.Sum);
        Assert.NotNull(metric.Gauge);
        var point = metric.Gauge!.DataPoints.Single();
        Assert.Equal(3, point.AsDouble);
        Assert.Null(point.StartTimeUnixNano);
    }

    [Fact]
    public void LabelsBecomeAttributes()
    {
        var metrics = new Metrics();
        metrics.RecordRequestStart("gpu-1");

        var scrape = new PrometheusScrape(
            "3.46.0", metrics.Snapshot(Now), [], [], new QueueSnapshot(0, 0, 0, 0, 0, null), [], AffinityEntries: 0);

        var result = OtlpFormatter.Build(DocumentFor(scrape), "3.46.0", StartedAt, Now);

        var metric = result.Payload.ResourceMetrics[0].ScopeMetrics[0].Metrics
            .Single(m => m.Name == "inferhub_node_requests_total");
        var point = metric.Sum!.DataPoints.Single();

        Assert.Contains(point.Attributes, a => a.Key == "node" && a.Value.StringValue == "gpu-1");
    }

    [Fact]
    public void HistogramFamilyIsSkippedNotMisshaped()
    {
        var metrics = new Metrics();
        metrics.RecordImageJob("sdxl", "succeeded", 8);

        var scrape = new PrometheusScrape(
            "3.46.0", metrics.Snapshot(Now), [], [], new QueueSnapshot(0, 0, 0, 0, 0, null), [], AffinityEntries: 0);

        var result = OtlpFormatter.Build(DocumentFor(scrape), "3.46.0", StartedAt, Now);

        Assert.Contains("inferhub_image_job_seconds", result.SkippedFamilies);
        Assert.DoesNotContain(
            result.Payload.ResourceMetrics[0].ScopeMetrics[0].Metrics,
            m => m.Name.StartsWith("inferhub_image_job_seconds", StringComparison.Ordinal));
    }

    [Fact]
    public void ResourceCarriesServiceNameAndVersion()
    {
        var scrape = new PrometheusScrape(
            "3.46.0", new Metrics().Snapshot(Now), [], [], new QueueSnapshot(0, 0, 0, 0, 0, null), [], AffinityEntries: 0);

        var result = OtlpFormatter.Build(DocumentFor(scrape), "3.46.0", StartedAt, Now);
        var attributes = result.Payload.ResourceMetrics[0].Resource.Attributes;

        Assert.Contains(attributes, a => a.Key == "service.name" && a.Value.StringValue == "inferhub-coordinator");
        Assert.Contains(attributes, a => a.Key == "service.version" && a.Value.StringValue == "3.46.0");
    }

    [Fact]
    public void ToJsonProducesValidJsonWithCamelCaseFields()
    {
        var scrape = new PrometheusScrape(
            "3.46.0", new Metrics().Snapshot(Now), [], [], new QueueSnapshot(0, 0, 0, 0, 0, null), [], AffinityEntries: 0);

        var result = OtlpFormatter.Build(DocumentFor(scrape), "3.46.0", StartedAt, Now);
        var json = OtlpFormatter.ToJson(result.Payload);

        Assert.Contains("\"resourceMetrics\"", json);
        Assert.Contains("\"scopeMetrics\"", json);
        Assert.Contains("\"aggregationTemporality\"", json);
        Assert.Contains("\"asDouble\"", json);

        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(parsed.RootElement.TryGetProperty("resourceMetrics", out _));
    }
}
