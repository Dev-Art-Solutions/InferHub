using System.Net;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 28. The output is parsed back with the minimal exposition parser at the bottom of this
/// file rather than string-matched: asserting on substrings would pass happily on output no
/// Prometheus could read, which is the exact failure this endpoint exists to avoid.
/// </summary>
public class PrometheusMetricsTests
{
    [Fact]
    public void ScrapeEmitsHelpAndTypeForEverySeries()
    {
        var text = PrometheusFormatter.Format(SampleScrape());
        var parsed = Exposition.Parse(text);

        Assert.NotEmpty(parsed.Samples);

        foreach (var name in parsed.Samples.Select(s => s.Name).Distinct())
        {
            Assert.True(parsed.Help.ContainsKey(name), $"{name} has no # HELP line");
            Assert.True(parsed.Types.ContainsKey(name), $"{name} has no # TYPE line");
        }
    }

    [Fact]
    public void CountersAreCountersAndGaugesAreGauges()
    {
        var parsed = Exposition.Parse(PrometheusFormatter.Format(SampleScrape()));

        Assert.Equal("counter", parsed.Types["inferhub_requests_total"]);
        Assert.Equal("counter", parsed.Types["inferhub_node_requests_completed_total"]);
        Assert.Equal("counter", parsed.Types["inferhub_collection_queries_total"]);
        Assert.Equal("gauge", parsed.Types["inferhub_requests_in_flight"]);
        Assert.Equal("gauge", parsed.Types["inferhub_queue_depth"]);
        Assert.Equal("gauge", parsed.Types["inferhub_node_tokens_per_second"]);
    }

    [Fact]
    public void FleetCountersCarryTheValuesMetricsRecorded()
    {
        var parsed = Exposition.Parse(PrometheusFormatter.Format(SampleScrape()));

        Assert.Equal(3, parsed.Value("inferhub_requests_total"));
        Assert.Equal(2, parsed.Value("inferhub_requests_completed_total"));
        Assert.Equal(1, parsed.Value("inferhub_requests_failed_total"));
        Assert.Equal(2, parsed.Value("inferhub_node_requests_total", ("node", "gpu-1")));
        Assert.Equal(1, parsed.Value("inferhub_node_requests_total", ("node", "gpu-2")));
    }

    [Fact]
    public void PerNodeAndPerCollectionSeriesAreLabelledNotNameMangled()
    {
        var parsed = Exposition.Parse(PrometheusFormatter.Format(SampleScrape()));

        // One series name, one label per dimension — a node id baked into the metric name would
        // make the cardinality unqueryable and every dashboard node-specific.
        var nodes = parsed.Samples
            .Where(s => s.Name == "inferhub_node_requests_total")
            .Select(s => s.Labels["node"])
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["gpu-1", "gpu-2"], nodes);

        Assert.Equal(6, parsed.Value("inferhub_collection_queries_total", ("collection", "docs")));
        Assert.Equal(4, parsed.Value("inferhub_collection_chunks_embedded_total", ("collection", "docs")));

