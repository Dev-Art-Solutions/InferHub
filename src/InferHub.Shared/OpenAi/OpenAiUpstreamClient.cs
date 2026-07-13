using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Shared.Ollama;

namespace InferHub.Shared.OpenAi;

/// <summary>
/// Speaks the OpenAI wire format to an upstream server — vLLM, llama.cpp's server, LM Studio,
/// TGI, or a hosted provider — while presenting an Ollama-shaped surface to its caller.
///
/// Both ends of the mesh use it: the node's <c>OpenAiBackend</c> to run a job on an upstream
/// engine, and the coordinator's <c>FallbackDispatcher</c> to answer when the fleet holds no
/// node for a model. Two callers, one dialect, one implementation. Nothing here touches ASP.NET
/// or persists anything.
/// </summary>
/// <remarks>
/// The <see cref="HttpClient"/> is supplied and owned by the caller — including for the async
/// iterators, whose enumeration must not outlive it.
/// </remarks>
public sealed class OpenAiUpstreamClient(HttpClient http)
{
    private const string ChatPath = "chat/completions";
    private const string CompletionsPath = "completions";
    private const string EmbeddingsPath = "embeddings";
    private const string ModelsPath = "models";

    private const string DataPrefix = "data:";
    private const string DoneSentinel = "[DONE]";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Points an <see cref="HttpClient"/> at an upstream. Relative paths only resolve against a
    /// base address with a trailing slash: without one, <c>.../v1</c> + <c>chat/completions</c>
    /// silently becomes <c>.../chat/completions</c> and every call 404s.
    /// </summary>
    public static HttpClient Configure(HttpClient http, string baseUrl, string? apiKey, int timeoutSeconds)
    {
        http.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return http;
    }

    /// <summary>The model ids the upstream serves. Digest and size have no equivalent there.</summary>
    public async Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(ModelsPath, cancellationToken);
        await ThrowIfUnsuccessfulAsync(response, cancellationToken);

        var list = await response.Content.ReadFromJsonAsync<ModelList>(JsonOptions, cancellationToken);

