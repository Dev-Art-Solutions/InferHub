using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InferHub.Desktop;

/// <summary>A node the app talks to: its local API and the control token (phase 112 D1).</summary>
public sealed record NodeEndpoint(Uri BaseUrl, string Token, string Source);

/// <summary>One server-sent event: its id, its type and its JSON.</summary>
public sealed record ServerEvent(long Id, string Type, JsonNode? Data);

/// <summary>What a call to the node answered when it did not succeed, in the node's own sentence.</summary>
public sealed class NodeControlException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The control API's client (phase 115 D1) — the app's only way to a node, whether attached or its own. Replies are
/// returned as JSON so the app shows what a node says without a second copy of every contract to keep in step.
/// </summary>
public sealed class NodeControlClient : IDisposable
{
    private readonly HttpClient http;
    private readonly HttpClient streams;

    public NodeControlClient(NodeEndpoint endpoint, HttpMessageHandler? handler = null)
    {
        Endpoint = endpoint;
        http = Build(handler, TimeSpan.FromSeconds(30));
        streams = Build(handler, Timeout.InfiniteTimeSpan);
    }

    public NodeEndpoint Endpoint { get; }

    private HttpClient Build(HttpMessageHandler? handler, TimeSpan timeout)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.BaseAddress = Endpoint.BaseUrl;
        client.Timeout = timeout;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Endpoint.Token);
        return client;
    }

    public Task<JsonObject> StatusAsync(CancellationToken cancellationToken = default) => GetAsync("/api/control/status", cancellationToken);

    public Task<JsonObject> ActivityAsync(CancellationToken cancellationToken = default) => GetAsync("/api/control/activity", cancellationToken);

    public Task<JsonObject> SettingsAsync(CancellationToken cancellationToken = default) => GetAsync("/api/control/settings", cancellationToken);

    public Task<JsonObject> SettingsFileAsync(CancellationToken cancellationToken = default) => GetAsync("/api/control/settings/file", cancellationToken);

    public Task<JsonObject> ModelsAsync(CancellationToken cancellationToken = default) => GetAsync("/api/control/models", cancellationToken);

    public Task<JsonObject> LogsAsync(long after = 0, int max = 500, CancellationToken cancellationToken = default) =>
        GetAsync($"/api/control/logs?after={after}&max={max}", cancellationToken);

    /// <summary>Writes settings into the node's <c>node.local.json</c>; the reply says whether a restart applies them.</summary>
    public Task<JsonObject> SaveSettingsAsync(IReadOnlyDictionary<string, JsonNode?> set, IReadOnlyList<string>? remove = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, "/api/control/settings", new JsonObject
        {
            ["set"] = new JsonObject(set.Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))),
            ["remove"] = new JsonArray([.. (remove ?? []).Select(r => (JsonNode)JsonValue.Create(r)!)])
        }, cancellationToken);

    public Task<JsonObject> SaveSettingsFileAsync(JsonObject file, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, "/api/control/settings/file", file, cancellationToken);

    public Task<JsonObject> SetSharingAsync(string mode, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, "/api/control/sharing", new JsonObject { ["mode"] = mode }, cancellationToken);

    /// <summary>Phase 116: the sharing hours (empty clears them), their time zone (empty: the node's machine) and the daily allowance (0: none).</summary>
    public Task<JsonObject> SetSharingTermsAsync(IReadOnlyList<string> hours, string? timeZone, int dailyNetworkMinutes, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, "/api/control/sharing", new JsonObject
        {
            ["hours"] = new JsonArray([.. hours.Select(h => (JsonNode)JsonValue.Create(h)!)]),
            ["timeZone"] = timeZone ?? "",
            ["dailyNetworkMinutes"] = dailyNetworkMinutes
        }, cancellationToken);

    /// <summary>Phase 117: which models the network may use — both lists sent, an empty one cleared.</summary>
    public Task<JsonObject> SetSharedModelsAsync(IReadOnlyList<string> include, IReadOnlyList<string> exclude, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, "/api/control/sharing", new JsonObject
        {
            ["models"] = new JsonObject
            {
                ["include"] = new JsonArray([.. include.Select(n => (JsonNode)JsonValue.Create(n)!)]),
                ["exclude"] = new JsonArray([.. exclude.Select(n => (JsonNode)JsonValue.Create(n)!)])
            }
        }, cancellationToken);

    public Task<JsonObject> RestartAsync(bool force = false, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/api/control/restart{(force ? "?force=true" : "")}", null, cancellationToken);

    /// <summary><c>pull</c>, <c>warm</c>, <c>unload</c> or <c>delete</c>; progress arrives on the event stream.</summary>
    public Task<JsonObject> ModelActionAsync(string model, string action, string? engine = null, CancellationToken cancellationToken = default)
    {
        var query = engine is null ? "" : $"?engine={Uri.EscapeDataString(engine)}";
        var path = $"/api/control/models/{Uri.EscapeDataString(model)}";

        return action == "delete"
            ? SendAsync(HttpMethod.Delete, path + query, null, cancellationToken)
            : SendAsync(HttpMethod.Post, $"{path}/{action}{query}", null, cancellationToken);
    }

    public Task<JsonObject> EngineAsync(string engine, bool start, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/api/control/engines/{Uri.EscapeDataString(engine)}/{(start ? "start" : "stop")}", null, cancellationToken);

    public Task<JsonObject> CatalogAsync(string engine, string model, bool load, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/api/control/catalog/{engine}/{Uri.EscapeDataString(model)}/{(load ? "load" : "unload")}", null, cancellationToken);

    /// <summary>Phase 118: which of the node's engines can run a model at a context, with the arithmetic (a 3.79 node).</summary>
    public Task<JsonObject> FitAsync(string link, int? context, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "/api/control/fit", new JsonObject { ["link"] = link, ["context"] = context }, cancellationToken);

    /// <summary>Phase 118: installs the option chosen — the node's own pull, its progress on the event stream.</summary>
    public Task<JsonObject> FitInstallAsync(string link, string engine, string? variant, int? context, bool force = false, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "/api/control/fit/install", new JsonObject
        {
            ["link"] = link,
            ["engine"] = engine,
            ["variant"] = variant,
            ["context"] = context,
            ["force"] = force
        }, cancellationToken);

    /// <summary>Phase 119: what is trending and popular on Hugging Face and in Ollama's library, fitting first (a 3.80 node).</summary>
    public Task<JsonObject> CatalogAsync(string source, string sort, string kind, string? search, bool fitsOnly, CancellationToken cancellationToken = default)
    {
        var query = $"?source={Uri.EscapeDataString(source)}&sort={Uri.EscapeDataString(sort)}&kind={Uri.EscapeDataString(kind)}&fitsOnly={(fitsOnly ? "true" : "false")}"
                    + (string.IsNullOrWhiteSpace(search) ? "" : "&q=" + Uri.EscapeDataString(search.Trim()));
        return SendAsync(HttpMethod.Get, "/api/control/fit/catalog" + query, null, cancellationToken);
    }

    /// <summary>The node's events as they happen — jobs, sharing, settings, model progress — until cancelled.</summary>
    public IAsyncEnumerable<ServerEvent> EventsAsync(long after = 0, CancellationToken cancellationToken = default) =>
        StreamAsync($"/api/control/events?after={after}", cancellationToken);

    public IAsyncEnumerable<ServerEvent> LogStreamAsync(long after = 0, CancellationToken cancellationToken = default) =>
        StreamAsync($"/api/control/logs/stream?after={after}", cancellationToken);

    /// <summary>A chat through the node's own OpenAI surface (<c>/v1/chat/completions</c>), for the Chat tab.</summary>
    public async Task<string> ChatAsync(string model, string prompt, string? apiKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new { model, messages = new[] { new { role = "user", content = prompt } }, stream = false })
        };

        // The local API's key, not the control token (112 D1): one does not open the other.
        request.Headers.Authorization = apiKey is { Length: > 0 } ? new AuthenticationHeaderValue("Bearer", apiKey) : null;
        using var response = await streams.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new NodeControlException((int)response.StatusCode, ErrorMessage(body) ?? $"the node answered {(int)response.StatusCode}");
        }

        return JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
    }

    private async Task<JsonObject> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(path, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request, cancellationToken);
        return await ReadAsync(response, cancellationToken);
    }

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new NodeControlException((int)response.StatusCode, ErrorMessage(text) ?? $"the node answered {(int)response.StatusCode}");
        }

        return string.IsNullOrWhiteSpace(text) ? [] : JsonNode.Parse(text) as JsonObject ?? [];
    }

    /// <summary>The node's sentence from <c>{"error": {"message": …, "failures": […]}}</c>.</summary>
    internal static string? ErrorMessage(string body)
    {
        try
        {
            var error = JsonNode.Parse(body)?["error"];
            var message = error?["message"]?.GetValue<string>();
            var failures = error?["failures"] is JsonArray list ? string.Join("; ", list.Select(f => f?.GetValue<string>())) : null;
            return failures is { Length: > 0 } ? $"{message}: {failures}" : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async IAsyncEnumerable<ServerEvent> StreamAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await streams.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new NodeControlException((int)response.StatusCode, ErrorMessage(await response.Content.ReadAsStringAsync(cancellationToken)) ?? "the node refused the stream");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        await foreach (var item in ReadEventsAsync(stream, cancellationToken))
        {
            yield return item;
        }
    }

    /// <summary>Parses <c>text/event-stream</c>: <c>id:</c>, <c>event:</c>, <c>data:</c>, a blank line ends one; comments skipped.</summary>
    public static async IAsyncEnumerable<ServerEvent> ReadEventsAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        long id = 0;
        string type = "message";
        var data = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    JsonNode? parsed;

                    try
                    {
                        parsed = JsonNode.Parse(data.ToString());
                    }
                    catch (JsonException)
                    {
                        parsed = JsonValue.Create(data.ToString());
                    }

                    yield return new ServerEvent(id, type, parsed);
                }

                type = "message";
                data.Clear();
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');

            switch (field)
            {
                case "id":
                    long.TryParse(value, out id);
                    break;
                case "event":
                    type = value;
                    break;
                case "data":
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    break;
            }
        }
    }

    public void Dispose()
    {
        http.Dispose();
        streams.Dispose();
    }
}
