using System.Runtime.CompilerServices;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using InferHub.Shared.OpenAi;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends;

/// <summary>
/// Drives any server that speaks the OpenAI wire format: vLLM, llama.cpp's server, LM Studio,
/// TGI, or a hosted provider. One implementation, because they all landed on the same dialect —
/// which is why "vLLM backend", "llama.cpp backend" and "hosted backend" collapsed into this.
///
/// Ollama-shaped JSON in, Ollama-shaped JSON out: the coordinator never learns this node is
/// talking to something else. All of the translation and SSE parsing is
/// <see cref="OpenAiUpstreamClient"/>, shared with the coordinator's cloud-burst fallback.
/// </summary>
public sealed class OpenAiBackend(
    IHttpClientFactory httpClientFactory,
    IOptions<OpenAiBackendOptions> options,
    ILogger<OpenAiBackend> logger) : IInferenceBackend
{
    public const string HttpClientName = "openai-upstream";

    private readonly OpenAiBackendOptions options = options.Value;

    public string Name => "openai";

    public string Endpoint => this.options.BaseUrl ?? "unset";

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = CreateHttpClient();
            var ids = await new OpenAiUpstreamClient(http).ListModelIdsAsync(cancellationToken);

            // Digest and size have no upstream equivalent — an OpenAI-compatible server reports a
            // name and nothing else. Null is the honest answer; /api/tags and the console render it.
            var models = ids.Select(id => new ModelInfo(id, Digest: null, SizeBytes: null)).ToArray();

            // Against a hosted provider the catalogue is hundreds of models this node cannot
            // serve. The allowlist is the difference between a useful node and one the router
            // will send anything to.
            return ModelFilter.Apply(models, this.options.Models);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list models from the OpenAI-compatible upstream at {BaseUrl}", Endpoint);
            return [];
        }
    }

    public async Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken)
    {
        using var http = CreateHttpClient();
        return await new OpenAiUpstreamClient(http).ChatAsync(requestJson, cancellationToken);
    }

    public async Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken)
    {
        using var http = CreateHttpClient();
        return await new OpenAiUpstreamClient(http).GenerateAsync(requestJson, cancellationToken);
    }

    public async Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken)
    {
        using var http = CreateHttpClient();
        return await new OpenAiUpstreamClient(http).EmbedAsync(requestJson, cancellationToken);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string kind,
        string requestJson,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The client is disposed when the enumeration ends — including when the coordinator
        // abandons it — so an abandoned stream does not leak an upstream connection.
        using var http = CreateHttpClient();

        await foreach (var chunk in new OpenAiUpstreamClient(http)
            .StreamAsync(kind, requestJson, cancellationToken))
        {
            yield return chunk;
        }
    }

    // A vLLM / llama.cpp / hosted upstream has its served model fixed at launch — there is nothing
    // to pull into it. The capability is declared false so the coordinator never offers the controls;
    // these throw only as a defensive backstop, since a capable caller will not reach them.
    public bool SupportsModelManagement => false;

    public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the OpenAI-compatible backend cannot manage models");

    public Task DeleteAsync(string model, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the OpenAI-compatible backend cannot manage models");

    public Task WarmAsync(string model, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the OpenAI-compatible backend cannot manage models");

    private HttpClient CreateHttpClient()
    {
        var baseUrl = options.BaseUrl
            ?? throw new InvalidOperationException(
                $"{OpenAiBackendOptions.SectionName}:{nameof(OpenAiBackendOptions.BaseUrl)} is not set.");

        // The factory owns the pooled handler; the base address, key and timeout come from
        // options on every call, so a config reload lands without a restart.
        return OpenAiUpstreamClient.Configure(
            httpClientFactory.CreateClient(HttpClientName),
            baseUrl,
            options.ApiKey,
            options.TimeoutSeconds);
    }
}
