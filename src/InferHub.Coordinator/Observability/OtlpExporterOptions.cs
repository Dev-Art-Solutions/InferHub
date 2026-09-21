namespace InferHub.Coordinator.Observability;

/// <summary>
/// The shape of the OTLP push exporter (phase 81). <c>Enabled</c> itself is read straight from
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> inside
/// <see cref="OtlpMetricsExporterService"/>, the same way <c>AutoScalerService</c> and
/// <c>CorpusFailoverService</c> read their own on/off switch — this class binds the rest, the same
/// split <see cref="MetricsOptions"/> already draws between "is this reachable" and "what does this do".
/// </summary>
public sealed class OtlpExporterOptions
{
    public const string SectionName = "Observability:Otlp";

    /// <summary>
    /// The collector's base URL, e.g. <c>http://otel-collector:4318</c>. <c>/v1/metrics</c> is
    /// appended by the exporter. Null (the default) means nothing is sent even if <c>Enabled</c> is
    /// somehow set without one — there is no default collector to guess at.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>How often a push is attempted. Default 30s.</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Static headers applied to every push, verbatim (phase-81 D7) — e.g. a collector's own ingest
    /// key. Never a credential this hub invents or reads from anywhere else.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.Ordinal);
}
