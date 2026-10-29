using InferHub.Coordinator.Auth;
using InferHub.Shared.Contracts;
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

        if (Target(relay, upstream, path, context.Request.QueryString.Value) is not { } target)
        {
            await Refuse(context, StatusCodes.Status404NotFound,
                $"'{upstream}' is not an upstream this relay knows; it knows {string.Join(", ", NodeRelay.Upstreams)}");
            return;
        }

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);

        foreach (var name in ForwardedRequestHeaders)
        {
            if (context.Request.Headers.TryGetValue(name, out var value))
            {
                request.Headers.TryAddWithoutValidation(name, value.ToArray());
            }
        }

        HttpResponseMessage response;

        try
        {
            response = await factory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

            long sent = 0;

            if (!HttpMethods.IsHead(context.Request.Method))
            {
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[81920];
                int read;

                while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    sent += read;
                }
            }

            // The path names a model, never a prompt; the query is dropped — a resolve carries a revision and nothing else,
            // but a signed URL is the kind of thing that ends up in one.
            logger.LogInformation(
                "Relayed {Method} {Upstream}/{Path} for {RemoteIp}: {Status}, {Bytes} bytes",
                context.Request.Method, upstream, path, context.Connection.RemoteIpAddress, (int)response.StatusCode, sent);
        }
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
