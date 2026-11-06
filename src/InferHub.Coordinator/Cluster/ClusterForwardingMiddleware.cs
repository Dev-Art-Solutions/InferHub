using System.Text.Json;
using InferHub.Coordinator.Services;
using Microsoft.AspNetCore.Http.Features;

namespace InferHub.Coordinator.Cluster;

/// <summary>
/// Phase 132 D3–D4. In an active-active cluster each node is connected to one member, so a request can arrive at a member
/// none of whose nodes hold the model it asks for. This passes such a request to a member whose nodes do, and streams the
/// answer back unchanged; and a job id this member never issued is looked for on the others.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only when this member cannot serve it from its own nodes.</b> A model one of this member's nodes holds is served here,
/// as before — passing work on is the exception that keeps a request from failing, not a second load balancer (32 D5). The
/// question asked is the router's own (<see cref="INodeRegistry.FindNodesWithModel"/>, under the request's organisation scope),
/// so "no node here can take it" means exactly what it means to a request that would otherwise get a 404 or a 503.
/// </para>
/// <para>
/// <b>Once.</b> A passed-on request carries <see cref="ForwardedByHeader"/>, and a request that carries it is never passed on
/// again: two members that each think the other holds a model answer once each, never in a loop.
/// </para>
/// <para>
/// <b>As the client.</b> Every header goes with it, the client's key included, so the member that serves it authenticates,
/// limits, meters, audits and scopes it exactly as if the client had called it. That is why the members must share their
/// <c>Auth</c> configuration — they already share everything else.
/// </para>
/// <para>
/// <b>A member that does not answer is not an error.</b> Its heartbeat may be a few seconds stale; the request is then served
/// here as if it had never been passed on, which gives the client the answer this member would have given anyway.
/// </para>
/// </remarks>
internal sealed class ClusterForwardingMiddleware(
    RequestDelegate next,
    IClusterMembership membership,
    ClusterPeers peers,
    IHttpClientFactory clients,
    ILogger<ClusterForwardingMiddleware> logger)
{
    public const string ForwardedByHeader = "X-InferHub-Forwarded-By";

    public const string ForwardedToHeader = "X-InferHub-Forwarded-To";

    public const string ClientName = "inferhub-cluster-peer";

    /// <summary>Routes whose JSON body names the model they need.</summary>
    private static readonly string[] ModelRoutes =
    [
        "/v1",
        "/api/generate",
        "/api/chat",
        "/api/embed",
        "/api/embeddings",
        "/api/images/jobs"
    ];

    private const string JobsPrefix = "/api/images/jobs";

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Proxy-Authorization",
        "Proxy-Connection", "Expect", "Content-Length", ClusterRoleMiddleware.RoleHeader
    };

    public async Task InvokeAsync(HttpContext context)
    {
        if (!membership.ActiveActive
            || !membership.IsActive
            || peers.Live.Count == 0
            || context.Request.Headers.ContainsKey(ForwardedByHeader))
        {
            await next(context);
            return;
        }

        if (JobId(context.Request) is { } jobId)
        {
            await JobAsync(context, jobId);
            return;
        }

        if (await ModelAsync(context) is { } model
            && context.RequestServices.GetRequiredService<INodeRegistry>().FindNodesWithModel(model).Count == 0
            && peers.FindFor(model) is { } peer)
        {
            if (await TryForwardAsync(context, peer, rewindable: true, missingIsFinal: true))
            {
                return;
            }

            context.Request.Body.Position = 0;
        }

        await next(context);
    }

    /// <summary>
    /// D4: <c>GET</c>/<c>DELETE /api/images/jobs/{id}…</c> for an id this member does not hold goes to each other member in turn
    /// until one does. A client behind a balancer polls whichever member it reaches; the job lives where it was submitted.
    /// </summary>
    private async Task JobAsync(HttpContext context, Guid id)
    {
        var jobs = context.RequestServices.GetService<ImageJobRegistry>();

        if (jobs is null || jobs.Store.Peek(id) is not null)
        {
            await next(context);
            return;
        }

        foreach (var peer in peers.Live)
        {
            if (await TryForwardAsync(context, peer, rewindable: false, missingIsFinal: false))
            {
                return;
            }
        }

        await next(context);
    }

    private static Guid? JobId(HttpRequest request)
    {
        if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsDelete(request.Method))
            || !request.Path.StartsWithSegments(JobsPrefix, out var rest)
            || !rest.HasValue)
        {
            return null;
        }

        var segment = rest.Value!.TrimStart('/').Split('/')[0];
        return Guid.TryParse(segment, out var id) ? id : null;
    }

    /// <summary>The <c>model</c> of a JSON POST to a model route, with the body left readable from the start.</summary>
    private static async Task<string?> ModelAsync(HttpContext context)
    {
        var request = context.Request;

        if (!HttpMethods.IsPost(request.Method)
            || request.Path.StartsWithSegments("/v1/models")
            || !ModelRoutes.Any(route => request.Path.StartsWithSegments(route))
            || request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) is not true)
        {
            return null;
        }

        request.EnableBuffering();

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: context.RequestAborted);
            return document.RootElement.ValueKind is JsonValueKind.Object
                   && document.RootElement.TryGetProperty("model", out var model)
                   && model.ValueKind is JsonValueKind.String
                ? model.GetString()
                : null;
        }
        catch (JsonException)
        {
            // Not this middleware's to refuse: the endpoint answers a malformed body in the caller's own dialect.
            return null;
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    /// <summary>
    /// True when <paramref name="peer"/> answered and its answer was written. False when it did not answer, or (for a job, where
    /// <paramref name="missingIsFinal"/> is false) answered 404 — the caller then tries elsewhere or serves it here.
    /// </summary>
    private async Task<bool> TryForwardAsync(HttpContext context, ClusterPeer peer, bool rewindable, bool missingIsFinal)
    {
        var source = context.Request;
        var target = new Uri(peer.Url, source.Path.Value!.TrimStart('/') + source.QueryString.Value);
        using var request = new HttpRequestMessage(new HttpMethod(source.Method), target);

        if (rewindable)
        {
            // A copy, not the body itself: StreamContent disposes what it wraps, and a member that does not answer leaves this
            // one to serve the request from that same body.
            using var copy = new MemoryStream();
            source.Body.Position = 0;
            await source.Body.CopyToAsync(copy, context.RequestAborted);
            source.Body.Position = 0;
            request.Content = new ByteArrayContent(copy.ToArray());
        }

        foreach (var header in source.Headers)
        {
            if (HopByHop.Contains(header.Key))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string?>)header.Value))
            {
                request.Content?.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string?>)header.Value);
            }
        }

        request.Headers.TryAddWithoutValidation(ForwardedByHeader, membership.InstanceId);

        if (context.Connection.RemoteIpAddress is { } remote)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", remote.ToString());
        }

        HttpResponseMessage response;

        try
        {
            response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            peers.Count(peer.Instance, "unreachable");
            logger.LogWarning(ex, "Cluster member {Peer} at {Url} did not answer a passed-on {Path}; serving it here", peer.Instance, peer.Url, source.Path);
            return false;
        }

        using (response)
        {
            if (!missingIsFinal && response.StatusCode is System.Net.HttpStatusCode.NotFound)
            {
                peers.Count(peer.Instance, "missing");
                return false;
            }

            peers.Count(peer.Instance, "served");
            context.Response.StatusCode = (int)response.StatusCode;

            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (!HopByHop.Contains(header.Key) || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }
            }

            context.Response.Headers[ForwardedToHeader] = peer.Instance;
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            await body.CopyToAsync(context.Response.Body, context.RequestAborted);
            return true;
        }
    }
}
