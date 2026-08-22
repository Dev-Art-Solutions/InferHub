using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Shared.Ollama;
using InferHub.Shared.Upstream;

namespace InferHub.Shared.Anthropic;

/// <summary>
/// Speaks Anthropic's own <c>/v1/messages</c> to the upstream while presenting an Ollama-shaped
/// surface to its caller (phase 63). Hand-rolled over <see cref="HttpClient"/>, no package — the
/// same call 33 D2 made for Qdrant, for the same reason.
/// </summary>
/// <remarks>
/// <para>
/// Three things here are <b>not</b> the OpenAI dialect and are why this is a second implementation
/// rather than a base URL: the credential is <c>x-api-key</c> with a required
/// <c>anthropic-version</c>, <c>max_tokens</c> is mandatory, and the SSE stream is typed events
/// with no <c>[DONE]</c> sentinel. Everything else about the seam is unchanged — Ollama JSON in,
/// Ollama JSON out, five members (61 D3).
/// </para>
/// <para>
/// The <see cref="HttpClient"/> is supplied and owned by the caller — including for the async
/// iterator, whose enumeration must not outlive it.
/// </para>
/// </remarks>
public sealed class AnthropicUpstreamClient(HttpClient http, int maxTokens)
    : IUpstreamDialect
{
    /// <summary>The version header Anthropic requires on every request, including <c>/v1/models</c>.</summary>
    public const string DefaultVersion = "2023-06-01";

    private const string MessagesPath = "messages";
    private const string ModelsPath = "models";

    private const string DataPrefix = "data:";

    // One page is 1000 at most, and the listing is short; the loop is bounded so a vendor that
    // never stops saying has_more cannot hold a console request open forever.
    private const int ModelPageSize = 1000;
    private const int MaxModelPages = 10;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Points an <see cref="HttpClient"/> at Anthropic. Relative paths only resolve against a base
    /// address with a trailing slash — without one, <c>.../v1</c> + <c>messages</c> silently
    /// becomes <c>.../messages</c> and every call 404s.
    /// </summary>
    /// <remarks>
    /// <b>No <c>Authorization</c> header is set, ever.</b> Anthropic authenticates with
    /// <c>x-api-key</c>, and a Bearer token sent instead is a 401 that reads like a bad key.
    /// </remarks>
    public static HttpClient Configure(
        HttpClient http,
        string baseUrl,
        string? apiKey,
        int timeoutSeconds,
        string? version = null)
    {
        http.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "anthropic-version",
            string.IsNullOrWhiteSpace(version) ? DefaultVersion : version.Trim());

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey.Trim());
        }

        return http;
    }

    /// <summary>
    /// The model ids Anthropic serves. Informational only — the <c>ModelMap</c> is the consent and
    /// a listing may never create a route (track D4).
    /// </summary>
    public async Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        string? afterId = null;

        for (var page = 0; page < MaxModelPages; page++)
        {
            var path = afterId is null
                ? $"{ModelsPath}?limit={ModelPageSize}"
                : $"{ModelsPath}?limit={ModelPageSize}&after_id={Uri.EscapeDataString(afterId)}";

            using var response = await http.GetAsync(path, cancellationToken);
            await ThrowIfUnsuccessfulAsync(response, cancellationToken);

            var body = await response.Content.ReadFromJsonAsync<AnthropicModelPage>(JsonOptions, cancellationToken);

            ids.AddRange((body?.Data ?? []).Select(model => model.Id));

            if (body is not { HasMore: true, LastId: { Length: > 0 } last })
            {
                break;
            }

            afterId = last;
        }

        return ids;
    }

    // ---- blocking ---------------------------------------------------------------------

    public async Task<string> ChatAsync(string ollamaJson, CancellationToken cancellationToken)
    {
        var ollama = Deserialize<ChatRequest>(ollamaJson);
        var request = AnthropicTranslator.ToAnthropicChat(ollama, maxTokens);
        request.Stream = false;

        var response = await PostAsync(request, cancellationToken);

        return Serialize(AnthropicTranslator.ToOllamaChat(response, ollama.Model ?? string.Empty));
    }

    public async Task<string> GenerateAsync(string ollamaJson, CancellationToken cancellationToken)
    {
        var ollama = Deserialize<GenerateRequest>(ollamaJson);
        var request = AnthropicTranslator.ToAnthropicGenerate(ollama, maxTokens);
        request.Stream = false;

        var response = await PostAsync(request, cancellationToken);

        return Serialize(AnthropicTranslator.ToOllamaGenerate(response, ollama.Model ?? string.Empty));
    }

    /// <summary>
    /// Anthropic publishes no embeddings API, so this refuses rather than approximating one
    /// (63 D7). Nothing on the coordinator reaches it — an embedding request goes to the fleet —
    /// and phase 67 is where the refusal becomes a declared capability instead of an exception.
    /// </summary>
    public Task<string> EmbedAsync(string ollamaJson, CancellationToken cancellationToken)
        => throw new AnthropicUpstreamException(
            (int)HttpStatusCode.NotImplemented,
            "Anthropic publishes no embeddings API: this provider serves chat only, and an "
            + "embedding model must be mapped to a provider or a node that has one");

    // ---- streaming --------------------------------------------------------------------

    public IAsyncEnumerable<string> StreamAsync(
        string kind,
        string ollamaJson,
        CancellationToken cancellationToken)
        => kind switch
        {
            "chat" => StreamChatAsync(ollamaJson, cancellationToken),
            "generate" => StreamGenerateAsync(ollamaJson, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported inference job kind '{kind}'.")
        };

    private async IAsyncEnumerable<string> StreamChatAsync(
        string ollamaJson,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var ollama = Deserialize<ChatRequest>(ollamaJson);
        var model = ollama.Model ?? string.Empty;

        var request = AnthropicTranslator.ToAnthropicChat(ollama, maxTokens);
        request.Stream = true;

        var state = new StreamState();

        await foreach (var text in ReadTextAsync(request, state, cancellationToken))
        {
            yield return Serialize(AnthropicTranslator.ToOllamaChatDelta(text, model));
        }

        yield return Serialize(AnthropicTranslator.ToOllamaChatDone(model, state.StopReason, state.Usage()));
    }

    private async IAsyncEnumerable<string> StreamGenerateAsync(
        string ollamaJson,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var ollama = Deserialize<GenerateRequest>(ollamaJson);
        var model = ollama.Model ?? string.Empty;

        var request = AnthropicTranslator.ToAnthropicGenerate(ollama, maxTokens);
        request.Stream = true;

        var state = new StreamState();

        await foreach (var text in ReadTextAsync(request, state, cancellationToken))
        {
            yield return Serialize(AnthropicTranslator.ToOllamaGenerateDelta(text, model));
        }

        yield return Serialize(AnthropicTranslator.ToOllamaGenerateDone(model, state.StopReason, state.Usage()));
    }

    /// <summary>
    /// The text deltas, in order, with the stop reason and the token counts accumulated into
    /// <paramref name="state"/> along the way.
    /// </summary>
    /// <remarks>
    /// <b>Anthropic's stream is not OpenAI's in three ways</b> and this is where all three live
    /// (63 D6): there is no <c>[DONE]</c> sentinel — <c>message_stop</c> ends it; an unknown event
    /// type is skipped, because the vendor's versioning policy says new ones will be added; and an
    /// <c>event: error</c> is a failure that arrived after the response headers, raised
    /// mid-iteration exactly as 62 D4 established for the other dialect.
    /// </remarks>
    private async IAsyncEnumerable<string> ReadTextAsync(
        AnthropicMessagesRequest request,
        StreamState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, MessagesPath)
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };

        using var response = await http.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        await ThrowIfUnsuccessfulAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            // ReadLineAsync only observes the token when it has to hit the socket; a line already
            // sitting in the reader's buffer comes back regardless. Check explicitly, so an
            // abandoned client stops the parse on the next frame rather than draining a response
            // nobody is listening to.
            cancellationToken.ThrowIfCancellationRequested();

            // The `event:` line names what the payload already says in its own `type`, so it is
            // skipped with the comments and the blank separators.
            if (!line.StartsWith(DataPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[DataPrefix.Length..].Trim();

            if (payload.Length == 0)
            {
                continue;
            }

            AnthropicStreamEvent? frame;

            try
            {
                frame = JsonSerializer.Deserialize<AnthropicStreamEvent>(payload, JsonOptions);
            }
            catch (JsonException)
            {
                // A frame this dialect cannot read is not a reason to fail a stream that is
                // otherwise arriving. The versioning policy is explicit that shapes will grow.
                continue;
            }

            if (frame is null)
            {
                continue;
            }

            switch (frame.Type)
            {
                case "message_start":
                    // The input count arrives here and nowhere else on most responses. Its
                    // output_tokens is 1 before a single token has been produced, which is exactly
                    // why 63 D5 takes counts rather than summing them.
                    state.Observe(frame.Message?.Usage);
                    state.StopReason ??= frame.Message?.StopReason;
                    break;

                case "content_block_delta":
                    if (frame.Delta is { Type: "text_delta", Text: { Length: > 0 } text })
                    {
                        yield return text;
                    }

                    break;

                case "message_delta":
                    state.StopReason = frame.Delta?.StopReason ?? state.StopReason;
                    state.Observe(frame.Usage);
                    break;

                case "error":
                    throw ErrorFrame(frame.Error);

                case "message_stop":
                    yield break;

                // ping, content_block_start, content_block_stop, and whatever the vendor adds next.
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// The counts as they stood at the last frame that carried any. <b>Taken, never summed</b>
    /// (63 D5): Anthropic documents the <c>message_delta</c> usage as cumulative, so adding the
    /// frames together over-reports every stream — and an over-reported number sitting beside the
    /// ones this hub measured is how a usage report stops being evidence.
    /// </summary>
    private sealed class StreamState
    {
        private int? inputTokens;
        private int? outputTokens;
        private int? cacheCreationTokens;
        private int? cacheReadTokens;
        private bool observed;

        public string? StopReason { get; set; }

        public void Observe(AnthropicUsage? usage)
        {
            if (usage is null)
            {
                return;
            }

            observed = true;
            inputTokens = usage.InputTokens ?? inputTokens;
            outputTokens = usage.OutputTokens ?? outputTokens;
            cacheCreationTokens = usage.CacheCreationInputTokens ?? cacheCreationTokens;
            cacheReadTokens = usage.CacheReadInputTokens ?? cacheReadTokens;
        }

        /// <summary>Null when no frame carried usage at all — absence stays absence (v3.13.1).</summary>
        public AnthropicUsage? Usage() => observed
            ? new AnthropicUsage
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CacheCreationInputTokens = cacheCreationTokens,
                CacheReadInputTokens = cacheReadTokens
            }
            : null;
    }

    // ---- plumbing ---------------------------------------------------------------------

    private async Task<AnthropicMessageResponse> PostAsync(
        AnthropicMessagesRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(MessagesPath, request, JsonOptions, cancellationToken);

        await ThrowIfUnsuccessfulAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<AnthropicMessageResponse>(JsonOptions, cancellationToken)
            ?? throw new AnthropicUpstreamException(
                (int)response.StatusCode,
                "the Anthropic upstream returned an empty body");
    }

    private static async Task ThrowIfUnsuccessfulAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new AnthropicUpstreamException((int)response.StatusCode, Describe(response.StatusCode, body));
    }

    private static AnthropicUpstreamException ErrorFrame(AnthropicErrorBody? error)
    {
        var detail = string.IsNullOrWhiteSpace(error?.Message)
            ? error?.Type ?? "no reason given"
            : error!.Message!.Trim();

        // 529 overloaded_error is the common one here. The status is not inferred from the text —
        // the transport succeeded and the upstream did not, which is a 502 (29 D6, unmoved).
        return new AnthropicUpstreamException(
            (int)HttpStatusCode.BadGateway,
            $"the Anthropic upstream failed mid-stream: {detail}");
    }

    /// <summary>
    /// Anthropic's own <c>error.message</c> when it sent one, plus the <c>request_id</c> — the
    /// identifier their support asks for, and the one thing a raw-body fallback would bury.
    /// </summary>
    private static string Describe(HttpStatusCode status, string body)
    {
        var detail = body;
        string? requestId = null;

        try
        {
            var envelope = JsonSerializer.Deserialize<AnthropicErrorEnvelope>(body, JsonOptions);

            if (!string.IsNullOrWhiteSpace(envelope?.Error?.Message))
            {
                detail = envelope.Error.Message!;
                requestId = envelope.RequestId;
            }
        }
        catch (JsonException)
        {
            // A proxy in front of the vendor answers in its own dialect. The raw body will do —
            // it unwraps, it never infers (29 D6).
        }

        detail = detail.Trim();

        // 529 is Anthropic's own and has no name in HttpStatusCode; printing "529 529" would be
        // the framework's gap showing through to an operator.
        var named = Enum.IsDefined(typeof(HttpStatusCode), status)
            ? $"{(int)status} {status}"
            : ((int)status).ToString();

        var suffix = string.IsNullOrWhiteSpace(requestId) ? string.Empty : $" (request_id: {requestId})";

        return detail.Length == 0
            ? $"the Anthropic upstream returned {named}{suffix}"
            : $"the Anthropic upstream returned {named}: {detail}{suffix}";
    }

    private static T Deserialize<T>(string requestJson)
        => JsonSerializer.Deserialize<T>(requestJson, JsonOptions)
            ?? throw new InvalidOperationException("request body could not be deserialized");

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