        return (list?.Data ?? []).Select(model => model.Id).ToArray();
    }

    // ---- blocking ---------------------------------------------------------------------

    public async Task<string> ChatAsync(string ollamaJson, CancellationToken cancellationToken)
    {
        var ollama = Deserialize<ChatRequest>(ollamaJson);
        var request = UpstreamTranslator.ToOpenAiChat(ollama);
        request.Stream = false;

        var response = await PostAsync<ChatCompletionRequest, ChatCompletionResponse>(
            ChatPath,
            request,
            cancellationToken);

        return Serialize(UpstreamTranslator.ToOllamaChat(response, ollama.Model ?? string.Empty));
    }

    public async Task<string> GenerateAsync(string ollamaJson, CancellationToken cancellationToken)
    {
        var ollama = Deserialize<GenerateRequest>(ollamaJson);
        var request = UpstreamTranslator.ToOpenAiCompletion(ollama);
        request.Stream = false;

        var response = await PostAsync<CompletionRequest, CompletionResponse>(
            CompletionsPath,
            request,
            cancellationToken);

        return Serialize(UpstreamTranslator.ToOllamaGenerate(response, ollama.Model ?? string.Empty));
    }

    public async Task<string> EmbedAsync(string ollamaJson, CancellationToken cancellationToken)
    {
        var ollama = Deserialize<EmbedRequest>(ollamaJson);
        var request = UpstreamTranslator.ToOpenAiEmbeddings(ollama);

        var response = await PostAsync<OpenAiEmbeddingsRequest, OpenAiEmbeddingsResponse>(
            EmbeddingsPath,
            request,
            cancellationToken);

        return Serialize(UpstreamTranslator.ToOllamaEmbed(response, ollama.Model ?? string.Empty));
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

        var request = UpstreamTranslator.ToOpenAiChat(ollama);
        request.Stream = true;
        request.StreamOptions = new StreamOptions { IncludeUsage = true };

        string? finishReason = null;
        OpenAiUsage? usage = null;

        await foreach (var frame in ReadFramesAsync(ChatPath, request, cancellationToken))
        {
            var chunk = JsonSerializer.Deserialize<ChatCompletionChunk>(frame, JsonOptions);
            if (chunk is null)
            {
                continue;
            }

            usage ??= chunk.Usage;

            // The usage-only chunk carries no choices, by contract. It is not a delta.
            if (chunk.Choices is not { Count: > 0 } choices)
            {
                continue;
            }

            finishReason ??= choices[0].FinishReason;

            if (choices[0].FinishReason is not null)
            {
                // The terminal delta is empty and the token counts arrive in a *later* usage
                // chunk, so the Ollama done chunk is emitted once the stream ends — not here.
                continue;
            }

            yield return Serialize(UpstreamTranslator.ToOllamaChatDelta(chunk, model));
        }

        yield return Serialize(UpstreamTranslator.ToOllamaChatDone(model, finishReason, usage));
    }

    private async IAsyncEnumerable<string> StreamGenerateAsync(
        string ollamaJson,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var ollama = Deserialize<GenerateRequest>(ollamaJson);
        var model = ollama.Model ?? string.Empty;

        var request = UpstreamTranslator.ToOpenAiCompletion(ollama);
        request.Stream = true;
        request.StreamOptions = new StreamOptions { IncludeUsage = true };

        string? finishReason = null;
        OpenAiUsage? usage = null;

        await foreach (var frame in ReadFramesAsync(CompletionsPath, request, cancellationToken))
        {
            var chunk = JsonSerializer.Deserialize<CompletionResponse>(frame, JsonOptions);
            if (chunk is null)
            {
                continue;
            }

            usage ??= chunk.Usage;

            if (chunk.Choices is not { Count: > 0 } choices)
            {
                continue;
            }

            finishReason ??= choices[0].FinishReason;

            if (choices[0].FinishReason is not null && string.IsNullOrEmpty(choices[0].Text))
            {
                continue;
            }

            yield return Serialize(UpstreamTranslator.ToOllamaGenerateDelta(chunk, model));
        }

        yield return Serialize(UpstreamTranslator.ToOllamaGenerateDone(model, finishReason, usage));
    }

    /// <summary>
    /// The upstream SSE stream, one <c>data:</c> payload at a time. Comment lines (<c>:</c>
    /// keep-alives) and blank separators are skipped; <c>[DONE]</c> ends it.
    /// </summary>
    private async IAsyncEnumerable<string> ReadFramesAsync<TRequest>(
        string path,
        TRequest request,
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
            // sitting in the StreamReader's buffer comes back regardless. Check explicitly, so an
            // abandoned client stops the parse on the very next frame rather than draining the
            // whole response nobody is listening to.
            cancellationToken.ThrowIfCancellationRequested();

            if (line.Length == 0 || line.StartsWith(':'))
            {
                continue;
            }

            if (!line.StartsWith(DataPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[DataPrefix.Length..].Trim();

            if (payload.Length == 0)
            {
                continue;
            }

            if (payload == DoneSentinel)
            {
                yield break;
            }

            yield return payload;
        }
    }

    // ---- plumbing ---------------------------------------------------------------------

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(path, request, JsonOptions, cancellationToken);

        await ThrowIfUnsuccessfulAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken)
            ?? throw new OpenAiUpstreamException(
                (int)response.StatusCode,
                "the OpenAI-compatible upstream returned an empty body");
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
        throw new OpenAiUpstreamException((int)response.StatusCode, Describe(response.StatusCode, body));
    }

    /// <summary>
    /// The upstream's own <c>error.message</c> when it sent one: a 401 that says "incorrect API
    /// key" is worth far more to whoever is reading the logs than a bare 401.
    /// </summary>
    private static string Describe(HttpStatusCode status, string body)
    {
        var detail = body;

        try
        {
            var envelope = JsonSerializer.Deserialize<OpenAiErrorEnvelope>(body, JsonOptions);
            if (!string.IsNullOrWhiteSpace(envelope?.Error?.Message))
            {
                detail = envelope.Error.Message;
            }
        }
        catch (JsonException)
        {
            // Not every server answers errors in the OpenAI envelope. The raw body will do.
        }

        detail = detail.Trim();

        return detail.Length == 0
            ? $"the OpenAI-compatible upstream returned {(int)status} {status}"
            : $"the OpenAI-compatible upstream returned {(int)status} {status}: {detail}";
    }

    private static T Deserialize<T>(string requestJson)
        => JsonSerializer.Deserialize<T>(requestJson, JsonOptions)
            ?? throw new InvalidOperationException("request body could not be deserialized");

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}

/// <summary>An upstream server answered, and it answered badly. Carries the status it used.</summary>
public sealed class OpenAiUpstreamException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
