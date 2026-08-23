using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Shared.Ollama;
using InferHub.Shared.Upstream;

namespace InferHub.Shared.Gemini;

/// <summary>
/// Speaks Gemini's own <c>:generateContent</c> to the upstream while presenting an Ollama-shaped
/// surface to its caller (phase 64). Hand-rolled over <see cref="HttpClient"/>, no package — the
/// same call 33 D2 made for Qdrant and 63 made for Anthropic, for the same reason.
/// </summary>
/// <remarks>
/// <para>
/// Four things here are neither the OpenAI dialect nor Anthropic's: the credential is
/// <c>x-goog-api-key</c>, <b>the model is a path segment rather than a body field</b> (64 D2),
/// streaming requires <c>?alt=sse</c> or the endpoint answers with a JSON array instead (64 D3),
/// and a refused prompt arrives as a <b>200 with no candidates</b> (64 D7). Everything else about
/// the seam is unchanged — Ollama JSON in, Ollama JSON out, five members (61 D3).
/// </para>
/// <para>
/// <b>This is the vendor's <em>legacy</em> surface and that is deliberate (64 D1).</b> Google's
/// current documentation recommends the Interactions API for new work and says
/// <c>generateContent</c> "remains fully supported" with no removal date. What Interactions adds —
/// <c>steps[]</c> and server-side conversation state — is what this track defers and what rules 6
/// and 7 would make us discard. A hub that wants Gemini's agentic surface wants a different type,
/// not a change here.
/// </para>
/// <para>
/// The <see cref="HttpClient"/> is supplied and owned by the caller — including for the async
/// iterator, whose enumeration must not outlive it.
/// </para>
/// </remarks>
public sealed class GeminiUpstreamClient(HttpClient http, int? thinkingBudget)
    : IUpstreamDialect
{
    private const string GenerateVerb = ":generateContent";

    /// <summary>
    /// <b><c>?alt=sse</c> is not optional.</b> Without it <c>:streamGenerateContent</c> answers with
    /// a chunked JSON array and never emits a <c>data:</c> line, so a reader written for SSE does
    /// not fail — it waits, until the request times out (64 D3).
    /// </summary>
    private const string StreamVerb = ":streamGenerateContent?alt=sse";

    private const string EmbedVerb = ":batchEmbedContents";

    private const string ModelsPath = "models";

    private const string DataPrefix = "data:";

    // One page is 1000 at most and the catalogue is short; the loop is bounded so a vendor that
    // never stops handing back a nextPageToken cannot hold a console request open forever.
    private const int ModelPageSize = 1000;
    private const int MaxModelPages = 10;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Points an <see cref="HttpClient"/> at Gemini. Relative paths only resolve against a base
    /// address with a trailing slash — without one, <c>.../v1beta</c> + <c>models/x</c> silently
    /// becomes <c>.../models/x</c> and every call 404s.
    /// </summary>
    /// <remarks>
    /// <b>No <c>Authorization</c> header is set, ever.</b> Gemini authenticates with
    /// <c>x-goog-api-key</c>, and a Bearer token sent instead is a 401 that reads like a bad key —
    /// the same trap 63 D1 named for Anthropic, with a different header.
    /// </remarks>
    public static HttpClient Configure(HttpClient http, string baseUrl, string? apiKey, int timeoutSeconds)
    {
        http.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("x-goog-api-key", apiKey.Trim());
        }

        return http;
    }

    /// <summary>
    /// The model ids Gemini serves, in the vendor's own <c>models/…</c> form so a console listing is
    /// pasteable into a <c>ModelMap</c> unedited (64 D2). Informational only — the map is the
    /// consent and a listing may never create a route (track D4).
    /// </summary>
    public async Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        string? pageToken = null;

        for (var page = 0; page < MaxModelPages; page++)
        {
            var path = pageToken is null
                ? $"{ModelsPath}?pageSize={ModelPageSize}"
                : $"{ModelsPath}?pageSize={ModelPageSize}&pageToken={Uri.EscapeDataString(pageToken)}";

            using var response = await http.GetAsync(path, cancellationToken);
            await ThrowIfUnsuccessfulAsync(response, cancellationToken);

            var body = await response.Content.ReadFromJsonAsync<GeminiModelPage>(JsonOptions, cancellationToken);

            ids.AddRange((body?.Models ?? []).Select(model => model.Name));

            if (body?.NextPageToken is not { Length: > 0 } next)
            {
                break;
            }

            pageToken = next;
        }

        return ids;
    }

    // ---- blocking ---------------------------------------------------------------------

    public async Task<string> ChatAsync(string ollamaJson, CancellationToken cancellationToken)
    {
        var ollama = Deserialize<ChatRequest>(ollamaJson);
        var request = GeminiTranslator.ToGeminiChat(ollama, thinkingBudget);

        var response = await PostAsync(GeminiTranslator.ToModelPath(ollama.Model) + GenerateVerb, request, cancellationToken);

        ThrowIfBlocked(response);

        return Serialize(GeminiTranslator.ToOllamaChat(response, ollama.Model ?? string.Empty));
    }

    public async Task<string> GenerateAsync(string ollamaJson, CancellationToken cancellationToken)
    {
        var ollama = Deserialize<GenerateRequest>(ollamaJson);
        var request = GeminiTranslator.ToGeminiGenerate(ollama, thinkingBudget);

        var response = await PostAsync(GeminiTranslator.ToModelPath(ollama.Model) + GenerateVerb, request, cancellationToken);

        ThrowIfBlocked(response);

        return Serialize(GeminiTranslator.ToOllamaGenerate(response, ollama.Model ?? string.Empty));
    }

    /// <summary>
    /// The first dialect in this project whose embeddings are real (64 D8) — Anthropic's
    /// <c>EmbedAsync</c> is a 501 because there is no such API there, and this is the same seam
    /// answered by the other vendor. <b>No <c>taskType</c> is sent</b>: the better value depends on
    /// whether the caller is ingesting or searching, and nothing in Ollama's <c>/api/embed</c> says
    /// which.
    /// </summary>
    public async Task<string> EmbedAsync(string ollamaJson, CancellationToken cancellationToken)
    {
        var ollama = Deserialize<EmbedRequest>(ollamaJson);
        var modelPath = GeminiTranslator.ToModelPath(ollama.Model);
        var request = GeminiTranslator.ToGeminiEmbed(ollama, modelPath);

        using var response = await http.PostAsJsonAsync(modelPath + EmbedVerb, request, JsonOptions, cancellationToken);

        await ThrowIfUnsuccessfulAsync(response, cancellationToken);

        var body = await response.Content.ReadFromJsonAsync<GeminiBatchEmbedResponse>(JsonOptions, cancellationToken)
            ?? throw new GeminiUpstreamException(
                (int)response.StatusCode,
                "the Gemini upstream returned an empty body");

        return Serialize(GeminiTranslator.ToOllamaEmbed(body, ollama.Model ?? string.Empty));
    }

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

        var request = GeminiTranslator.ToGeminiChat(ollama, thinkingBudget);
        var path = GeminiTranslator.ToModelPath(ollama.Model) + StreamVerb;
        var state = new StreamState();

        await foreach (var text in ReadTextAsync(path, request, state, cancellationToken))
        {
            yield return Serialize(GeminiTranslator.ToOllamaChatDelta(text, model));
        }

        yield return Serialize(GeminiTranslator.ToOllamaChatDone(model, state.FinishReason, state.Usage));
    }

    private async IAsyncEnumerable<string> StreamGenerateAsync(
        string ollamaJson,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var ollama = Deserialize<GenerateRequest>(ollamaJson);
        var model = ollama.Model ?? string.Empty;

        var request = GeminiTranslator.ToGeminiGenerate(ollama, thinkingBudget);
        var path = GeminiTranslator.ToModelPath(ollama.Model) + StreamVerb;
        var state = new StreamState();

        await foreach (var text in ReadTextAsync(path, request, state, cancellationToken))
        {
            yield return Serialize(GeminiTranslator.ToOllamaGenerateDelta(text, model));
        }

        yield return Serialize(GeminiTranslator.ToOllamaGenerateDone(model, state.FinishReason, state.Usage));
    }

    /// <summary>
    /// The text deltas, in order, with the finish reason and the token counts kept in
    /// <paramref name="state"/> along the way.
    /// </summary>
    /// <remarks>
    /// <b>Gemini's stream is a third end-of-stream convention</b> (64 D3): every frame is a whole
    /// <c>GenerateContentResponse</c>, there is no <c>[DONE]</c> sentinel and no terminal event —
    /// the stream ends when the body does. A frame carrying an <c>error</c> object instead of
    /// candidates raises mid-iteration, which is 62 D4's contract for the third time; so does a
    /// frame that blocks the prompt, which would otherwise be a 200 that looks finished (64 D7).
    /// </remarks>
    private async IAsyncEnumerable<string> ReadTextAsync(
        string path,
        GeminiGenerateRequest request,
        StreamState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path)
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

            if (!line.StartsWith(DataPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[DataPrefix.Length..].Trim();

            if (payload.Length == 0)
            {
                continue;
            }

            // An error arrives on this same channel, as a data frame whose body is the envelope
            // rather than a response. Checked before the response parse, because the two shapes
            // share no field and a GenerateContentResponse with nothing in it is indistinguishable
            // from a frame this dialect failed to understand.
            if (ErrorFrame(payload) is { } failure)
            {
                throw failure;
            }

            GeminiGenerateResponse? frame;

            try
            {
                frame = JsonSerializer.Deserialize<GeminiGenerateResponse>(payload, JsonOptions);
            }
            catch (JsonException)
            {
                // A frame this dialect cannot read is not a reason to fail a stream that is
                // otherwise arriving. The vendor adds fields; it has not yet removed one.
                continue;
            }

            if (frame is null)
            {
                continue;
            }

            if (GeminiTranslator.BlockReason(frame) is { } blocked)
            {
                throw Blocked(blocked);
            }

            state.Observe(frame);

            if (GeminiTranslator.TextOf(frame) is { Length: > 0 } text)
            {
                yield return text;
            }
        }
    }

    /// <summary>
    /// The counts as they stood at the last frame that carried any. <b>Taken, never summed</b>
    /// (64 D4): Gemini's streaming <c>usageMetadata</c> is cumulative, so adding the frames together
    /// over-reports every stream. That is the second vendor in a row to publish it this way, which
    /// is why 64 D4 states it as a rule — read a provider's usage as a snapshot unless it documents
    /// an increment.
    /// </summary>
    private sealed class StreamState
    {
        public string? FinishReason { get; private set; }

        public GeminiUsageMetadata? Usage { get; private set; }

        public void Observe(GeminiGenerateResponse frame)
        {
            // Absence stays absence (v3.13.1): a frame without usage does not blank what the last
            // one reported, and a stream without any leaves the counts off the done chunk entirely.
            Usage = frame.UsageMetadata ?? Usage;

            if (frame.Candidates is { Count: > 0 } candidates
                && candidates[0].FinishReason is { Length: > 0 } reason)
            {
                FinishReason = reason;
            }
        }
    }

    // ---- plumbing ---------------------------------------------------------------------

    private async Task<GeminiGenerateResponse> PostAsync(
        string path,
        GeminiGenerateRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(path, request, JsonOptions, cancellationToken);

        await ThrowIfUnsuccessfulAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<GeminiGenerateResponse>(JsonOptions, cancellationToken)
            ?? throw new GeminiUpstreamException(
                (int)response.StatusCode,
                "the Gemini upstream returned an empty body");
    }

    /// <summary>
    /// 64 D7. A prompt the safety filters refused comes back <b>200 with an empty
    /// <c>candidates</c></b> and the reason under <c>promptFeedback</c>. Letting that through would
    /// hand a client an empty answer with <c>done: true</c> — a wrong answer shaped like a right
    /// one, and the third time this track has met a success status that is not one.
    /// </summary>
    private static void ThrowIfBlocked(GeminiGenerateResponse response)
    {
        if (GeminiTranslator.BlockReason(response) is { } reason)
        {
            throw Blocked(reason);
        }
    }

    private static GeminiUpstreamException Blocked(string reason)
        => new(
            (int)HttpStatusCode.BadGateway,
            $"the Gemini upstream refused the prompt before the model saw it (blockReason: {reason}); "
            + "no answer was generated");

    /// <summary>
    /// A <c>data:</c> frame carrying Google's error envelope rather than a response, or null.
    /// 62 D4's contract for the third time: a failure after the response headers must raise, or a
    /// request that died at token 40 returns 200 and looks finished.
    /// </summary>
    private static GeminiUpstreamException? ErrorFrame(string payload)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<GeminiErrorEnvelope>(payload, JsonOptions);

            if (envelope?.Error is not { } error || (error.Message is null && error.Status is null))
            {
                return null;
            }

            // The transport succeeded and the upstream did not, which is a 502 — the status is not
            // inferred from the text and not borrowed from error.code (29 D6, unmoved).
            return new GeminiUpstreamException(
                (int)HttpStatusCode.BadGateway,
                $"the Gemini upstream failed mid-stream: {Detail(error)}");
        }
        catch (JsonException)
        {
            return null;
        }
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
        throw new GeminiUpstreamException((int)response.StatusCode, Describe(response.StatusCode, body));
    }

    /// <summary>
    /// Google's own <c>message</c> when it sent one, plus the two fields a compatibility layer
    /// drops: the canonical <c>status</c> and, on a 429, the <c>retryDelay</c> from its
    /// <c>RetryInfo</c> detail (64 D9).
    /// </summary>
    private static string Describe(HttpStatusCode status, string body)
    {
        var detail = body;

        try
        {
            var envelope = JsonSerializer.Deserialize<GeminiErrorEnvelope>(body, JsonOptions);

            if (envelope?.Error is { } error && (error.Message is not null || error.Status is not null))
            {
                detail = Detail(error);
            }
        }
        catch (JsonException)
        {
            // A proxy in front of the vendor answers in its own dialect. The raw body will do —
            // it unwraps, it never infers (29 D6).
        }

        detail = detail.Trim();

        var named = Enum.IsDefined(typeof(HttpStatusCode), status)
            ? $"{(int)status} {status}"
            : ((int)status).ToString();

        return detail.Length == 0
            ? $"the Gemini upstream returned {named}"
            : $"the Gemini upstream returned {named}: {detail}";
    }

    /// <summary>
    /// <c>RESOURCE_EXHAUSTED: Quota exceeded… (retry after 51s)</c> — the status is the half an
    /// HTTP number does not give you, and the delay is the half a human needs.
    /// </summary>
    private static string Detail(GeminiErrorBody error)
    {
        var message = string.IsNullOrWhiteSpace(error.Message) ? null : error.Message!.Trim();
        var status = string.IsNullOrWhiteSpace(error.Status) ? null : error.Status!.Trim();

        var text = (status, message) switch
        {
            (null, null) => "no reason given",
            (null, not null) => message!,
            (not null, null) => status!,
            _ => $"{status}: {message}"
        };

        return error.RetryDelay() is { } delay ? $"{text} (retry after {delay})" : text;
    }

    private static T Deserialize<T>(string requestJson)
        => JsonSerializer.Deserialize<T>(requestJson, JsonOptions)
            ?? throw new InvalidOperationException("request body could not be deserialized");

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
