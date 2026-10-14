using InferHub.Coordinator.Observability;
using InferHub.Shared.CloudMedia;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>
/// The hub's cloud pictures, voices and transcripts (phase 104): which provider claims a
/// (capability, model), whether it serves this request rather than a node, and the call itself.
/// </summary>
/// <remarks>
/// <para>
/// Registered always and inert when no provider carries a <c>Media</c> map: <see cref="Choose"/> is
/// a null for every request, so every route behaves exactly as it did in v3.68.
/// </para>
/// <para>
/// <b>There is no node fallback after a provider failure</b> (104 D7). A chat provider under
/// <c>prefer</c> may fall back to a node (65 D3); a picture or a voice may not, because the vendor may
/// already have spent the money — phase 56's rule against spending one request twice.
/// </para>
/// </remarks>
public sealed class MediaProviders
{
    public const string HttpClientName = "inferhub-cloud-media";

    private readonly ProviderOptions options;
    private readonly CloudMediaClient client;
    private readonly Metrics metrics;
    private readonly ILogger<MediaProviders> logger;

    public MediaProviders(
        IOptions<ProviderOptions> options,
        IHttpClientFactory httpClients,
        Metrics metrics,
        ILogger<MediaProviders> logger)
    {
        this.options = options.Value;
        this.metrics = metrics;
        this.logger = logger;

        Catalog = new CloudMediaCatalog(this.options.Entries.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.ToCloudMedia(),
            StringComparer.OrdinalIgnoreCase));

        var http = httpClients.CreateClient(HttpClientName);

        // Each route carries its own timeout (TimeoutSeconds); the client's must not cut it short.
        http.Timeout = Timeout.InfiniteTimeSpan;
        client = new CloudMediaClient(http);
    }

    public CloudMediaCatalog Catalog { get; }

    /// <summary>Does any provider claim this pair, whoever would serve it right now?</summary>
    public bool Claims(string capability, string model) => Catalog.TryResolve(capability, model, out _);

    /// <summary>
    /// The provider that serves this request, or null for "a node does" (104 D7).
    /// </summary>
    /// <param name="nodeAvailable">
    /// Whether the router found a node for the pair. Under <c>no-node</c> — the default, and 65 D1's
    /// — a provider serves only when it did not.
    /// </param>
    public CloudMediaRoute? Choose(string capability, string model, bool nodeAvailable)
    {
        if (!Catalog.TryResolve(capability, model, out var route))
        {
            return null;
        }

        var policy = PolicyFor(route);

        return policy is ProviderPolicy.Prefer or ProviderPolicy.Only || !nodeAvailable ? route : null;
    }

    /// <summary>Whether a node may serve this pair at all — false only under an <c>only</c> policy.</summary>
    public bool NodesAllowed(string capability, string model) =>
        !Catalog.TryResolve(capability, model, out var route) || PolicyFor(route) != ProviderPolicy.Only;

    public string PolicyFor(CloudMediaRoute route) =>
        options.Entries.TryGetValue(route.ProviderId, out var provider)
            ? provider.PolicyFor(route.LocalModel)
            : ProviderPolicy.NoNode;

    /// <summary>
    /// Runs the job against the vendor and records it the way a chat provider's dispatch is recorded
    /// (61 D4/66): the counters, the provider's last error — and the capability, model, provider and
    /// outcome in the log, never the prompt, the audio or the picture (rule 7).
    /// </summary>
    public async Task<ToolResult> RunAsync(CloudMediaRoute route, ToolJob job, CancellationToken cancellationToken)
    {
        metrics.RecordProviderDispatched(route.ProviderId, route.LocalModel);

        var result = await client.RunAsync(job, route, attachments: null, cancellationToken);

        if (!result.Success && !string.Equals(result.ErrorCode, ToolErrorCodes.Cancelled, StringComparison.Ordinal))
        {
            metrics.RecordProviderFailed(route.ProviderId, route.LocalModel, result.Error ?? "the provider failed");
        }

        logger.LogInformation(
            "{Capability} job {JobId} ({Model}) on provider {ProviderId} ({Upstream}): success={Success}",
            route.Capability,
            job.JobId,
            route.LocalModel,
            route.ProviderId,
            route.UpstreamModel,
            result.Success);

        return result;
    }
}
