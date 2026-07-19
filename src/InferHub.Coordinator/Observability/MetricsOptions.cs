namespace InferHub.Coordinator.Observability;

public sealed class MetricsOptions
{
    public const string SectionName = "Metrics";

    /// <summary>
    /// Whether <c>/metrics</c> is reachable without an admin key. Default <c>false</c>.
    ///
    /// <para>The endpoint is operational, like <c>/health</c> which is deliberately open — but
    /// unlike <c>/health</c> it leaks node names, model names, client ids and the shape of your
    /// traffic. So it defaults to the admin key and drops to open only when an operator says so,
    /// which is the right way round: a scrape endpoint that is accidentally public is a
    /// reconnaissance gift, and a scrape endpoint that is accidentally private is a 401 in a
    /// Prometheus log.</para>
    /// </summary>
    public bool OpenScrape { get; set; }
}
