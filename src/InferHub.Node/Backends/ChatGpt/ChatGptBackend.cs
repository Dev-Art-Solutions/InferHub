using System.Runtime.CompilerServices;
using InferHub.Node.Configuration;
using InferHub.Shared.ChatGpt;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Backends.ChatGpt;

/// <summary>
/// A node whose models are its own ChatGPT account's (phase 103): <c>Backend:Type=chatgpt</c>, or one
/// <c>chatgpt</c> engine beside Ollama and the rest. Inference is the Responses API with this node's
/// own session, in solo mode or when the coordinator routes a job here.
/// </summary>
/// <remarks>
/// <b>Not <see cref="UpstreamBackend"/></b>, which drives the four key-authenticated dialects and is
/// left exactly as it was: this one's credential is a session that expires hourly and is refreshed
/// here, and its catalogue changes when somebody signs in as a different account.
/// </remarks>
public sealed class ChatGptBackend(
    NodeChatGpt chatGpt,
    IHttpClientFactory httpClientFactory,
    ILogger<ChatGptBackend> logger) : IInferenceBackend
{
    public const string HttpClientName = "chatgpt-responses";

    private static readonly string[] ChatOnly = [CapabilityKinds.Chat];

    public string Name => BackendOptions.ChatGpt;

    /// <summary>Phase 106: one account per node, so one subject however many engines name it.</summary>
    public const string QuotaSubject = BackendOptions.ChatGpt;

    public string Endpoint => chatGpt.Options.BaseUrl;

    public IReadOnlyList<string> Kinds => ChatOnly;

    public async Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken)
    {
        var state = chatGpt.Session.State();

        // Signed out, or no plan permission: this node serves nothing right now. Empty, not null —
        // "has none" is the true answer, and the router should stop sending here.
        if (!state.SignedIn || !state.PlanUsage)
        {
            return [];
        }

        try
        {
            var models = await chatGpt.Session.ListModelsAsync(chatGpt.Options.BaseUrl, cancellationToken);

            // Digest and size are facts about a file on a box; an account's model has neither.
            return ModelFilter.Apply(
                models.Select(model => new ModelInfo(model.Slug, Digest: null, SizeBytes: null)).ToArray(),
                chatGpt.Options.Models);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not list this node's ChatGPT models: {Message}", ex.Message);

            // Null, not empty: "could not ask" is not "has none" (see IInferenceBackend).
            return null;
        }
    }

    public async Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken)
    {
        using var quota = InferHub.Shared.Quota.QuotaScope.Enter(QuotaSubject);
        using var http = CreateHttpClient();
        return await Dialect(http).ChatAsync(requestJson, cancellationToken);
    }

    public async Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken)
    {
        using var quota = InferHub.Shared.Quota.QuotaScope.Enter(QuotaSubject);
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
        using var quota = InferHub.Shared.Quota.QuotaScope.Enter(QuotaSubject);
        using var http = CreateHttpClient();

        await foreach (var chunk in Dialect(http).StreamAsync(kind, requestJson, cancellationToken))
        {
            yield return chunk;
        }
    }

    // An account's catalogue is not ours to pull into or delete from.
    public bool SupportsModelManagement => false;

    public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the chatgpt backend cannot manage models");

    public Task DeleteAsync(string model, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the chatgpt backend cannot manage models");

    public Task WarmAsync(string model, CancellationToken cancellationToken) =>
        throw new NotSupportedException("the chatgpt backend cannot manage models");

    private ChatGptResponsesClient Dialect(HttpClient http) => new(http, chatGpt.Session, chatGpt.UsagePause);

    private HttpClient CreateHttpClient()
        => ChatGptResponsesClient.Configure(
            httpClientFactory.CreateClient(HttpClientName),
            chatGpt.Options.BaseUrl,
            chatGpt.Options.TimeoutSeconds);
}
