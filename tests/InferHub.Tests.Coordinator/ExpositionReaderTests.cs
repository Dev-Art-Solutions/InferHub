using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;

namespace InferHub.Tests;

/// <summary>
/// Phase 81, D2: <see cref="ExpositionReader"/> is production code's own reader of
/// <see cref="PrometheusFormatter"/>'s output, so the OTLP exporter reuses one source of truth
/// instead of re-deriving every metric family a second time. Round-tripped here against a scrape
/// carrying counters, gauges and a histogram, the three shapes the formatter ever emits.
/// </summary>
public class ExpositionReaderTests
{
    [Fact]
    public void EverySampleThePrometheusFormatterWroteComesBackOut()
    {
        var metrics = new Metrics();
        metrics.RecordRequestStart("gpu-1");
        metrics.RecordRequestComplete("gpu-1");
        metrics.RecordRequestFail("gpu-1");
        metrics.RecordVectorQuery("docs", TimeSpan.FromMilliseconds(12));
        metrics.RecordImageJob("sdxl", "succeeded", 8);

        var scrape = new PrometheusScrape(
            "3.46.0",
            metrics.Snapshot(DateTimeOffset.UtcNow),
            [],
            [],
            new QueueSnapshot(1, 4, 3, 1, 0, MedianWaitMs: 250),
            [],
            AffinityEntries: 2);

        var text = PrometheusFormatter.Format(scrape);
        var document = ExpositionReader.Parse(text);

        Assert.NotEmpty(document.Samples);
        Assert.Equal(1, document.Samples.Count(s => s.Name == "inferhub_requests_total"));
        Assert.Equal(1, document.Samples.Single(s => s.Name == "inferhub_requests_total").Value);

        Assert.Equal(1, document.Samples
            .Single(s => s.Name == "inferhub_node_requests_total" && s.Labels["node"] == "gpu-1")
            .Value);

        Assert.Equal("counter", document.TypeOf("inferhub_requests_total"));
        Assert.Equal("gauge", document.TypeOf("inferhub_queue_depth"));
        Assert.Equal("histogram", document.TypeOf("inferhub_image_job_seconds"));

        // The histogram's own samples carry a suffix the TYPE line does not (phase 51 D2) — the
        // reader still returns them as plain samples, named exactly as the formatter wrote them.
        Assert.Contains(document.Samples, s => s.Name == "inferhub_image_job_seconds_sum");
        Assert.Contains(document.Samples, s => s.Name == "inferhub_image_job_seconds_bucket");
    }

    [Fact]
    public void LabelsWithEscapedCharactersRoundTrip()
    {
        var metrics = new Metrics();
        metrics.RecordRequestStart("node\"with\\quotes");

        var scrape = new PrometheusScrape(
            "3.46.0",
            metrics.Snapshot(DateTimeOffset.UtcNow),
            [],
            [],
            new QueueSnapshot(0, 0, 0, 0, 0, null),
            [],
            AffinityEntries: 0);

        var document = ExpositionReader.Parse(PrometheusFormatter.Format(scrape));

        var sample = document.Samples.Single(s => s.Name == "inferhub_node_requests_total");
        Assert.Equal("node\"with\\quotes", sample.Labels["node"]);
    }

    [Fact]
    public void MalformedTypeLineThrows()
    {
        Assert.Throws<FormatException>(() => ExpositionReader.Parse("# TYPE only_one_token\n"));
    }

    [Fact]
    public void UnrecognisedCommentLineThrows()
    {
        Assert.Throws<FormatException>(() => ExpositionReader.Parse("# something else\n"));
    }
}
