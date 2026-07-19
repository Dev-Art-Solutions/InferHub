using System.Globalization;
using System.Text;
using InferHub.Coordinator.Services;

namespace InferHub.Coordinator.Observability;

/// <summary>
/// Everything a scrape needs, gathered once by the endpoint and handed here. The formatter is a
/// pure function over it — no services, no clock, no I/O — so a test can assert the exact bytes.
/// </summary>
public sealed record PrometheusScrape(
    string Version,
    MetricsSnapshot Metrics,
    IReadOnlyList<NodeSnapshot> Nodes,
    IReadOnlyList<ThroughputSample> Throughput,
    QueueSnapshot Queue,
    IReadOnlyList<ClientScrapeSample> Clients);

/// <summary>
/// A client's live window consumption against its configured limits. Counts and ids only — the
/// same shape <c>/api/admin/clients</c> already exposes, and for the same reason it is safe:
/// there is no content anywhere in the usage path (rule 7). Fed from <see cref="AdmissionControl"/>,
/// never from the usage ledger — the ledger is append-only history and is never read to drive
/// anything (rule 4 / phase-25 D2).
/// </summary>
public sealed record ClientScrapeSample(
    string ClientId,
    int InFlight,
    int RequestsLastMinute,
    long TokensLastMinute,
    long TokensToday,
    int? MaxConcurrent,
    int? RequestsPerMinute,
    long? TokensPerMinute,
    long? TokensPerDay);

