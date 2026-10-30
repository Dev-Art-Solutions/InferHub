using InferHub.Coordinator.Auth;
using InferHub.Shared.Contracts;
using System.Net;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Phase 122. <c>Relay:</c> — what a sealed node (<c>Node:Sealed</c>) would have fetched itself, fetched by its hub.
/// Off unless <see cref="Enabled"/>: turning a coordinator into anybody's download path is the owner's decision.
/// </summary>
public sealed class RelayOptions
{
    public const string SectionName = "Relay";

    public bool Enabled { get; set; }

    /// <summary>Hugging Face, or a mirror that speaks its API.</summary>
    public string HuggingFace { get; set; } = "https://huggingface.co";

    /// <summary>The registry an Ollama pulls <c>library/…</c> and <c>owner/…</c> from.</summary>
    public string OllamaRegistry { get; set; } = "https://registry.ollama.ai";

    /// <summary>Ollama's library pages, which the node's catalogue reads (119 D3).</summary>
    public string OllamaLibrary { get; set; } = "https://ollama.com";

    /// <summary>Phase 123: the blobs this hub relayed, kept by sha256. Off by default.</summary>
    public RelayCacheOptions Cache { get; set; } = new();

    /// <summary>Phase 124: models fetched or imported once, answered without the upstream. Off by default.</summary>
    public RelaySupplyOptions Supply { get; set; } = new();

    public string? BaseFor(string upstream) => upstream switch
    {
        NodeRelay.HuggingFace => HuggingFace,
        NodeRelay.OllamaRegistry => OllamaRegistry,
        NodeRelay.OllamaLibrary => OllamaLibrary,
        _ => null
    };
}

/// <summary>
/// <c>GET|HEAD /node/relay/{upstream}/{path}</c>, behind the node enrollment secret (122 D3). The hub follows
/// redirects itself — a Hugging Face resolve and a registry blob both answer with a signed CDN URL, and handing
/// that back would send the node to the CDN, which is the door being closed — and streams the body as it comes,
/// <c>Range</c> included, because an Ollama pull asks for a 20 GB blob in parts.
/// </summary>
public static class NodeRelayEndpoints
{
    public const string HttpClientName = "InferHub.NodeRelay";

    private static readonly string[] ForwardedRequestHeaders = ["Range", "Accept", "Authorization", "If-Range"];

    private static readonly string[] ForwardedResponseHeaders =
    [
        "Content-Range", "Accept-Ranges", "ETag", "Last-Modified", "Docker-Content-Digest",
        "X-Repo-Commit", "X-Linked-Size", "X-Linked-Etag", "X-Error-Code", "X-Error-Message", "Link"
    ];

    public static void MapNodeRelay(this IEndpointRouteBuilder app)
    {
        app.MapMethods(NodeRelay.PathPrefix + "/{upstream}/{**path}", [HttpMethods.Get, HttpMethods.Head], RelayAsync);
    }

