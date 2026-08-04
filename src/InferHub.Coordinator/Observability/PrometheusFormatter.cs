using System.Globalization;
using System.Text;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;

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
    IReadOnlyList<ClientScrapeSample> Clients,
    int AffinityEntries,
    // Null for a single-coordinator deployment: no cluster, no series (D5 — absence is a fact).
    ClusterScrapeSample? Cluster = null,
    // Phase 45, all four appended with empty defaults: a fleet that declares no capability, runs no
    // tool, writes no profile and hosts no node corpus emits exactly the v3.12 scrape.
    IReadOnlyList<CapabilitySummary>? Capabilities = null,
    IReadOnlyList<NodeToolState>? Tools = null,
    IReadOnlyList<ProfileScrapeSample>? Profiles = null,
    IReadOnlyList<NodeCorpusState>? Corpora = null);

/// <summary>
/// How many nodes are in each state under a profile (phase 45). <c>conflict</c> is the hub's own
/// answer and the one worth alerting on: it means two profiles match a box and neither was sent.
/// </summary>
public sealed record ProfileScrapeSample(string Profile, string State, int Nodes);

/// <summary>
/// Leadership, as a dashboard can read it (phase 32). <c>Active</c> is the one that matters: it is
/// the series an alert watches, because a mesh where it sums to 0 has no leader and a mesh where it
/// sums to 2 has a split brain the fence was supposed to prevent.
/// </summary>
public sealed record ClusterScrapeSample(string InstanceId, bool Active, long Fence);

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

        // A fleet gauge, so present at zero: "no warm conversations" is a fact, not an absence.
        Gauge(builder, "inferhub_affinity_entries", "Live sticky-conversation affinity hints.", scrape.AffinityEntries);

        if (scrape.Cluster is { } cluster)
        {
            Header(builder, "inferhub_cluster_active", "gauge",
                "1 when this coordinator holds the lease and serves inference, 0 when it is a standby.");
            Sample(builder, "inferhub_cluster_active", [("instance", cluster.InstanceId)], cluster.Active ? 1 : 0);

            Header(builder, "inferhub_cluster_fence", "gauge",
                "Acquisition counter of the coordinator lease; a change means leadership moved.");
            Sample(builder, "inferhub_cluster_fence", [("instance", cluster.InstanceId)], cluster.Fence);
        }

        PerNode(builder, scrape);
        PerCollection(builder, m);
        Queue(builder, scrape.Queue);
        PerClient(builder, scrape.Clients);
        PerCapability(builder, scrape.Capabilities);
        PerTool(builder, scrape.Tools);
        PerAudio(builder, m.PerAudio);
        PerProfile(builder, scrape.Profiles);
        PerNodeCorpus(builder, scrape.Corpora);

        return builder.ToString();
    }

    /// <summary>
    /// How many nodes serve each capability (phase 45). A capability nobody provides has <b>no</b>
    /// series rather than a zero — D5 again, and here the lie would be a loud one: a
    /// <c>transcription capacity: 0</c> on a fleet that was never asked to transcribe pages somebody
    /// at three in the morning.
    /// </summary>
    private static void PerCapability(StringBuilder builder, IReadOnlyList<CapabilitySummary>? capabilities)
    {
        if (capabilities is not { Count: > 0 }) return;

        Header(builder, "inferhub_capability_nodes", "gauge", "Connected nodes that serve a capability.");
        foreach (var c in capabilities) Sample(builder, "inferhub_capability_nodes", [("capability", c.Capability)], c.Nodes);

        Header(builder, "inferhub_capability_models", "gauge", "Distinct models the fleet serves under a capability.");
        foreach (var c in capabilities) Sample(builder, "inferhub_capability_models", [("capability", c.Capability)], c.Models.Count);
    }

    /// <summary>
    /// What each node's tool runtime is doing, as the node last reported it (phase 45). Labelled by
    /// node as well as by tool: a per-node counter resets when that node restarts, which is a reset
    /// Prometheus detects per series — summing across the fleet into one counter would make every
    /// node bounce look like a fleet-wide rate spike.
    /// </summary>
    private static void PerTool(StringBuilder builder, IReadOnlyList<NodeToolState>? tools)
    {
        var rows = (tools ?? Array.Empty<NodeToolState>())
            .SelectMany(state => state.Tools.Select(tool => (state.NodeId, Tool: tool)))
            .ToArray();

        if (rows.Length == 0) return;

        // A manifest `Tools:Allowed` does not name has **no pool at all** — its worker and request
        // counts are structural zeros rather than measurements, and they would sit on a dashboard
        // for as long as the file is on the box. D2: absence stays absence. Its `tool_pool` series
        // below is the fact that it exists and is not running, which is the whole of what is true.
        // A *suspended* or *stopped* pool keeps its counters: those are real history.
        var pooled = rows.Where(row => row.Tool.State != NodeToolInfo.NotAllowed).ToArray();

        if (pooled.Length > 0)
        {
            Header(builder, "inferhub_tool_requests_total", "counter", "Tool requests a node's worker pool served.");
            foreach (var (node, tool) in pooled)
            {
                Sample(builder, "inferhub_tool_requests_total",
                    [("node", node), ("tool", tool.Id), ("outcome", "ok")], tool.Requests - tool.Failures);
                Sample(builder, "inferhub_tool_requests_total",
                    [("node", node), ("tool", tool.Id), ("outcome", "error")], tool.Failures);
            }

            Header(builder, "inferhub_tool_workers", "gauge", "Warm tool workers a node is holding, by what they are doing.");
            foreach (var (node, tool) in pooled)
            {
                Sample(builder, "inferhub_tool_workers",
                    [("node", node), ("tool", tool.Id), ("state", "busy")], tool.Busy);
                Sample(builder, "inferhub_tool_workers",
                    [("node", node), ("tool", tool.Id), ("state", "idle")], Math.Max(0, tool.Workers - tool.Busy));
            }
        }

        // A pool that gave up holds zero workers. So does a pool nobody has called yet. Without this
        // series a dashboard cannot tell them apart — which is D2's own complaint about zeros,
        // pointed at the thing D2 asked for.
        Header(builder, "inferhub_tool_pool", "gauge", "1 for the state a node's tool pool is in: running, suspended, stopped or not-allowed.");
        foreach (var (node, tool) in rows)
        {
            Sample(builder, "inferhub_tool_pool",
                [("node", node), ("tool", tool.Id), ("state", tool.State)], 1);
        }
    }

    /// <summary>
    /// Audio work per <c>(kind, model)</c>. Two series rather than one, because a transcription is
    /// metered in seconds and a synthesis in characters (phase-42 D7) — and a pair that has only
    /// ever done one of them emits only that one.
    /// </summary>
    private static void PerAudio(StringBuilder builder, IReadOnlyList<AudioMetricsSnapshot>? audio)
    {
        if (audio is not { Count: > 0 }) return;

        var seconds = audio.Where(a => a.Seconds > 0).ToArray();
        var characters = audio.Where(a => a.Characters > 0).ToArray();

        if (seconds.Length > 0)
        {
            Header(builder, "inferhub_audio_seconds_total", "counter", "Audio seconds transcribed, as the worker measured the decoded file.");
            foreach (var a in seconds) Sample(builder, "inferhub_audio_seconds_total", [("kind", a.Kind), ("model", a.Model)], a.Seconds);
        }

        if (characters.Length > 0)
        {
            Header(builder, "inferhub_audio_characters_total", "counter", "Characters synthesised, counted at the edge.");
            foreach (var a in characters) Sample(builder, "inferhub_audio_characters_total", [("kind", a.Kind), ("model", a.Model)], a.Characters);
        }
    }

    /// <summary>
    /// Nodes per <c>(profile, state)</c> (phase 45). The series an operator alerts on is
    /// <c>state="refused"</c> and <c>state="conflict"</c>: both mean a box is not doing what the
    /// fleet's configuration says it should, and neither shows up anywhere else as a number.
    /// </summary>
    private static void PerProfile(StringBuilder builder, IReadOnlyList<ProfileScrapeSample>? profiles)
    {
        if (profiles is not { Count: > 0 }) return;

        Header(builder, "inferhub_profile_state", "gauge", "Connected nodes under a profile, by whether they applied it.");
        foreach (var p in profiles)
        {
            Sample(builder, "inferhub_profile_state", [("profile", p.Profile), ("state", p.State)], p.Nodes);
        }
    }

    /// <summary>
    /// Records in each node-owned collection, as the owning node last counted them (phase-44 D6).
    /// The hub does not query a node to answer a scrape, so this is a report and its staleness is
    /// bounded by the node's model-refresh interval.
    /// </summary>
    private static void PerNodeCorpus(StringBuilder builder, IReadOnlyList<NodeCorpusState>? corpora)
    {
        var rows = (corpora ?? Array.Empty<NodeCorpusState>())
            .Where(state => state.Enabled)
            .SelectMany(state => state.Collections.Select(c => (state.NodeId, Collection: c)))
            .ToArray();

        if (rows.Length == 0) return;

        Header(builder, "inferhub_node_corpus_records", "gauge", "Records in a node-owned collection, as its owner counts them.");
        foreach (var (node, collection) in rows)
        {
            Sample(builder, "inferhub_node_corpus_records",
                [("node", node), ("collection", collection.Name)], collection.Records);
        }
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