        var throughput = parsed.Samples.Single(s => s.Name == "inferhub_node_tokens_per_second");
        Assert.Equal("gpu-1", throughput.Labels["node"]);
        Assert.Equal("llama3", throughput.Labels["model"]);
    }

    [Fact]
    public void ClientWindowsComeFromAdmissionAndLimitsAreOmittedWhenUnlimited()
    {
        var parsed = Exposition.Parse(PrometheusFormatter.Format(SampleScrape()));

        Assert.Equal(120, parsed.Value("inferhub_client_tokens_today", ("client", "acme")));
        Assert.Equal(1000, parsed.Value("inferhub_client_limit_tokens_per_day", ("client", "acme")));

        // 'unlimited' is the absence of a series, not a zero and not a sentinel.
        Assert.DoesNotContain(
            parsed.Samples,
            s => s.Name == "inferhub_client_limit_max_concurrent" && s.Labels["client"] == "acme");
    }

    [Fact]
    public void UnmeasuredAndNeverUsedSeriesAreAbsentRatherThanZero()
    {
        var empty = new PrometheusScrape(
            "2.10.0",
            new Metrics().Snapshot(DateTimeOffset.UtcNow),
            [],
            [],
            new QueueSnapshot(0, 0, 0, 0, 0, MedianWaitMs: null),
            [],
            AffinityEntries: 0);

        var parsed = Exposition.Parse(PrometheusFormatter.Format(empty));

        Assert.DoesNotContain(parsed.Samples, s => s.Name == "inferhub_node_tokens_per_second");
        Assert.DoesNotContain(parsed.Samples, s => s.Name == "inferhub_queue_wait_median_ms");
        Assert.DoesNotContain(parsed.Samples, s => s.Name == "inferhub_fallback_last_model");

        // The fleet counters still exist at zero — a zero there is a statement, not an absence.
        Assert.Equal(0, parsed.Value("inferhub_requests_total"));
        Assert.Equal(0, parsed.Value("inferhub_queue_depth"));
        // Affinity entries is a fleet gauge: present at zero, like the queue depth.
        Assert.Equal(0, parsed.Value("inferhub_affinity_entries"));
    }

    [Fact]
    public void LabelValuesAreEscaped()
    {
        var metrics = new Metrics();
        metrics.RecordRequestStart("node\"with\\quotes");

        var scrape = new PrometheusScrape(
            "2.10.0",
            metrics.Snapshot(DateTimeOffset.UtcNow),
            [],
            [],
            new QueueSnapshot(0, 0, 0, 0, 0, null),
            [],
            AffinityEntries: 0);

        var text = PrometheusFormatter.Format(scrape);

        Assert.Contains("node=\"node\\\"with\\\\quotes\"", text);
        Assert.Equal(1, Exposition.Parse(text).Value("inferhub_node_requests_total", ("node", "node\"with\\quotes")));
    }

    [Fact]
    public void ValuesUseInvariantDecimalSeparator()
    {
        var text = PrometheusFormatter.Format(SampleScrape());

        // A decimal comma would be a locale bug that only appears on a Bulgarian or German host,
        // and Prometheus rejects the whole scrape over it. Every value is checked, not a sample.
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var value = line[(line.LastIndexOf(' ') + 1)..];
            Assert.DoesNotContain(",", value);
        }

        Assert.Contains("inferhub_node_tokens_per_second{node=\"gpu-1\",model=\"llama3\"} 42.5", text);
    }

    [Fact]
    public async Task MetricsRequiresAnAdminKeyByDefault()
    {
        var middleware = NewMiddleware(out var nextCalled, adminKeys: ["admin-secret"], openScrape: false);
        var context = NewContext("/metrics", IPAddress.Parse("8.8.8.8"));

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task MetricsAcceptsTheAdminKey()
    {
        var middleware = NewMiddleware(out var nextCalled, adminKeys: ["admin-secret"], openScrape: false);
        var context = NewContext("/metrics", IPAddress.Parse("8.8.8.8"), "Bearer admin-secret");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task MetricsIsOpenWhenOpenScrapeIsSet()
    {
        var middleware = NewMiddleware(out var nextCalled, adminKeys: ["admin-secret"], openScrape: true);
        var context = NewContext("/metrics", IPAddress.Parse("8.8.8.8"));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task OpenScrapeDoesNotOpenTheAdminRoutes()
    {
        // Dropping the scrape guard is an operational choice about one endpoint. If it ever
        // unlocked /api/admin too, that would be a config flag that quietly grants cordon,
        // eviction and model-pull to anyone who can reach the port.
        var middleware = NewMiddleware(out var nextCalled, adminKeys: ["admin-secret"], openScrape: true);
        var context = NewContext("/api/admin/nodes", IPAddress.Parse("8.8.8.8"));

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    private static PrometheusScrape SampleScrape()
    {
        var metrics = new Metrics();

        metrics.RecordRequestStart("gpu-1");
        metrics.RecordRequestComplete("gpu-1");
        metrics.RecordRequestStart("gpu-1");
        metrics.RecordRequestComplete("gpu-1");
        metrics.RecordRequestStart("gpu-2");
        metrics.RecordRequestFail("gpu-2");
        metrics.RecordFallbackDispatched("gpt-4o-mini");
        metrics.RecordVectorQuery("docs", TimeSpan.FromMilliseconds(12));
        for (var i = 0; i < 5; i++) metrics.RecordVectorQuery("docs", TimeSpan.FromMilliseconds(12));
        metrics.RecordChunksEmbedded("docs", 4);

        return new PrometheusScrape(
            "2.10.0",
            metrics.Snapshot(DateTimeOffset.UtcNow),
            [],
            [new ThroughputSample("gpu-1", "llama3", 42.5)],
            new QueueSnapshot(1, 4, 3, 1, 0, MedianWaitMs: 250),
            [new ClientScrapeSample("acme", 1, 7, 40, 120, null, null, null, TokensPerDay: 1000)],
            AffinityEntries: 3);
    }

    private static AdminApiKeyMiddleware NewMiddleware(
        out Func<bool> nextCalled,
        IReadOnlyList<string> adminKeys,
        bool openScrape)
    {
        var called = false;
        nextCalled = () => called;

        RequestDelegate next = _ =>
        {
            called = true;
            return Task.CompletedTask;
        };

        return new AdminApiKeyMiddleware(
            next,
            TestOptions.Monitor(new ApiKeyOptions { AdminApiKeys = adminKeys.ToList() }),
            TestOptions.Monitor(new MetricsOptions { OpenScrape = openScrape }),
            NullLogger<AdminApiKeyMiddleware>.Instance);
    }

    private static HttpContext NewContext(string path, IPAddress remoteIp, string? authorization = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = remoteIp;

        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context;
    }

    /// <summary>
    /// A minimal reader for the text exposition format — enough to prove the output is parseable
    /// and correctly labelled, not a Prometheus reimplementation. If this parser cannot read a
    /// line, neither can a scraper.
    /// </summary>
    private sealed record ExpositionSample(string Name, IReadOnlyDictionary<string, string> Labels, double Value);

    private sealed class Exposition
    {
        public required IReadOnlyList<ExpositionSample> Samples { get; init; }
        public required IReadOnlyDictionary<string, string> Help { get; init; }
        public required IReadOnlyDictionary<string, string> Types { get; init; }

        public double Value(string name, params (string Key, string Value)[] labels)
        {
            var match = Samples.Single(sample =>
                sample.Name == name
                && sample.Labels.Count == labels.Length
                && labels.All(label => sample.Labels.TryGetValue(label.Key, out var v) && v == label.Value));

            return match.Value;
        }

        public static Exposition Parse(string text)
        {
            var samples = new List<ExpositionSample>();
            var help = new Dictionary<string, string>(StringComparer.Ordinal);
            var types = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;

                if (line.StartsWith("# HELP ", StringComparison.Ordinal))
                {
                    var rest = line["# HELP ".Length..];
                    var space = rest.IndexOf(' ');
                    Assert.True(space > 0, $"malformed HELP line: {line}");
                    help[rest[..space]] = rest[(space + 1)..];
                    continue;
                }

                if (line.StartsWith("# TYPE ", StringComparison.Ordinal))
                {
                    var parts = line["# TYPE ".Length..].Split(' ');
                    Assert.Equal(2, parts.Length);
                    types[parts[0]] = parts[1];
                    continue;
                }

                Assert.False(line.StartsWith('#'), $"unrecognised comment line: {line}");

                var valueSeparator = line.LastIndexOf(' ');
                Assert.True(valueSeparator > 0, $"sample line has no value: {line}");

                var series = line[..valueSeparator];
                var value = double.Parse(line[(valueSeparator + 1)..], System.Globalization.CultureInfo.InvariantCulture);

                var brace = series.IndexOf('{');
                var name = brace < 0 ? series : series[..brace];
                var labels = brace < 0
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : ParseLabels(series[(brace + 1)..^1]);

                samples.Add(new ExpositionSample(name, labels, value));
            }

            return new Exposition { Samples = samples, Help = help, Types = types };
        }

        private static Dictionary<string, string> ParseLabels(string body)
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            var index = 0;

            while (index < body.Length)
            {
                var equals = body.IndexOf('=', index);
                Assert.True(equals > index, $"malformed label set: {body}");

                var key = body[index..equals];
                Assert.Equal('"', body[equals + 1]);

                var builder = new System.Text.StringBuilder();
                var cursor = equals + 2;

                while (body[cursor] != '"')
                {
                    if (body[cursor] == '\\')
                    {
                        cursor++;
                        builder.Append(body[cursor] switch
                        {
                            'n' => '\n',
                            '"' => '"',
                            '\\' => '\\',
                            var other => other
                        });
                    }
                    else
                    {
                        builder.Append(body[cursor]);
                    }

                    cursor++;
                }

                labels[key] = builder.ToString();
                index = cursor + 1;

                if (index < body.Length && body[index] == ',') index++;
            }

            return labels;
        }
    }
}
