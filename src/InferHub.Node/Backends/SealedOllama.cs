using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Backends;

/// <summary>
/// Phase 122. The Ollama half of <c>Node:Sealed</c>: its cloud models are not this node's (122 D5), and a pull goes
/// through the coordinator rather than to registry.ollama.ai (122 D6).
/// </summary>
/// <remarks>
/// <para>
/// <b>A pull is still Ollama's own.</b> Ollama will pull from any registry named in the model —
/// <c>127.0.0.1:port/library/qwen3:8b</c> with <c>insecure</c> — so the node opens a registry for the length of one
/// pull, on loopback, that answers from the hub's relay; then copies the result to the name that was asked for and
/// deletes the loopback one. The blobs are shared, so the copy is a manifest. <b>Considered and rejected:</b> the node
/// downloading the blobs and assembling the model through <c>/api/blobs</c> + <c>/api/create</c> — a second
/// implementation of Ollama's manifest format, wrong the first time a new layer type ships.
/// </para>
/// <para>
/// <b>Found by running it (Ollama 0.40):</b> a blob GET must answer with a redirect to a <em>different host</em>, or
/// Ollama follows it and fails with "no Location header". The registry is <c>127.0.0.1</c> and the redirect is to
/// <c>localhost</c> — the same listener under a second name, which is why there is an IPv6 one too: on Windows
/// Ollama dials <c>localhost</c> as <c>::1</c> only.
/// </para>
/// </remarks>
public sealed class SealedOllama(SealedEgress egress, IHttpClientFactory http, ILogger<SealedOllama> logger)
{
    public const string RegistryClientName = "InferHub.SealedRegistry";

    private const string DefaultRegistry = "registry.ollama.ai";

    private volatile HashSet<string> cloud = new(StringComparer.OrdinalIgnoreCase);
    private volatile HashSet<string> local = new(StringComparer.OrdinalIgnoreCase);
    private int cloudOn = -1;

    /// <summary>Ollama lists a cloud model with a <c>remote_host</c>; those are dropped and remembered.</summary>
    public IReadOnlyList<ModelInfo> KeepLocal(JsonElement tags)
    {
        var kept = new List<ModelInfo>();
        var remote = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var here = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (tags.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in models.EnumerateArray())
            {
                var name = model.TryGetProperty("name", out var n) ? n.GetString() : null;

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (IsRemote(model))
                {
                    remote.Add(OllamaBackend.NormalizeModelName(name));
                    continue;
                }

                here.Add(OllamaBackend.NormalizeModelName(name));
                kept.Add(new ModelInfo(
                    name,
                    model.TryGetProperty("digest", out var d) ? d.GetString() ?? string.Empty : string.Empty,
                    model.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0));
            }
        }

