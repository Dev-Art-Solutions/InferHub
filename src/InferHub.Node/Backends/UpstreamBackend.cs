using System.Runtime.CompilerServices;
using InferHub.Node.Configuration;
using InferHub.Shared.Anthropic;
using InferHub.Shared.Contracts;
using InferHub.Shared.Gemini;
using InferHub.Shared.OpenAi;
using InferHub.Shared.Upstream;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends;

/// <summary>
/// Drives an upstream that is not this box's own Ollama: a local OpenAI-compatible server (vLLM,
/// llama.cpp, LM Studio, TGI) since phase 22, and since phase 67 OpenRouter, Anthropic's
/// <c>/v1/messages</c> and Gemini's <c>:generateContent</c> as well.
///
/// Ollama-shaped JSON in, Ollama-shaped JSON out: the coordinator never learns this node is talking
/// to something else. All of the translation and SSE parsing is an
/// <see cref="IUpstreamDialect"/> living in <c>InferHub.Shared</c>, shared with the coordinator's
/// own provider dispatcher — the same code composed twice, never a second implementation.
/// </summary>
/// <remarks>
/// <para>
/// <b>One class per seam, not per vendor</b> (67 D2). What the node's second backend has always
/// been — Ollama JSON in, Ollama JSON out, over somebody else's HTTP (22 D1) — is exactly
/// <see cref="IUpstreamDialect"/>'s five members (61 D3), so a fourth vendor costs two arms in two
/// switches rather than a fourth copy of this file. It was called <c>OpenAiBackend</c> until phase
/// 67; a class that drives Anthropic could not keep that name.
/// </para>
/// <para>
/// <b>The credential is part of the dialect.</b> A Bearer token sent to Anthropic or Gemini is a 401
/// that reads like a bad key (63 D1), which is why <see cref="CreateHttpClient"/> switches too and
/// why each vendor's own <c>Configure</c> is the only thing that touches a header.
/// </para>
/// </remarks>
public sealed class UpstreamBackend(
    IHttpClientFactory httpClientFactory,
    IOptions<BackendOptions> backend,
    IOptions<UpstreamBackendOptions> options,
    ILogger<UpstreamBackend> logger) : IInferenceBackend
{
    public const string HttpClientName = "openai-upstream";

    private static readonly string[] ChatAndEmbed = [CapabilityKinds.Chat, CapabilityKinds.Embed];

    private static readonly string[] ChatOnly = [CapabilityKinds.Chat];

    private readonly UpstreamBackendOptions options = options.Value;

    /// <summary>
    /// The configured type, so <c>/api/status</c> and the fleet list say <c>anthropic</c> rather than
    /// the name of the class that happens to drive it.
    /// </summary>
    public string Name => Type;

    public string Endpoint => this.options.ResolvedBaseUrl(Type) ?? "unset";

    /// <summary>
    /// Declared, not discovered (67 D4). <b>Anthropic publishes no embeddings API</b>, so a node on
    /// it declares <c>chat</c> alone and phase 40's router answers an embedding request with a 503
    /// naming the capability — before the hop, instead of a 501 inside a failed job.
    /// </summary>
    /// <remarks>
    /// This is <see cref="SupportsModelManagement"/>'s own argument, one member down: a backend that
    /// throws when asked to do the impossible is a seam nobody trusts twice. It is deliberately not
    /// derived inside <c>BackendCapabilities</c> — that file's whole point (40 D2) is that nothing
    /// there guesses what a model is for.
    /// </remarks>
    public IReadOnlyList<string> Kinds => Type == BackendOptions.Anthropic ? ChatOnly : ChatAndEmbed;

    private string Type => backend.Value.Normalized();

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = CreateHttpClient();
            var ids = await Dialect(http).ListModelIdsAsync(cancellationToken);

            // Digest and size have no upstream equivalent — neither an OpenAI-compatible server nor
            // a cloud vendor reports them. Null is the honest answer; /api/tags and the console
            // render it.
            var models = ids.Select(id => new ModelInfo(id, Digest: null, SizeBytes: null)).ToArray();

            // Against a vendor the catalogue is tens or hundreds of models this node cannot serve.
            // The allowlist is the difference between a useful node and one the router will send
            // anything to — which is why it is *required* for the three vendor types (67 D5).
            return ModelFilter.Apply(models, this.options.Models);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list models from the {Type} upstream at {BaseUrl}", Type, Endpoint);
            return [];
        }
    }

    public async Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken)
    {
        using var http = CreateHttpClient();
        return await Dialect(http).ChatAsync(requestJson, cancellationToken);
    }

    public async Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken)
    {
        using var http = CreateHttpClient();
        return await Dialect(http).GenerateAsync(requestJson, cancellationToken);
    }

    public async Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken)
    {
        using var http = CreateHttpClient();
        return await Dialect(http).EmbedAsync(requestJson, cancellationToken);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string kind,
        string requestJson,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The client is disposed when the enumeration ends — including when the coordinator
        // abandons it — so an abandoned stream does not leak an upstream connection.
        using var http = CreateHttpClient();

        await foreach (var chunk in Dialect(http).StreamAsync(kind, requestJson, cancellationToken))
        {
            yield return chunk;
        }
    }

    // A vLLM / llama.cpp / hosted upstream has its served model fixed at launch, and a cloud vendor
    // is not ours to pull into at all. The capability is declared false so the coordinator never
    // offers the controls; these throw only as a defensive backstop, since a capable caller will not
    // reach them.
    public bool SupportsModelManagement => false;

    public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"the {Type} backend cannot manage models");

    public Task DeleteAsync(string model, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"the {Type} backend cannot manage models");

    public Task WarmAsync(string model, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"the {Type} backend cannot manage models");

    /// <summary>
    /// One dialect per backend type. The switch is exhaustive because the composition root and
    /// <see cref="UpstreamBackendOptionsValidator"/> have already refused an unknown type at
    /// startup — a backend that failed to be understood must not become a request that fails hours
    /// later, in front of a user.
    /// </summary>
    private IUpstreamDialect Dialect(HttpClient http)
        => Type switch
        {
            // OpenRouter *is* the OpenAI dialect — the identity is the claim (62 D1). What its type
            // buys is configuration, one method down.
            BackendOptions.OpenAi or BackendOptions.OpenRouter => new OpenAiUpstreamClient(http),
            BackendOptions.Anthropic => new AnthropicUpstreamClient(http, options.MaxTokens),
            BackendOptions.Gemini => new GeminiUpstreamClient(http, options.ThinkingBudget),
            var type => throw new InvalidOperationException($"backend type '{type}' has no upstream dialect")
        };

    private HttpClient CreateHttpClient()
    {
        var type = Type;

        var baseUrl = options.ResolvedBaseUrl(type)
            ?? throw new InvalidOperationException(
                $"{UpstreamBackendOptions.SectionName}:{nameof(UpstreamBackendOptions.BaseUrl)} is not set.");

        // The factory owns the pooled handler; the base address, key and timeout come from
        // options on every call, so a config reload lands without a restart.
        var pooled = httpClientFactory.CreateClient(HttpClientName);

        if (type == BackendOptions.Anthropic)
        {
            return AnthropicUpstreamClient.Configure(
                pooled,
                baseUrl,
                options.ApiKey,
                options.TimeoutSeconds,
                options.ResolvedAnthropicVersion());
        }

        if (type == BackendOptions.Gemini)
        {
            return GeminiUpstreamClient.Configure(pooled, baseUrl, options.ApiKey, options.TimeoutSeconds);
        }

        var http = OpenAiUpstreamClient.Configure(pooled, baseUrl, options.ApiKey, options.TimeoutSeconds);

        if (type == BackendOptions.OpenRouter)
        {
            // Absent unless the operator wrote one down (62 D2): these two put a deployment on
            // OpenRouter's public rankings, and this node does not volunteer somebody else's.
            AddIfPresent(http, "HTTP-Referer", options.Referer);
            AddIfPresent(http, "X-OpenRouter-Title", options.Title);
        }

        return http;
    }

    private static void AddIfPresent(HttpClient http, string header, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation(header, value.Trim());
        }
    }
}
