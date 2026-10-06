using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Shared.Brio;
using InferHub.Shared.Contracts;
using InferHub.Shared.LlamaCpp;

namespace InferHub.Node.Backends;

/// <summary>
/// A <c>llamacpp</c> engine (phase 96): the phase-95 <see cref="UpstreamBackend"/> for everything with
/// an Ollama shape, and llama.cpp's own server for the rest — a router's model management (D3), its
/// native routes (D4) and its reranker (D5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Chat, completion, embeddings and streaming are the inner backend's, unchanged.</b> The OpenAI
/// dialect already drives them and a second driver would be a second set of bugs; this class only
/// adds what that one cannot see, because all of it lives off <c>/v1</c> at the server's root.
/// </para>
/// <para>
/// <b>What a model is for comes from configuration</b> (D2): the router's listing says
/// <c>unloaded</c> or <c>loaded</c> and nothing about pooling, so an embedding model is one a preset
/// called one.
/// </para>
/// </remarks>
public sealed class LlamaCppBackend : IInferenceBackend, IModelKinds, IBackendToolJobs
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);

    private readonly IInferenceBackend inner;
    private readonly EngineOptions engine;
    private readonly bool router;
    private readonly Func<HttpClient> http;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private IReadOnlyList<RouterModel> lastListing = [];

    /// <param name="router">A launched router, or <c>Router: true</c> at a base URL (D1, D3).</param>
    /// <param name="http">A client whose base address is the server's root, not <c>/v1</c>.</param>
    public LlamaCppBackend(
        IInferenceBackend inner,
        EngineOptions engine,
        bool router,
        Func<HttpClient> http,
        TimeProvider time,
        ILogger logger)
    {
        this.inner = inner;
        this.engine = engine;
        this.router = router;
        this.http = http;
        this.time = time;
        this.logger = logger;
    }

    public string Name => inner.Name;

    public string Endpoint => inner.Endpoint;

    public bool IsRouter => router;

    /// <summary>The union of what its models serve — per model, <see cref="KindsFor"/>.</summary>
    public IReadOnlyList<string> Kinds
    {
        get
        {
            if (!router)
            {
                return SingleKinds;
            }

            var kinds = new List<string> { CapabilityKinds.Chat };

            if (engine.Serve.Presets.Values.Any(p => p.Embeddings))
            {
                kinds.Add(CapabilityKinds.Embed);
            }

            if (engine.Serve.Presets.Values.Any(p => p.Reranking))
            {
                kinds.Add(CapabilityKinds.Rerank);
            }

            kinds.Add(CapabilityKinds.LlamaCpp);
            return kinds;
        }
    }

    private IReadOnlyList<string> SingleKinds => engine.Embeddings
        ? [CapabilityKinds.Embed, CapabilityKinds.LlamaCpp]
        : engine.Reranking
            ? [CapabilityKinds.Rerank, CapabilityKinds.LlamaCpp]
            : [CapabilityKinds.Chat, CapabilityKinds.LlamaCpp];

    public IReadOnlyList<string>? KindsFor(string model)
    {
        if (!router)
        {
            return SingleKinds;
        }

        // A preset says what its model is for; a file in the directory or a pulled repo chats.
        return engine.Serve.Presets.TryGetValue(model, out var preset)
            ? preset.Embeddings
                ? [CapabilityKinds.Embed, CapabilityKinds.LlamaCpp]
                : preset.Reranking
                    ? [CapabilityKinds.Rerank, CapabilityKinds.LlamaCpp]
                    : [CapabilityKinds.Chat, CapabilityKinds.LlamaCpp]
            : [CapabilityKinds.Chat, CapabilityKinds.LlamaCpp];
    }

    public async Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken)
    {
        if (!router)
        {
            return await inner.ListModelsAsync(cancellationToken);
        }

        var listing = await ListRouterAsync(cancellationToken);

        if (listing is null)
        {
            return null;
        }

        // D2: a model still downloading is not one a request can be sent to.
        return listing
            .Where(m => !string.Equals(m.Status, RouterModel.Downloading, StringComparison.OrdinalIgnoreCase))
            .Where(m => !Shadowed(m))
            .Select(m => new ModelInfo(m.Id, Digest: null, SizeBytes: null))
            .ToArray();
    }

    /// <summary>
    /// A second listing of a preset's own weights (found in the live run): a preset's <c>HfRepo</c>
    /// lands in llama.cpp's cache, and the router lists the cache too — so a reranker appeared again
    /// under its repo name, where D2 would have declared it a chat model. A preset's <c>Model</c>
    /// inside <c>ModelsDir</c> is the same file listed twice the same way. The preset's name is the one.
    /// </summary>
    private bool Shadowed(RouterModel model)
    {
        if (engine.Serve.Presets.ContainsKey(model.Id))
        {
            return false;
        }

        foreach (var preset in engine.Serve.Presets.Values)
        {
            if (model.Source == "cache"
                && !string.IsNullOrWhiteSpace(preset.HfRepo)
                && string.Equals(model.Id, preset.HfRepo.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (model.Source == "models_dir"
                && !string.IsNullOrWhiteSpace(preset.Model)
                && !string.IsNullOrWhiteSpace(engine.Serve.ModelsDir)
                && string.Equals(Path.GetFileNameWithoutExtension(preset.Model.Trim()), model.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    Path.GetFullPath(Path.GetDirectoryName(preset.Model.Trim()) ?? "."),
                    Path.GetFullPath(engine.Serve.ModelsDir.Trim()).TrimEnd('/', '\\'),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken)
        => inner.GenerateAsync(requestJson, cancellationToken);

    public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken)
        => inner.ChatAsync(requestJson, cancellationToken);

    public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken)
        => inner.EmbedAsync(requestJson, cancellationToken);

    public IAsyncEnumerable<string> StreamAsync(string kind, string requestJson, CancellationToken cancellationToken)
        => inner.StreamAsync(kind, requestJson, cancellationToken);

    /// <summary>A router manages its models; one <c>llama-server -m</c> has its model fixed at launch.</summary>
    public bool SupportsModelManagement => router;

    /// <summary>
    /// D3: <c>POST /models</c> starts llama.cpp's own Hugging Face download, then the listing says
    /// <c>downloading</c> until it is done. It reports no byte counts, so neither does this.
    /// </summary>
    public async IAsyncEnumerable<ModelPullProgress> PullAsync(
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RequireRouter("pull");
        var repo = NormalizeRepo(model);

        await SendAsync(HttpMethod.Post, "models", new JsonObject { ["model"] = repo }, cancellationToken);
        yield return new ModelPullProgress("downloading from Hugging Face (llama.cpp reports no byte counts)", null, null);

        while (true)
        {
            await Task.Delay(Poll, time, cancellationToken);
            var listing = await ListRouterAsync(cancellationToken)
                ?? throw new InvalidOperationException("the llama.cpp router stopped answering during the download");

            var entry = Find(listing, repo);

            if (entry is null)
            {
                // The router drops an entry whose download failed; the reason is on its stderr, which
                // this node logs.
                throw new InvalidOperationException(
                    $"llama.cpp could not download '{repo}'; its reason is in this node's log (a repo or quant that does not exist reads as 401 there)");
            }

            if (!string.Equals(entry.Status, RouterModel.Downloading, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Engine pulled '{Model}' from Hugging Face.", repo);
                yield break;
            }
        }
    }

    /// <summary>
    /// D3: only what the router can remove — a downloaded repo. A file in <c>Serve:ModelsDir</c> or a
    /// preset is the operator's, and deleting it from the hub would be a hub writing the box's disk.
    /// </summary>
    public async Task DeleteAsync(string model, CancellationToken cancellationToken)
    {
        RequireRouter("delete");
        var entry = Find(await ListRouterAsync(cancellationToken) ?? [], model)
            ?? throw new InvalidOperationException($"the llama.cpp router does not list '{model}'");

        if (!entry.CanRemove)
        {
            throw new InvalidOperationException(
                $"'{entry.Id}' comes from {(entry.Source == "preset" ? "Serve:Presets" : "Serve:ModelsDir")}; it is this box's operator's to remove, not the hub's");
        }

        await SendAsync(HttpMethod.Delete, $"models?model={Uri.EscapeDataString(entry.Id)}", null, cancellationToken);
    }

    /// <summary>D3: <c>/models/load</c>, then wait for <c>loaded</c> — a warm that returns early is not one.</summary>
    public async Task WarmAsync(string model, CancellationToken cancellationToken)
    {
        RequireRouter("warm");
        var entry = Find(await ListRouterAsync(cancellationToken) ?? [], model)
            ?? throw new InvalidOperationException($"the llama.cpp router does not list '{model}'");

        if (string.Equals(entry.Status, RouterModel.Loaded, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await SendAsync(HttpMethod.Post, "models/load", new JsonObject { ["model"] = entry.Id }, cancellationToken);

        var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(1, engine.TimeoutSeconds));
        var seenLoading = false;
        var polls = 0;

        while (time.GetUtcNow() < deadline)
        {
            await Task.Delay(Poll, time, cancellationToken);
            polls++;
            var status = Find(await ListRouterAsync(cancellationToken) ?? [], entry.Id)?.Status;

            if (string.Equals(status, RouterModel.Loaded, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(status, RouterModel.Loading, StringComparison.OrdinalIgnoreCase))
            {
                seenLoading = true;
            }
            else if (seenLoading || polls > 1)
            {
                // A child that dies on a bad GGUF can be gone before the first poll sees "loading";
                // one poll of grace, then anything that is not loading is not going to be loaded.
                throw new InvalidOperationException($"llama.cpp could not load '{entry.Id}'; its reason is in this node's log");
            }
        }

        throw new TimeoutException($"'{entry.Id}' was not loaded within {engine.TimeoutSeconds} s");
    }

    public async Task UnloadAsync(string model, CancellationToken cancellationToken)
    {
        RequireRouter("unload");
        var entry = Find(await ListRouterAsync(cancellationToken) ?? [], model)
            ?? throw new InvalidOperationException($"the llama.cpp router does not list '{model}'");

        if (!string.Equals(entry.Status, RouterModel.Loaded, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entry.Status, RouterModel.Loading, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await SendAsync(HttpMethod.Post, "models/unload", new JsonObject { ["model"] = entry.Id }, cancellationToken);
    }

    public bool Serves(string capability, string model)
    {
        if (string.Equals(capability, CapabilityKinds.LlamaCpp, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(capability, CapabilityKinds.Rerank, StringComparison.OrdinalIgnoreCase)
               && (KindsFor(model) ?? []).Contains(CapabilityKinds.Rerank, StringComparer.OrdinalIgnoreCase);
    }

    public Task<ToolResult> RunAsync(ToolJob job, CancellationToken cancellationToken)
        => string.Equals(job.Capability, CapabilityKinds.Rerank, StringComparison.OrdinalIgnoreCase)
            ? RerankAsync(job, cancellationToken)
            : NativeAsync(job, cancellationToken);

    /// <summary>D4: the caller's body to the server's own route, and its answer back, untouched.</summary>
    private async Task<ToolResult> NativeAsync(ToolJob job, CancellationToken cancellationToken)
    {
        if (LlamaCppNative.ReadPayload(job.Payload) is not { } call)
        {
            return ToolResult.Refused(
                job.JobId,
                $"not a llama.cpp call; the operations are {string.Join(", ", LlamaCppNative.PostOperations.Concat(LlamaCppNative.GetOperations))}",
                ToolErrorCodes.InvalidRequest);
        }

        var request = LlamaCppNative.IsGet(call.Operation)
            ? new HttpRequestMessage(HttpMethod.Get, $"{call.Operation}?model={Uri.EscapeDataString(job.Model)}")
            : new HttpRequestMessage(HttpMethod.Post, call.Operation)
            {
                // A sized body (93 D6): every server this node drives gets a Content-Length.
                Content = new StringContent(call.Body ?? "{}", Encoding.UTF8, "application/json")
            };

        return await ExchangeAsync(job, request, body => ToolResult.Succeeded(job.JobId, body), cancellationToken);
    }

    /// <summary>D5: phase 80's <c>{query, documents}</c> → <c>/v1/rerank</c> → <c>{scores}</c> in document order.</summary>
    private async Task<ToolResult> RerankAsync(ToolJob job, CancellationToken cancellationToken)
    {
        JsonObject body;
        int count;

        try
        {
            var payload = JsonNode.Parse(job.Payload) as JsonObject
                ?? throw new JsonException("not an object");
            count = (payload["documents"] as JsonArray)?.Count ?? 0;
            body = new JsonObject
            {
                ["model"] = job.Model,
                ["query"] = payload["query"]?.DeepClone(),
                ["documents"] = payload["documents"]?.DeepClone()
            };
        }
        catch (JsonException)
        {
            return ToolResult.Refused(job.JobId, "a rerank job is {\"query\", \"documents\"}", ToolErrorCodes.InvalidRequest);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "v1/rerank")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };

        return await ExchangeAsync(
            job,
            request,
            answer =>
            {
                var scores = new double[count];
                var seen = 0;
                long tokens = 0;

                using (var document = JsonDocument.Parse(answer))
                {
                    if (document.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var result in results.EnumerateArray())
                        {
                            if (result.TryGetProperty("index", out var index) && index.TryGetInt32(out var i)
                                && i >= 0 && i < count
                                && result.TryGetProperty("relevance_score", out var score) && score.TryGetDouble(out var value))
                            {
                                scores[i] = value;
                                seen++;
                            }
                        }
                    }

                    if (document.RootElement.TryGetProperty("usage", out var usage)
                        && usage.TryGetProperty("total_tokens", out var total) && total.TryGetInt64(out var n))
                    {
                        tokens = n;
                    }
                }

                // 80's exact-length rule: a score missing is not a zero.
                return seen == count
                    ? ToolResult.Succeeded(job.JobId, JsonSerializer.Serialize(new { scores, total_tokens = tokens }))
                    : ToolResult.Failed(job.JobId, $"the llama.cpp reranker scored {seen} of {count} documents");
            },
            cancellationToken);
    }

    private async Task<ToolResult> ExchangeAsync(
        ToolJob job,
        HttpRequestMessage request,
        Func<string, ToolResult> success,
        CancellationToken cancellationToken)
    {
        using var client = http();
        using var _ = request;
        HttpResponseMessage response;

        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ToolResult.Failed(job.JobId, $"the llama.cpp engine did not answer within {client.Timeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Failed(job.JobId, $"the llama.cpp engine at {client.BaseAddress} is unreachable: {ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return success(body);
                }
                catch (JsonException)
                {
                    return ToolResult.Failed(job.JobId, "the llama.cpp engine answered something that is not JSON");
                }
            }

            var message = ErrorMessage(body) ?? $"the llama.cpp engine answered {(int)response.StatusCode}";

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : 1;
                return ToolResult.Retry(job.JobId, message, retryAfter);
            }

            if (response.StatusCode == HttpStatusCode.NotFound
                || message.Contains("not found", StringComparison.OrdinalIgnoreCase) && message.Contains("model", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResult.Refused(job.JobId, message, BrioErrorCodes.ModelNotFound);
            }

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                return ToolResult.Refused(job.JobId, message, ToolErrorCodes.InvalidRequest);
            }

            return ToolResult.Failed(job.JobId, message);
        }
    }

    /// <summary>The router's <c>/models</c>, or null when it could not be asked (69's distinction).</summary>
    internal async Task<IReadOnlyList<RouterModel>?> ListRouterAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = http();
            using var response = await client.GetAsync("models", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var listing = RouterModel.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            lastListing = listing;
            return listing;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    internal IReadOnlyList<RouterModel> LastListing => lastListing;

    private async Task SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        using var client = http();
        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ErrorMessage(text) ?? $"the llama.cpp router answered {(int)response.StatusCode} to {method} /{path}");
        }
    }

    private void RequireRouter(string what)
    {
        if (!router)
        {
            throw new NotSupportedException(
                $"this llama.cpp engine serves one model fixed at launch and cannot {what}; a router (Serve:ModelsDir or Serve:Presets) can");
        }
    }

    /// <summary><c>hf.co/owner/repo:quant</c> is Ollama's spelling of the same repo; llama.cpp takes it bare.</summary>
    internal static string NormalizeRepo(string model)
    {
        var name = (model ?? string.Empty).Trim();

        foreach (var prefix in new[] { "https://huggingface.co/", "huggingface.co/", "hf.co/" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
                break;
            }
        }

        return LlamaCppServe.HfRepo().IsMatch(name)
            ? name
            : throw new ArgumentException($"a llama.cpp router pulls owner/repo[:quant] from Hugging Face; '{model}' is not one");
    }

    private static RouterModel? Find(IReadOnlyList<RouterModel> listing, string model)
        => listing.FirstOrDefault(m => string.Equals(m.Id, model.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static string? ErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }

                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString()?.Trim();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}

/// <summary>One entry of a router's <c>GET /models</c> (b11417).</summary>
internal sealed record RouterModel(string Id, string Status, string? Source, bool CanRemove)
{
    public const string Loaded = "loaded";
    public const string Loading = "loading";
    public const string Downloading = "downloading";

    public static IReadOnlyList<RouterModel> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var models = new List<RouterModel>();

        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return models;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var status = item.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Object
                         && s.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()!
                : "unknown";

            var source = item.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String ? src.GetString() : null;
            var canRemove = item.TryGetProperty("can_remove", out var cr) && cr.ValueKind == JsonValueKind.True;

            models.Add(new RouterModel(id.GetString()!, status, source, canRemove));
        }

        return models;
    }
}