        cloud = remote;
        local = here;
        return kept;
    }

    private static bool IsRemote(JsonElement model)
        => (model.TryGetProperty("remote_host", out var host) && host.ValueKind == JsonValueKind.String && host.GetString()!.Length > 0)
           || (model.TryGetProperty("remote_model", out var remote) && remote.ValueKind == JsonValueKind.String && remote.GetString()!.Length > 0);

    /// <summary>
    /// Ollama's <c>/api/status</c> says whether its cloud is off. Said once per change: an Ollama this node did not
    /// start (a service, the desktop app) does not inherit <c>OLLAMA_NO_CLOUD</c>, and its owner should know.
    /// </summary>
    public void ObserveStatus(JsonElement status)
    {
        if (!status.TryGetProperty("cloud", out var block) || !block.TryGetProperty("disabled", out var disabled))
        {
            return;
        }

        var on = disabled.ValueKind == JsonValueKind.False ? 1 : 0;

        if (Interlocked.Exchange(ref cloudOn, on) != on && on == 1)
        {
            logger.LogError(
                "Node:Sealed, and the Ollama this node drives has its cloud ON — set OLLAMA_NO_CLOUD=1 where that Ollama runs. Until then its cloud models are not listed and are refused here.");
        }
    }

    /// <summary>A request for a cloud model is refused before Ollama sees it (122 D5).</summary>
    public void EnsureLocal(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        var name = OllamaBackend.NormalizeModelName(model);

        if (cloud.Contains(name) || (!local.Contains(name) && name.EndsWith("cloud", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"'{model}' is an Ollama cloud model, which runs on ollama.com; Node:Sealed serves only models on this machine");
        }
    }

    /// <summary>
    /// <c>qwen3:8b</c> → (<c>library</c>, <c>qwen3</c>, <c>8b</c>); <c>owner/model</c> keeps its owner. Another
    /// registry (<c>hf.co/…</c>) is refused: the relay knows Ollama's registry, not every host a name can carry.
    /// </summary>
    internal static (string Namespace, string Model, string Tag) ParseName(string model)
    {
        var name = (model ?? string.Empty).Trim();
        var parts = name.Split('/');

        if (parts.Length == 3)
        {
            if (!parts[0].Equals(DefaultRegistry, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"'{model}' is pulled from {parts[0]}; a sealed node pulls through its coordinator from {DefaultRegistry} only");
            }

            parts = parts[1..];
        }

        if (parts.Length is < 1 or > 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"'{model}' is not an Ollama model name");
        }

        var (repo, tag) = parts[^1].Split(':', 2) is [var r, var t] ? (r, t) : (parts[^1], "latest");

        if (tag.EndsWith("cloud", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"'{model}' is an Ollama cloud model; Node:Sealed serves only models on this machine");
        }

        return (parts.Length == 2 ? parts[0] : "library", repo, tag);
    }

    /// <summary>The pull, through a loopback registry that answers from the relay; then the model under its own name.</summary>
    public async IAsyncEnumerable<ModelPullProgress> PullAsync(
        string model,
        Uri ollama,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (egress.RelayBase(NodeRelay.OllamaRegistry) is not { } relay)
        {
            throw new InvalidOperationException(
                $"Node:Sealed, and this node has no coordinator to pull '{model}' through; pull it where the internet is and copy it here, or join a coordinator with Relay:Enabled");
        }

        if (!ollama.IsLoopback)
        {
            throw new InvalidOperationException(
                $"Node:Sealed pulls through a registry on this machine's loopback, which the Ollama at {ollama.Authority} cannot reach; pull on that machine");
        }

        var (ns, repo, tag) = ParseName(model);
        await using var registry = LoopbackRegistry.Start(() => http.CreateClient(RegistryClientName), relay, logger);
        var shimName = $"127.0.0.1:{registry.Port}/{ns}/{repo}:{tag}";
        var target = model.Trim();
        using var client = http.CreateClient(nameof(OllamaClient.OllamaHttpClient));

        logger.LogInformation("Pulling '{Model}' through the coordinator (Node:Sealed)", target);

        using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ollama, "api/pull"))
               {
                   Content = JsonContent.Create(new { model = shimName, insecure = true, stream = true })
               })
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Ollama refused the pull: {await response.Content.ReadAsStringAsync(cancellationToken)}");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(body);

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                using var frame = JsonDocument.Parse(line);
                var root = frame.RootElement;

                if (root.TryGetProperty("error", out var error))
                {
                    throw new InvalidOperationException(Readable(error.GetString(), registry.Port));
                }

                var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;

                if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return new ModelPullProgress(
                    status ?? string.Empty,
                    root.TryGetProperty("total", out var t) && t.TryGetInt64(out var total) ? total : null,
                    root.TryGetProperty("completed", out var c) && c.TryGetInt64(out var completed) ? completed : null);
            }
        }

        yield return new ModelPullProgress($"naming it {target}", null, null);

        using (var copy = await client.PostAsJsonAsync(new Uri(ollama, "api/copy"), new { source = shimName, destination = target }, cancellationToken))
        {
            if (!copy.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"the pull landed as '{shimName}' and Ollama would not copy it to '{target}': {await copy.Content.ReadAsStringAsync(cancellationToken)}");
            }
        }

        using (var delete = new HttpRequestMessage(HttpMethod.Delete, new Uri(ollama, "api/delete")) { Content = JsonContent.Create(new { model = shimName }) })
        using (var deleted = await client.SendAsync(delete, cancellationToken))
        {
            if (!deleted.IsSuccessStatusCode)
            {
                logger.LogWarning("Pulled '{Model}', and the loopback name '{Shim}' could not be removed; delete it in Ollama", target, shimName);
            }
        }

        yield return new ModelPullProgress("success", null, null);
    }

    private static string Readable(string? error, int port)
        => (error ?? "the pull failed").Replace($"127.0.0.1:{port}/", string.Empty, StringComparison.Ordinal);
}

/// <summary>
/// A registry on loopback for one pull: the manifest and the blobs, from the hub's relay. Plain sockets rather than
/// <c>HttpListener</c> (an http.sys URL reservation on Windows) or Kestrel (rule 2 keeps ASP.NET in <c>LocalApi/</c>):
/// what Ollama asks is a GET or a HEAD with a <c>Range</c>, and every answer closes the connection.
/// </summary>
internal sealed class LoopbackRegistry : IAsyncDisposable
{
    private const string Followed = "/relayed";

    private readonly TcpListener v4;
    private readonly TcpListener? v6;
    private readonly Func<HttpClient> client;
    private readonly Uri relay;
    private readonly ILogger logger;
    private readonly CancellationTokenSource stop = new();
    private readonly List<Task> loops = [];

    private LoopbackRegistry(TcpListener v4, TcpListener? v6, Func<HttpClient> client, Uri relay, ILogger logger)
    {
        this.v4 = v4;
        this.v6 = v6;
        this.client = client;
        this.relay = relay;
        this.logger = logger;
        Port = ((IPEndPoint)v4.LocalEndpoint).Port;
    }