    internal static async Task RelayAsync(
        string upstream,
        string? path,
        HttpContext context,
        IOptionsMonitor<RelayOptions> options,
        NodeAuthFilter auth,
        IHttpClientFactory factory,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var logger = loggers.CreateLogger("InferHub.Coordinator.NodeRelay");
        var relay = options.CurrentValue;

        if (!auth.IsAuthorized(context))
        {
            await Refuse(context, StatusCodes.Status401Unauthorized, "the relay is for this coordinator's nodes; present X-Node-Enrollment-Secret");
            return;
        }

        if (!relay.Enabled)
        {
            await Refuse(context, StatusCodes.Status403Forbidden,
                "this coordinator does not fetch for its nodes (Relay:Enabled=false); a sealed node downloads nothing until it does");
            return;
        }

        // 124 D1/D4: what the admin supplied is answered from the hub's disk, and under Only nothing else is answered.
        if (context.RequestServices.GetService<RelaySupply>() is { Enabled: true } supply)
        {
            if (supply.Find(upstream, path) is { } answer && await ServeSuppliedAsync(context, supply, answer, cancellationToken) is { } bytes)
            {
                logger.LogInformation(
                    "Relayed {Method} {Upstream}/{Path} for {RemoteIp} from the supply: {Status}, {Bytes} bytes",
                    context.Request.Method, upstream, path, context.Connection.RemoteIpAddress, context.Response.StatusCode, bytes);
                return;
            }

            if (supply.Only)
            {
                supply.Refused();
                context.Response.Headers[NodeRelay.SupplyHeader] = NodeRelay.NotSupplied;
                logger.LogInformation("Refused {Upstream}/{Path} for {RemoteIp}: not supplied (Relay:Supply:Only)", upstream, path, context.Connection.RemoteIpAddress);
                await Refuse(context, StatusCodes.Status404NotFound,
                    $"'{upstream}/{(path ?? string.Empty).TrimStart('/')}' is not supplied by this coordinator, and Relay:Supply:Only gives its nodes nothing else; "
                    + "an admin supplies a model with POST /api/admin/supply, or imports one with POST /api/admin/supply/import");
                return;
            }
        }

        if (Target(relay, upstream, path, context.Request.QueryString.Value) is not { } target)
        {
            await Refuse(context, StatusCodes.Status404NotFound,
                $"'{upstream}' is not an upstream this relay knows; it knows {string.Join(", ", NodeRelay.Upstreams)}");
            return;
        }

        var client = factory.CreateClient(HttpClientName);
        var method = new HttpMethod(context.Request.Method);
        var forwarded = ForwardedRequestHeaders
            .Where(name => context.Request.Headers.ContainsKey(name))
            .Select(name => (Name: name, Values: context.Request.Headers[name].Select(value => value ?? string.Empty).ToArray()))
            .ToArray();

        // Resolved rather than injected, so a host that maps the relay without the cache keeps 3.82's relay exactly.
        var cache = context.RequestServices.GetService<RelayCache>() is { Enabled: true } enabled && !HttpMethods.IsHead(context.Request.Method)
            ? enabled
            : null;

        HttpResponseMessage response;
        string? digest = null;

        try
        {
            // 123 D1: the first hop is always the upstream's. It decides access (a gated repo, a node's token) and
            // names the content; only the body behind it can come from the hub's disk.
            var first = await client.SendAsync(Request(method, target, forwarded, target), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            digest = cache is null ? null : DigestOf(upstream, path, first);

            if (digest is not null && (int)first.StatusCode is >= 200 and < 400 && cache!.Open(digest) is { } blob)
            {
                first.Dispose();

                await using (blob.Stream)
                {
                    var (status, bytes) = await ServeCachedAsync(context, blob, upstream, cancellationToken);
                    cache.Served(bytes);
                    logger.LogInformation(
                        "Relayed {Method} {Upstream}/{Path} for {RemoteIp} from the cache: {Status}, {Bytes} bytes",
                        context.Request.Method, upstream, path, context.Connection.RemoteIpAddress, status, bytes);
                }

                return;
            }

            response = await FollowAsync(client, first, method, target, forwarded, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Relay to {Upstream} failed: {Reason}", upstream, ex.Message);
            await Refuse(context, StatusCodes.Status502BadGateway, $"the coordinator could not reach {upstream}: {ex.Message}");
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;

            foreach (var name in ForwardedResponseHeaders)
            {
                if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                {
                    context.Response.Headers[name] = values.ToArray();
                }
            }

            if (response.Content.Headers.ContentType is { } contentType)
            {
                context.Response.ContentType = contentType.ToString();
            }

            if (response.Content.Headers.ContentLength is { } length)
            {
                context.Response.ContentLength = length;
            }

            // 123 D3: a whole blob is kept as it passes; a part is relayed as before and the hub fetches the whole
            // blob once, beside it — never making this node wait for twenty gigabytes before its first range.
            using var fill = digest is not null && response.StatusCode == HttpStatusCode.OK
                ? cache!.Begin(digest, response.Content.Headers.ContentLength)
                : null;

            if (digest is not null)
            {
                context.Response.Headers[NodeRelay.CacheHeader] = "miss";

                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var whole = forwarded
                        .Where(header => !header.Name.Equals("Range", StringComparison.OrdinalIgnoreCase)
                            && !header.Name.Equals("If-Range", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    cache!.FillInBackground(digest, async token =>
                    {
                        var hop = await client.SendAsync(Request(HttpMethod.Get, target, whole, target), HttpCompletionOption.ResponseHeadersRead, token);
                        return await FollowAsync(client, hop, HttpMethod.Get, target, whole, token);
                    });
                }
            }

            long sent = 0;

            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[81920];
                int read;

                while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    fill?.Write(buffer.AsSpan(0, read));
                    sent += read;
                }

                fill?.Complete();
            }

            // The path names a model, never a prompt; the query is dropped — a resolve carries a revision and nothing else,
            // but a signed URL is the kind of thing that ends up in one.
            logger.LogInformation(
                "Relayed {Method} {Upstream}/{Path} for {RemoteIp}: {Status}, {Bytes} bytes",
                context.Request.Method, upstream, path, context.Connection.RemoteIpAddress, (int)response.StatusCode, sent);
        }
    }

    /// <summary>
    /// The relay's client and its cache. Redirects are followed here rather than by the handler: the first hop is where
    /// Hugging Face names a file's sha256 (<c>X-Linked-Etag</c>), and the handler would follow past it to a CDN that
    /// does not (123 D1). No loggers: a resolve redirects to a signed CDN URL, and the factory would log it.
    /// </summary>
    public static IServiceCollection AddNodeRelay(this IServiceCollection services)
    {
        services.AddSingleton<RelayCache>();
        services.AddSingleton<RelaySupply>();
        services.AddSingleton<SupplyJobs>();
        services.AddHttpClient(HttpClientName, http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        return services;
    }

    /// <summary>
    /// The sha256 the upstream itself named for what this path answers (123 D1): a registry blob is addressed by its
    /// digest; a Hugging Face LFS file's first hop carries it as <c>X-Linked-Etag</c>. Anything else is not kept.
    /// </summary>
    internal static string? DigestOf(string upstream, string? path, HttpResponseMessage first)
    {
        var relative = (path ?? string.Empty).TrimStart('/');

        if (upstream == NodeRelay.OllamaRegistry)
        {
            var at = relative.LastIndexOf("/blobs/", StringComparison.Ordinal);
            return relative.StartsWith("v2/", StringComparison.Ordinal) && at > 0
                ? RelayCache.Sha256Of(relative[(at + "/blobs/".Length)..])
                : null;
        }

        if (upstream == NodeRelay.HuggingFace && relative.Contains("/resolve/", StringComparison.Ordinal)
            && first.Headers.TryGetValues("X-Linked-Etag", out var linked))
        {
            return RelayCache.Sha256Of(linked.FirstOrDefault());
        }

        return null;
    }

    internal static HttpRequestMessage Request(HttpMethod method, Uri uri, (string Name, string[] Values)[] headers, Uri origin)
    {
        var request = new HttpRequestMessage(method, uri);

        foreach (var (name, values) in headers)
        {
            // .NET's own redirect handling dropped Authorization off the upstream's host, and 3.82's relay relied on it:
            // the node's Hugging Face token reaches Hugging Face, never the CDN (122 D3). Kept by hand.
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Authority, origin.Authority, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(name, values);
        }

        return request;
    }

    internal static async Task<HttpResponseMessage> FollowAsync(
        HttpClient client, HttpResponseMessage response, HttpMethod method, Uri origin, (string Name, string[] Values)[] headers, CancellationToken cancellationToken)
    {
        var at = response.RequestMessage?.RequestUri ?? origin;

        for (var hop = 0; hop < 10 && (int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location; hop++)
        {
            var next = location.IsAbsoluteUri ? location : new Uri(at, location);

            if (next.Scheme is not ("http" or "https"))
            {
                break;
            }

            response.Dispose();
            response = await client.SendAsync(Request(method, next, headers, origin), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            at = next;
        }

        return response;
    }

    /// <summary>The kept blob, as the CDN would have answered: a single <c>Range</c> honoured, anything else whole.</summary>
    private static async Task<(int Status, long Bytes)> ServeCachedAsync(HttpContext context, CachedBlob blob, string upstream, CancellationToken cancellationToken)
    {
        context.Response.Headers[NodeRelay.CacheHeader] = "hit";

        if (upstream == NodeRelay.OllamaRegistry)
        {
            context.Response.Headers["Docker-Content-Digest"] = "sha256:" + blob.Sha256;
        }

        return await ServeFileAsync(context, blob.Stream, blob.Sha256, "application/octet-stream", cancellationToken);
    }

    /// <summary>
    /// A supplied answer (124 D3): a blob as the CDN would have served it, or the replayed JSON. Null when the blob is
    /// gone from the disk — then it is not supplied, which under <c>Only</c> is a refusal rather than a fetch.
    /// </summary>
    private static async Task<long?> ServeSuppliedAsync(HttpContext context, RelaySupply supply, SupplyAnswer answer, CancellationToken cancellationToken)
    {
        FileStream? stream = null;

        if (answer.Sha256 is { } sha256 && (stream = supply.OpenBlob(sha256)) is null)
        {
            return null;
        }

        context.Response.Headers[NodeRelay.SupplyHeader] = NodeRelay.Supplied;

        foreach (var (name, value) in answer.Headers)
        {
            context.Response.Headers[name] = value;
        }

        long bytes;

        if (answer.Sha256 is null)
        {
            var body = answer.Body ?? [];
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = answer.ContentType;
            context.Response.ContentLength = body.Length;

            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await context.Response.Body.WriteAsync(body, cancellationToken);
            }

            bytes = HttpMethods.IsHead(context.Request.Method) ? 0 : body.Length;
        }
        else
        {
            await using (stream!)
            {
                (_, bytes) = await ServeFileAsync(context, stream!, answer.Sha256, answer.ContentType, cancellationToken);
            }
        }

        supply.Served(bytes);
        return bytes;
    }

    /// <summary>A file on the hub's disk, as the CDN would have answered: a single <c>Range</c> honoured, anything else whole.</summary>
    private static async Task<(int Status, long Bytes)> ServeFileAsync(HttpContext context, Stream file, string sha256, string contentType, CancellationToken cancellationToken)
    {
        var size = file.Length;
        long from = 0, to = size - 1;
        var status = StatusCodes.Status200OK;

        if (Microsoft.Net.Http.Headers.RangeHeaderValue.TryParse(context.Request.Headers.Range.ToString(), out var range)
            && range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) && range.Ranges.Count == 1)
        {
            var only = range.Ranges.Single();

            if (only.From is { } start)
            {
                from = start;
                to = only.To is { } end ? Math.Min(end, size - 1) : size - 1;
            }
            else if (only.To is { } suffix)
            {
                from = Math.Max(0, size - suffix);
            }

            if (from >= size || from > to)
            {
                context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                context.Response.Headers.ContentRange = $"bytes */{size}";
                return (StatusCodes.Status416RangeNotSatisfiable, 0);
            }

            status = StatusCodes.Status206PartialContent;
            context.Response.Headers.ContentRange = $"bytes {from}-{to}/{size}";
        }

        var count = to - from + 1;
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = count;
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.ETag = $"\"{sha256}\"";

        if (HttpMethods.IsHead(context.Request.Method))
        {
            return (status, 0);
        }

        file.Seek(from, SeekOrigin.Begin);
        var buffer = new byte[81920];
        var left = count;

        while (left > 0)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), cancellationToken);

            if (read == 0)
            {
                break;
            }

            await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            left -= read;
        }

        return (status, count - left);
    }

    /// <summary>The upstream's base plus the path, and nowhere else: a path cannot move the host (122 D3).</summary>
    internal static Uri? Target(RelayOptions relay, string upstream, string? path, string? query)
    {
        if (relay.BaseFor(upstream) is not { } configured
            || !Uri.TryCreate(configured.TrimEnd('/') + "/", UriKind.Absolute, out var root))
        {
            return null;
        }

        var relative = (path ?? string.Empty).TrimStart('/');

        if (!Uri.TryCreate(root, relative + (query ?? string.Empty), out var target)
            || !string.Equals(target.Scheme, root.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(target.Authority, root.Authority, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return target;
    }

    private static Task Refuse(HttpContext context, int status, string error)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error });
    }
}