/// <summary>
/// The Prometheus text exposition format, written by hand. It is <c># HELP</c> / <c># TYPE</c> /
/// <c>name{labels} value</c> — three lines of string formatting, the same reasoning that kept the
/// NDJSON framing (phase 9) and the SSE framing (phase 21) dependency-free. Taking
/// <c>prometheus-net</c> for this would buy a registry abstraction we do not want on the hot path
/// and a dependency we would then have to keep, in exchange for code that fits on a screen.
///
/// <para>Nothing here measures anything. Every number already existed in <c>Metrics</c>,
/// <c>ThroughputTracker</c>, <c>RequestQueue</c> and <c>AdmissionControl</c>; this phase gives them
/// a history and an alert, and adds no work to the request path.</para>
/// </summary>
public static class PrometheusFormatter
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static string Format(PrometheusScrape scrape)
    {
        var builder = new StringBuilder(8 * 1024);

        Info(builder, "inferhub_build_info", "Coordinator build, as a label on a constant 1.",
            [("version", scrape.Version)]);

        Gauge(builder, "inferhub_uptime_seconds", "Seconds since this coordinator started.",
            scrape.Metrics.UptimeSeconds);

        var m = scrape.Metrics;

        Counter(builder, "inferhub_requests_total", "Inference requests accepted, all dialects.", m.RequestsTotal);
        Gauge(builder, "inferhub_requests_in_flight", "Inference requests currently executing on the fleet.", m.RequestsInFlight);
        Counter(builder, "inferhub_requests_completed_total", "Inference requests that completed.", m.RequestsCompleted);
        Counter(builder, "inferhub_requests_failed_total", "Inference requests that failed.", m.RequestsFailed);
        Counter(builder, "inferhub_failovers_attempted_total", "Pre-stream failovers attempted.", m.FailoversAttempted);
        Counter(builder, "inferhub_failovers_succeeded_total", "Pre-stream failovers that found another node.", m.FailoversSucceeded);
        Counter(builder, "inferhub_nodes_evicted_total", "Nodes evicted by the heartbeat reaper.", m.NodesEvicted);
        Counter(builder, "inferhub_openai_requests_total", "Requests that arrived over the OpenAI-compatible surface.", m.OpenAiRequestsTotal);
        Counter(builder, "inferhub_fallback_dispatched_total", "Requests sent to the cloud-burst upstream instead of a node.", m.FallbackDispatched);

        // Info-style: the model name is a label, not a value. Absent entirely until a burst has
        // happened, so a deployment that never bursts has no series rather than an empty one.
        if (!string.IsNullOrWhiteSpace(m.LastFallbackModel))
        {
            Info(builder, "inferhub_fallback_last_model", "Model of the most recent cloud burst.",
                [("model", m.LastFallbackModel!)]);
        }

        Counter(builder, "inferhub_vector_replicas_healed_total", "Vector replicas re-pushed by the healing service.", m.VectorReplicasHealed);
        Counter(builder, "inferhub_vector_rebuilds_from_raw_total", "Vector index rebuilds from the raw store.", m.VectorRebuildsFromRaw);
        Gauge(builder, "inferhub_vector_under_replicated", "Collections currently below their replication factor.", m.VectorUnderReplicated);

        PerNode(builder, scrape);
        PerCollection(builder, m);
        Queue(builder, scrape.Queue);
        PerClient(builder, scrape.Clients);

        return builder.ToString();
    }

    private static void PerNode(StringBuilder builder, PrometheusScrape scrape)
    {
        var counters = scrape.Metrics.PerNode;

        if (counters.Count > 0)
        {
            Header(builder, "inferhub_node_requests_total", "counter", "Requests routed to a node.");
            foreach (var node in counters) Sample(builder, "inferhub_node_requests_total", [("node", node.NodeId)], node.RequestsTotal);

            Header(builder, "inferhub_node_requests_in_flight", "gauge", "Requests currently executing on a node, as the hub counts them.");
            foreach (var node in counters) Sample(builder, "inferhub_node_requests_in_flight", [("node", node.NodeId)], node.RequestsInFlight);

            Header(builder, "inferhub_node_requests_completed_total", "counter", "Requests a node completed.");
            foreach (var node in counters) Sample(builder, "inferhub_node_requests_completed_total", [("node", node.NodeId)], node.RequestsCompleted);

            Header(builder, "inferhub_node_requests_failed_total", "counter", "Requests a node failed.");
            foreach (var node in counters) Sample(builder, "inferhub_node_requests_failed_total", [("node", node.NodeId)], node.RequestsFailed);
        }

        var nodes = scrape.Nodes;

        if (nodes.Count > 0)
        {
            Header(builder, "inferhub_node_up", "gauge", "1 for every node currently connected to the hub.");
            foreach (var node in nodes) Sample(builder, "inferhub_node_up", [("node", node.NodeId), ("name", node.Name)], 1);

            Header(builder, "inferhub_node_cordoned", "gauge", "1 when a node is cordoned and takes no new work.");
            foreach (var node in nodes) Sample(builder, "inferhub_node_cordoned", [("node", node.NodeId)], node.Cordoned ? 1 : 0);

            Header(builder, "inferhub_node_models", "gauge", "Models a node advertises.");
            foreach (var node in nodes) Sample(builder, "inferhub_node_models", [("node", node.NodeId)], node.ModelCount);

            Header(builder, "inferhub_node_local_in_flight", "gauge", "Requests a node reports executing locally.");
            foreach (var node in nodes) Sample(builder, "inferhub_node_local_in_flight", [("node", node.NodeId)], node.LocalInFlight);

            Header(builder, "inferhub_node_seconds_since_heartbeat", "gauge", "Age of a node's last heartbeat.");
            foreach (var node in nodes) Sample(builder, "inferhub_node_seconds_since_heartbeat", [("node", node.NodeId)], node.AgeSeconds);
        }

        // Unmeasured (node, model) pairs produce no series at all. An unmeasured node is treated
        // as *average* by the router (phase 26, D4), never as zero — emitting a 0 here would put a
        // lie on a dashboard and invite an alert on a node that has simply not been asked yet.
        if (scrape.Throughput.Count > 0)
        {
            Header(builder, "inferhub_node_tokens_per_second", "gauge", "Measured decayed tokens/second per node and model (EWMA).");
            foreach (var sample in scrape.Throughput)
            {
                Sample(builder, "inferhub_node_tokens_per_second",
                    [("node", sample.NodeId), ("model", sample.Model)], sample.TokensPerSecond);
            }
        }
    }

    private static void PerCollection(StringBuilder builder, MetricsSnapshot metrics)
    {
        var collections = metrics.PerCollection;
        if (collections.Count == 0) return;

        Header(builder, "inferhub_collection_queries_total", "counter", "Retrieval queries served per collection.");
        foreach (var c in collections) Sample(builder, "inferhub_collection_queries_total", [("collection", c.Collection)], c.Queries);

        Header(builder, "inferhub_collection_query_latency_avg_ms", "gauge", "Mean retrieval latency per collection since start.");
        foreach (var c in collections) Sample(builder, "inferhub_collection_query_latency_avg_ms", [("collection", c.Collection)], c.QueryLatencyAvgMs);

        Header(builder, "inferhub_collection_documents_ingested_total", "counter", "Documents ingested into a collection since start.");
        foreach (var c in collections) Sample(builder, "inferhub_collection_documents_ingested_total", [("collection", c.Collection)], c.DocumentsIngested);

        Header(builder, "inferhub_collection_chunks_embedded_total", "counter", "Chunks embedded into a collection since start.");
        foreach (var c in collections) Sample(builder, "inferhub_collection_chunks_embedded_total", [("collection", c.Collection)], c.ChunksEmbedded);

        Header(builder, "inferhub_collection_ingestion_failures_total", "counter", "Ingestion runs that failed for a collection.");
        foreach (var c in collections) Sample(builder, "inferhub_collection_ingestion_failures_total", [("collection", c.Collection)], c.IngestionFailures);
    }

    private static void Queue(StringBuilder builder, QueueSnapshot queue)
    {
        Gauge(builder, "inferhub_queue_depth", "Requests currently waiting for fleet capacity.", queue.Depth);
        Counter(builder, "inferhub_queue_queued_total", "Requests that had to wait for capacity.", queue.Queued);
        Counter(builder, "inferhub_queue_admitted_total", "Queued requests that got a slot.", queue.Admitted);
        Counter(builder, "inferhub_queue_timed_out_total", "Queued requests that waited out the bound and got a 503.", queue.TimedOut);
        Counter(builder, "inferhub_queue_rejected_total", "Requests rejected because the queue itself was full.", queue.Rejected);

        // No samples yet means no median. Absent rather than 0 — "nothing has ever queued" and
        // "everything is admitted instantly" are different facts and should not share a value.
        if (queue.MedianWaitMs is { } median)
        {
            Gauge(builder, "inferhub_queue_wait_median_ms", "Median wait of the last 128 queued requests.", median);
        }
    }

    private static void PerClient(StringBuilder builder, IReadOnlyList<ClientScrapeSample> clients)
    {
        if (clients.Count == 0) return;

        Header(builder, "inferhub_client_requests_in_flight", "gauge", "Requests a named client currently has in flight.");
        foreach (var c in clients) Sample(builder, "inferhub_client_requests_in_flight", [("client", c.ClientId)], c.InFlight);

        Header(builder, "inferhub_client_requests_last_minute", "gauge", "Requests a named client made in the trailing minute.");
        foreach (var c in clients) Sample(builder, "inferhub_client_requests_last_minute", [("client", c.ClientId)], c.RequestsLastMinute);

        Header(builder, "inferhub_client_tokens_last_minute", "gauge", "Tokens a named client consumed in the trailing minute.");
        foreach (var c in clients) Sample(builder, "inferhub_client_tokens_last_minute", [("client", c.ClientId)], c.TokensLastMinute);

        Header(builder, "inferhub_client_tokens_today", "gauge", "Tokens a named client consumed since UTC midnight.");
        foreach (var c in clients) Sample(builder, "inferhub_client_tokens_today", [("client", c.ClientId)], c.TokensToday);

        // A limit that is null is unlimited, and an unlimited limit has no series — not a 0, and
        // not a sentinel like -1 that a dashboard would happily plot.
        Limit(builder, clients, "inferhub_client_limit_max_concurrent", "Configured concurrency cap.", c => c.MaxConcurrent);
        Limit(builder, clients, "inferhub_client_limit_requests_per_minute", "Configured requests-per-minute cap.", c => c.RequestsPerMinute);
        Limit(builder, clients, "inferhub_client_limit_tokens_per_minute", "Configured tokens-per-minute cap.", c => c.TokensPerMinute);
        Limit(builder, clients, "inferhub_client_limit_tokens_per_day", "Configured daily token budget.", c => c.TokensPerDay);
    }

    private static void Limit(
        StringBuilder builder,
        IReadOnlyList<ClientScrapeSample> clients,
        string name,
        string help,
        Func<ClientScrapeSample, double?> select)
    {
        var set = clients.Where(c => select(c) is not null).ToArray();
        if (set.Length == 0) return;

        Header(builder, name, "gauge", help);
        foreach (var c in set) Sample(builder, name, [("client", c.ClientId)], select(c)!.Value);
    }

    private static void Counter(StringBuilder builder, string name, string help, double value)
    {
        Header(builder, name, "counter", help);
        Sample(builder, name, [], value);
    }

    private static void Gauge(StringBuilder builder, string name, string help, double value)
    {
        Header(builder, name, "gauge", help);
        Sample(builder, name, [], value);
    }

    private static void Info(StringBuilder builder, string name, string help, (string Key, string Value)[] labels)
    {
        Header(builder, name, "gauge", help);
        Sample(builder, name, labels, 1);
    }

    private static void Header(StringBuilder builder, string name, string type, string help)
    {
        builder.Append("# HELP ").Append(name).Append(' ').Append(EscapeHelp(help)).Append('\n');
        builder.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
    }

    private static void Sample(StringBuilder builder, string name, (string Key, string Value)[] labels, double value)
    {
        builder.Append(name);

        if (labels.Length > 0)
        {
            builder.Append('{');
            for (var i = 0; i < labels.Length; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append(labels[i].Key).Append("=\"").Append(EscapeLabel(labels[i].Value)).Append('"');
            }
            builder.Append('}');
        }

        builder.Append(' ').Append(FormatValue(value)).Append('\n');
    }

    // Node ids, model names and client ids are operator-chosen strings, so they can contain
    // anything. The exposition spec escapes exactly three characters in a label value.
    private static string EscapeLabel(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string EscapeHelp(string help) => help
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string FormatValue(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "+Inf";
        if (double.IsNegativeInfinity(value)) return "-Inf";

        return value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