    public int Port { get; }

    public static LoopbackRegistry Start(Func<HttpClient> client, Uri relay, ILogger logger)
    {
        var v4 = new TcpListener(IPAddress.Loopback, 0);
        v4.Start();
        var port = ((IPEndPoint)v4.LocalEndpoint).Port;
        TcpListener? v6 = null;

        try
        {
            v6 = new TcpListener(IPAddress.IPv6Loopback, port);
            v6.Start();
        }
        catch (SocketException)
        {
            // No IPv6 loopback (a container with it switched off): "localhost" is 127.0.0.1 there.
            v6 = null;
        }

        var registry = new LoopbackRegistry(v4, v6, client, relay, logger);
        registry.loops.Add(registry.AcceptAsync(v4));

        if (v6 is not null)
        {
            registry.loops.Add(registry.AcceptAsync(v6));
        }

        return registry;
    }

    private async Task AcceptAsync(TcpListener listener)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient socket;

            try
            {
                socket = await listener.AcceptTcpClientAsync(stop.Token);
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(socket));
        }
    }

    private async Task ServeAsync(TcpClient socket)
    {
        using (socket)
        {
            try
            {
                await using var stream = socket.GetStream();
                var head = await ReadHeadAsync(stream, stop.Token);

                if (head is null)
                {
                    return;
                }

                await AnswerAsync(stream, head.Value, stop.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or HttpRequestException)
            {
                logger.LogDebug("Loopback registry: {Reason}", ex.Message);
            }
        }
    }

    private async Task AnswerAsync(NetworkStream stream, (string Method, string Path, Dictionary<string, string> Headers) request, CancellationToken cancellationToken)
    {
        var (method, path, headers) = request;
        var head = method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

        if (!head && !method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            await WriteHeadAsync(stream, 405, "Method Not Allowed", [], 0, cancellationToken);
            return;
        }

        // The cross-host redirect Ollama insists on for a blob (see SealedOllama's remarks).
        if (!head && path.Contains("/blobs/", StringComparison.Ordinal) && !path.StartsWith(Followed + "/", StringComparison.Ordinal))
        {
            await WriteHeadAsync(stream, 307, "Temporary Redirect", [("Location", $"http://localhost:{Port}{Followed}{path}")], 0, cancellationToken);
            return;
        }

        if (path.StartsWith(Followed + "/", StringComparison.Ordinal))
        {
            path = path[Followed.Length..];
        }

        using var forward = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, new Uri(relay, path.TrimStart('/')));

        foreach (var name in new[] { "Range", "Accept" })
        {
            if (headers.TryGetValue(name, out var value))
            {
                forward.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var answer = await client().SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var passed = new List<(string, string)>();

        foreach (var name in new[] { "Content-Range", "Accept-Ranges", "Docker-Content-Digest", "ETag" })
        {
            if (answer.Headers.TryGetValues(name, out var values) || answer.Content.Headers.TryGetValues(name, out values))
            {
                passed.Add((name, string.Join(", ", values)));
            }
        }

        if (answer.Content.Headers.ContentType is { } type)
        {
            passed.Add(("Content-Type", type.ToString()));
        }

        await WriteHeadAsync(stream, (int)answer.StatusCode, answer.ReasonPhrase ?? "OK", passed, answer.Content.Headers.ContentLength, cancellationToken);

        if (!head)
        {
            await using var body = await answer.Content.ReadAsStreamAsync(cancellationToken);
            await body.CopyToAsync(stream, cancellationToken);
        }
    }

    private static async Task WriteHeadAsync(
        NetworkStream stream, int status, string reason, IReadOnlyList<(string Name, string Value)> headers, long? length, CancellationToken cancellationToken)
    {
        var text = new StringBuilder($"HTTP/1.1 {status} {reason}\r\n");

        foreach (var (name, value) in headers)
        {
            text.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        if (length is { } bytes)
        {
            text.Append("Content-Length: ").Append(bytes).Append("\r\n");
        }

        text.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text.ToString()), cancellationToken);
    }

    internal static async Task<(string Method, string Path, Dictionary<string, string> Headers)?> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var filled = 0;

        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken);

            if (read == 0)
            {
                return null;
            }

            filled += read;
            var text = Encoding.ASCII.GetString(buffer, 0, filled);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);

            if (end < 0)
            {
                continue;
            }

            var lines = text[..end].Split("\r\n");
            var first = lines[0].Split(' ');

            if (first.Length < 2)
            {
                return null;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');

                if (colon > 0)
                {
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
            }

            return (first[0], first[1], headers);
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        v4.Stop();
        v6?.Stop();

        try
        {
            await Task.WhenAll(loops);
        }
        catch (Exception)
        {
            // Stopping a listener faults its accept; nothing is waiting on that.
        }

        stop.Dispose();
    }
}
