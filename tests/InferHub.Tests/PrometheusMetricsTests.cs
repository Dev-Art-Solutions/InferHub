using System.Net;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
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

    // ---- phase 45 -------------------------------------------------------------------------------

    [Fact]
    public void CapabilityToolProfileAndCorpusSeriesCarryTheReportedValues()
    {
        var parsed = Exposition.Parse(PrometheusFormatter.Format(TrackScrape()));

        Assert.Equal(2, parsed.Value("inferhub_capability_nodes", ("capability", "chat")));
        Assert.Equal(1, parsed.Value("inferhub_capability_nodes", ("capability", "transcribe")));

        // 12 served, 1 of them failed — so 11 ok. A single "requests" counter would leave a
        // dashboard unable to draw an error rate without a second series to subtract.
        Assert.Equal(11, parsed.Value("inferhub_tool_requests_total", ("node", "gpu-1"), ("tool", "piper"), ("outcome", "ok")));
        Assert.Equal(1, parsed.Value("inferhub_tool_requests_total", ("node", "gpu-1"), ("tool", "piper"), ("outcome", "error")));

        Assert.Equal(1, parsed.Value("inferhub_tool_workers", ("node", "gpu-1"), ("tool", "piper"), ("state", "busy")));
        Assert.Equal(2, parsed.Value("inferhub_tool_workers", ("node", "gpu-1"), ("tool", "piper"), ("state", "idle")));
        Assert.Equal(1, parsed.Value("inferhub_tool_pool", ("node", "gpu-1"), ("tool", "piper"), ("state", "running")));
        Assert.Equal(1, parsed.Value("inferhub_tool_pool", ("node", "gpu-1"), ("tool", "whisper"), ("state", "not-allowed")));

        // A manifest that was never allowed has no pool, so its worker and request counts are
        // structural zeros rather than measurements — and they would sit on a dashboard for as long
        // as the file is on the box. Found by scraping the published image, not by a test.
        Assert.DoesNotContain(parsed.Samples, sample =>
            sample.Name is "inferhub_tool_workers" or "inferhub_tool_requests_total"
            && sample.Labels["tool"] == "whisper");

        Assert.Equal(90.5, parsed.Value("inferhub_audio_seconds_total", ("kind", "transcribe"), ("model", "whisper-small")));
        Assert.Equal(64, parsed.Value("inferhub_audio_characters_total", ("kind", "speak"), ("model", "en_US-amy")));

        Assert.Equal(1, parsed.Value("inferhub_profile_state", ("profile", "gpu-boxes"), ("state", "refused")));
        Assert.Equal(1240, parsed.Value("inferhub_node_corpus_records", ("node", "gpu-1"), ("collection", "handbook")));
    }

    /// <summary>
    /// D2. A capability nobody serves, a tool nobody loaded, a profile nobody wrote and a corpus
    /// nobody assigned each produce <b>no</b> series — not a zero. "transcription capacity: 0" on a
    /// fleet that was never asked to transcribe is a statement, and a false one.
    /// </summary>
    [Fact]
    public void AbsenceStaysAbsenceForEveryPhase45Series()
    {
        var parsed = Exposition.Parse(PrometheusFormatter.Format(SampleScrape()));

        foreach (var name in new[]
        {
            "inferhub_capability_nodes",
            "inferhub_tool_requests_total",
            "inferhub_tool_workers",
            "inferhub_tool_pool",
            "inferhub_audio_seconds_total",
            "inferhub_audio_characters_total",
            "inferhub_profile_state",
            "inferhub_node_corpus_records",

            // Phase 51, and the two that would be loudest if they were wrong. A recipe nobody has
            // rendered with emits nothing; a node with no DECLARED VRAM budget emits nothing —
            // `budget_mib{node}=0` reads as "this box has no VRAM", which is a different and false
            // statement from "nobody declared a budget on this box" (48 D1).
            "inferhub_image_jobs_total",
            "inferhub_image_job_seconds_bucket",
            "inferhub_image_recipe",
            "inferhub_node_vram_budget_mib",
            "inferhub_node_vram_resident_mib",
            "inferhub_node_vram_measured_mib"
        })
        {
            Assert.DoesNotContain(parsed.Samples, sample => sample.Name == name);
        }
    }

    /// <summary>
    /// The image queue's gauges are the <em>opposite</em> of the series above: fleet-level, so
    /// present at zero (phase-28 D5's other half). A hub with an image queue and nothing in it is
    /// saying something, and a dashboard cannot tell "idle" from "not scraped" otherwise.
    /// </summary>
    [Fact]
    public void TheImageQueueGaugesArePresentAtZero()
    {
        var parsed = Exposition.Parse(PrometheusFormatter.Format(
            SampleScrape() with { ImageQueue = new ImageQueueScrapeSample(0, 0, 0) }));

        foreach (var name in new[] { "inferhub_image_queue_depth", "inferhub_image_jobs_active", "inferhub_image_retained_bytes" })
        {
            Assert.Equal(0, Assert.Single(parsed.Samples, sample => sample.Name == name).Value);
        }
    }

    /// <summary>
    /// The duration histogram is hand-written, so this asserts it is actually one: cumulative
    /// buckets, a <c>+Inf</c> row that equals <c>_count</c>, and a <c>_sum</c>. Without the
    /// <c>+Inf</c> bucket <c>histogram_quantile</c> returns nothing at all rather than an obviously
    /// wrong number, which is the failure mode worth a test.
    /// </summary>
    [Fact]
    public void TheImageJobHistogramIsShapedLikeAHistogram()
    {
        var metrics = new Metrics();
        metrics.RecordImageJob("sdxl", "succeeded", 0.5);
        metrics.RecordImageJob("sdxl", "succeeded", 8);
        metrics.RecordImageJob("sdxl", "failed", 400);

        var parsed = Exposition.Parse(PrometheusFormatter.Format(
            SampleScrape() with { Metrics = metrics.Snapshot(DateTimeOffset.UtcNow) }));

        double Bucket(string le) => Assert.Single(
            parsed.Samples,
            s => s.Name == "inferhub_image_job_seconds_bucket" && s.Labels["le"] == le && s.Labels["recipe"] == "sdxl").Value;

        // Cumulative: 0.5s is in every bucket, 8s from le=15 up, 400s only in +Inf.
        Assert.Equal(1, Bucket("1"));
        Assert.Equal(1, Bucket("5"));
        Assert.Equal(2, Bucket("15"));
        Assert.Equal(2, Bucket("60"));
        Assert.Equal(2, Bucket("300"));
        Assert.Equal(3, Bucket("+Inf"));

        var count = Assert.Single(parsed.Samples, s => s.Name == "inferhub_image_job_seconds_count").Value;
        var sum = Assert.Single(parsed.Samples, s => s.Name == "inferhub_image_job_seconds_sum").Value;

        Assert.Equal(3, count);
        Assert.Equal(Bucket("+Inf"), count);
        Assert.Equal(408.5, sum, 3);

        // Every outcome, not only the happy one: a fleet whose `failed` counter was absent would
        // look identical whether it was healthy or dropping every third render.
        Assert.Equal(2, Assert.Single(parsed.Samples,
            s => s.Name == "inferhub_image_jobs_total" && s.Labels["outcome"] == "succeeded").Value);
        Assert.Equal(1, Assert.Single(parsed.Samples,
            s => s.Name == "inferhub_image_jobs_total" && s.Labels["outcome"] == "failed").Value);
    }

    /// <summary>
    /// A recipe a node holds and will not offer is invisible in every other series — it is simply
    /// absent from the capability list. This is the one that can be alerted on (phase 51, D1).
    /// </summary>
    [Fact]
    public void ARefusedImageRecipeIsAScrapeableSeriesWithItsReason()
    {
        var parsed = Exposition.Parse(PrometheusFormatter.Format(TrackScrape()));

        var refused = Assert.Single(parsed.Samples,
            s => s.Name == "inferhub_image_recipe" && s.Labels["recipe"] == "sdxl-turbo");

        Assert.Equal("unlicensed", refused.Labels["reason"]);
        Assert.Equal("gpu-1", refused.Labels["node"]);
        Assert.Equal(1, refused.Value);

        Assert.Equal("ok", Assert.Single(parsed.Samples,
            s => s.Name == "inferhub_image_recipe" && s.Labels["recipe"] == "sdxl").Labels["reason"]);
    }

    /// <summary>
    /// The declared budget and the worker's own reading, side by side and never merged — a
    /// disagreement is the thing worth seeing, and a hub that adopted the measurement would have
    /// re-detected VRAM after phase 48 decided not to (48 D1).
    /// </summary>
    [Fact]
    public void TheCardsDeclaredBudgetAndItsMeasuredSizeAreSeparateSeries()
    {
        var parsed = Exposition.Parse(PrometheusFormatter.Format(TrackScrape()));

        Assert.Equal(24576, Assert.Single(parsed.Samples, s => s.Name == "inferhub_node_vram_budget_mib").Value);
        Assert.Equal(2048, Assert.Single(parsed.Samples, s => s.Name == "inferhub_node_vram_reserve_mib").Value);
        Assert.Equal(8000, Assert.Single(parsed.Samples, s => s.Name == "inferhub_node_vram_resident_mib").Value);
        Assert.Equal(24564, Assert.Single(parsed.Samples, s => s.Name == "inferhub_node_vram_measured_mib").Value);
    }

    /// <summary>
    /// A transcription is metered in seconds and a synthesis in characters (phase-42 D7), so a
    /// <c>(kind, model)</c> pair that has only ever done one of them emits only that one series —
    /// a zero in the other unit would be a number nobody can tell from a real measurement.
    /// </summary>
    [Fact]
    public void AudioPairsOnlyEmitTheUnitTheyWereActuallyMeasuredIn()
    {
        var parsed = Exposition.Parse(PrometheusFormatter.Format(TrackScrape()));

        Assert.DoesNotContain(parsed.Samples, sample =>
            sample.Name == "inferhub_audio_characters_total" && sample.Labels["kind"] == "transcribe");
        Assert.DoesNotContain(parsed.Samples, sample =>
            sample.Name == "inferhub_audio_seconds_total" && sample.Labels["kind"] == "speak");
    }

    [Fact]
    public void Phase45SeriesAreParseableAndCarryHelpAndType()
    {
        var parsed = Exposition.Parse(PrometheusFormatter.Format(TrackScrape()));

        foreach (var name in parsed.Samples.Select(sample => sample.Name).Distinct())
        {
            Assert.True(parsed.Help.ContainsKey(name), $"{name} has no # HELP line");
            Assert.True(parsed.Types.ContainsKey(name), $"{name} has no # TYPE line");
        }

        // 90.5 seconds must not come out as "90,5" on a Bulgarian or German host — the locale bug
        // that sinks a whole scrape and only appears on the machines nobody runs CI on.
        Assert.Contains("inferhub_audio_seconds_total{kind=\"transcribe\",model=\"whisper-small\"} 90.5", parsed.Raw);
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

    /// <summary>
    /// A fleet that actually uses phases 40–45: two nodes declaring capabilities, one running a
    /// tool pool and holding a manifest it was never allowed to start, a profile it refused an item
    /// of, a corpus it owns, and audio metered in both units.
    /// </summary>
    private static PrometheusScrape TrackScrape()
    {
        var metrics = new Metrics();
        metrics.RecordToolUnits("transcribe", "whisper-small", 90.5, InferHub.Shared.Contracts.UsageUnitKinds.AudioSeconds);
        metrics.RecordToolUnits("speak", "en_US-amy", 64, InferHub.Shared.Contracts.UsageUnitKinds.Characters);

        var now = DateTimeOffset.UtcNow;

        return SampleScrape() with
        {
            Metrics = metrics.Snapshot(now),
            Capabilities =
            [
                new CapabilitySummary("chat", 2, ["llama3"]),
                new CapabilitySummary("transcribe", 1, ["whisper-small"])
            ],
            Tools =
            [
                new NodeToolState("gpu-1", Enabled: true,
                [
                    new NodeToolInfo("piper", true, NodeToolInfo.Running,
                        [new NodeCapability("speak", ["en_US-amy"])],
                        MaxWorkers: 3, Workers: 3, Busy: 1, Requests: 12, Failures: 1,
                        LastError: null, LastErrorAtUtc: null),
                    new NodeToolInfo("whisper", false, NodeToolInfo.NotAllowed, [],
                        MaxWorkers: 0, Workers: 0, Busy: 0, Requests: 0, Failures: 0,
                        LastError: null, LastErrorAtUtc: null)
                ], now,
                new NodeVramState(24576, 2048, 24564, [new NodeResidentModel("sdxl", 8000, InUse: true)]),
                [
                    new NodeImageRecipeState("sdxl", true, ImageRecipeReasons.Ok, ["image", "image-edit"],
                        8000, "CreativeML-OpenRAIL++-M", null, "none"),
                    new NodeImageRecipeState("sdxl-turbo", false, ImageRecipeReasons.Unlicensed, [],
                        8000, "sai-nc-community", null, "none")
                ])
            ],
            Profiles = [new ProfileScrapeSample("gpu-boxes", "refused", 1)],
            Corpora =
            [
                new NodeCorpusState("gpu-1", Enabled: true, Provider: "qdrant", Status: NodeCorpusState.Running,
                    Collections: [new NodeCorpusCollection("handbook", 768, 1240)], Error: null, now)
            ]
        };
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

        /// <summary>The bytes as written, for the few assertions that are genuinely about them.</summary>
        public required string Raw { get; init; }

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

            return new Exposition { Samples = samples, Help = help, Types = types, Raw = text };
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
