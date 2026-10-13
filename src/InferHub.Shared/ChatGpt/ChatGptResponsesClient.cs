using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Shared.Ollama;
using InferHub.Shared.Upstream;

namespace InferHub.Shared.ChatGpt;

/// <summary>
/// The fifth upstream dialect (phase 103): OpenAI's <c>/v1/responses</c>, called with a ChatGPT
/// sign-in's access token instead of an API key, presenting the same Ollama-shaped surface as the
/// other four (61 D3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Not <see cref="OpenAi.OpenAiUpstreamClient"/> with a different token</b>, and that is the
/// phase's load-bearing call (103 D2). Plan usage is only on the Responses API, only with
/// <c>store: false</c> and <c>stream: true</c>, with a list of fields that are refused outright
/// (<c>temperature</c>, <c>top_p</c>, <c>max_output_tokens</c>, <c>user</c>…) — so the
/// chat-completions dialect would be a 400 on the first request that carried an Ollama option. The
/// existing <c>openai-compatible</c> type, with its API key, is untouched.
/// </para>
/// <para>
/// <b>Every request streams upstream, including a blocking one.</b> The vendor requires
/// <c>stream: true</c>, so a non-streamed Ollama call is the same stream read to the end and folded
/// into one answer — and it counts as done only on <c>response.completed</c>, because a usage-limit
/// failure can arrive after the first token.
/// </para>
/// </remarks>
public sealed class ChatGptResponsesClient(HttpClient http, ChatGptSession session, TimeSpan? usagePause = null) : IUpstreamDialect
{
    private const string ResponsesPath = "responses";

    private const string AssistantRole = "assistant";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Points the client at the Responses API. No credential is set here — one is added per request.</summary>
    public static HttpClient Configure(HttpClient http, string baseUrl, int timeoutSeconds)
    {
        http.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        return http;
    }

    public async Task<IReadOnlyList<string>> ListModelIdsAsync(CancellationToken cancellationToken)
        => (await session.ListModelsAsync(http.BaseAddress!.ToString(), cancellationToken))
            .Select(model => model.Slug)
            .ToArray();

    public async Task<string> ChatAsync(string ollamaJson, CancellationToken cancellationToken)
    {
        var request = Deserialize<ChatRequest>(ollamaJson);
        var model = request.Model ?? string.Empty;
        var text = new StringBuilder();
        var state = new StreamState();

        await foreach (var delta in ReadAsync(ChatGptResponsesTranslator.FromChat(request), state, cancellationToken))
        {
            text.Append(delta);
        }

        var response = ChatGptResponsesTranslator.ChatDone(model, state);
        response.Message = new ChatMessage { Role = AssistantRole, Content = text.ToString() };
        return JsonSerializer.Serialize(response, JsonOptions);
    }

    public async Task<string> GenerateAsync(string ollamaJson, CancellationToken cancellationToken)
    {
        var request = Deserialize<GenerateRequest>(ollamaJson);
        var model = request.Model ?? string.Empty;
        var text = new StringBuilder();
        var state = new StreamState();

        await foreach (var delta in ReadAsync(ChatGptResponsesTranslator.FromGenerate(request), state, cancellationToken))
        {
            text.Append(delta);
        }

        var response = ChatGptResponsesTranslator.GenerateDone(model, state);
        response.Response = text.ToString();
        return JsonSerializer.Serialize(response, JsonOptions);
    }

    /// <summary>Plan usage covers the Responses API and nothing else: there is no embeddings route.</summary>
    public Task<string> EmbedAsync(string ollamaJson, CancellationToken cancellationToken)
        => throw new ChatGptUpstreamException(501, null,
            "ChatGPT: embeddings are not part of ChatGPT plan usage; map embedding models to a node or another provider.");

    public IAsyncEnumerable<string> StreamAsync(string kind, string ollamaJson, CancellationToken cancellationToken)
        => kind switch
        {
            "chat" => StreamChatAsync(ollamaJson, cancellationToken),
            "generate" => StreamGenerateAsync(ollamaJson, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported inference job kind '{kind}'.")
        };

    private async IAsyncEnumerable<string> StreamChatAsync(string ollamaJson, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = Deserialize<ChatRequest>(ollamaJson);
        var model = request.Model ?? string.Empty;
        var state = new StreamState();

        await foreach (var delta in ReadAsync(ChatGptResponsesTranslator.FromChat(request), state, cancellationToken))
        {
            yield return JsonSerializer.Serialize(new ChatResponse
            {
                Model = model,
                CreatedAt = DateTimeOffset.UtcNow,
                Message = new ChatMessage { Role = AssistantRole, Content = delta },
                Done = false
            }, JsonOptions);
        }

        var done = ChatGptResponsesTranslator.ChatDone(model, state);
        done.Message = new ChatMessage { Role = AssistantRole, Content = string.Empty };
        yield return JsonSerializer.Serialize(done, JsonOptions);
    }

    private async IAsyncEnumerable<string> StreamGenerateAsync(string ollamaJson, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = Deserialize<GenerateRequest>(ollamaJson);
        var model = request.Model ?? string.Empty;
        var state = new StreamState();

        await foreach (var delta in ReadAsync(ChatGptResponsesTranslator.FromGenerate(request), state, cancellationToken))
        {
            yield return JsonSerializer.Serialize(new GenerateResponse
            {
                Model = model,
                CreatedAt = DateTimeOffset.UtcNow,
                Response = delta,
                Done = false
            }, JsonOptions);
        }

        var done = ChatGptResponsesTranslator.GenerateDone(model, state);
        done.Response = string.Empty;
        yield return JsonSerializer.Serialize(done, JsonOptions);
    }

    /// <summary>
    /// The text deltas in order, with the terminal event's usage kept in <paramref name="state"/>.
    /// Raises on <c>response.failed</c>, <c>error</c>, an <c>incomplete</c> that is not a length cut,
    /// and a body that ends with no terminal event at all.
    /// </summary>
    private async IAsyncEnumerable<string> ReadAsync(
        JsonObject body,
        StreamState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await SendAsync(body, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var data = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);

            if (line is null || line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var payload = data.ToString();
                    data.Clear();

                    if (Handle(payload, state) is { } delta)
                    {
                        yield return delta;
                    }

                    if (state.Terminal)
                    {
                        session.ReportSuccess();
                        yield break;
                    }
                }

                if (line is null)
                {
                    break;
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.AsSpan(5).TrimStart(' ');

                if (value.SequenceEqual("[DONE]"))
                {
                    continue;
                }

                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(value);
            }
        }

        if (!state.Terminal)
        {
            throw Fail(new ChatGptUpstreamException(502, null,
                "ChatGPT: the stream ended without response.completed; the answer may be cut short and is not counted as complete."));
        }
    }

    /// <summary>One event. Returns a text delta, or null for an event that carries none.</summary>
    private string? Handle(string payload, StreamState state)
    {
        JsonElement root;

        try
        {
            root = JsonDocument.Parse(payload).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }

        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

        switch (type)
        {
            case "response.output_text.delta":
                return root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String
                    ? delta.GetString()
                    : null;

            case "response.completed":
                state.Terminal = true;
                state.ReadUsage(root);
                return null;

            case "response.incomplete":
                state.ReadUsage(root);
                var reason = root.TryGetProperty("response", out var r)
                             && r.TryGetProperty("incomplete_details", out var details)
                             && details.ValueKind == JsonValueKind.Object
                             && details.TryGetProperty("reason", out var why)
                    ? why.GetString()
                    : null;

                if (reason == "max_output_tokens")
                {
                    state.Terminal = true;
                    state.Length = true;
                    return null;
                }

                throw Fail(new ChatGptUpstreamException(502, "response_incomplete",
                    ChatGptUpstreamException.Describe("response_incomplete", $"the response ended incomplete ({reason ?? "no reason given"})")));

            case "response.failed":
                var (failedCode, failedMessage, failedParam) = root.TryGetProperty("response", out var failed)
                    && failed.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object
                        ? ChatGptUpstreamException.Read(error)
                        : (null, null, null);

                throw Fail(new ChatGptUpstreamException(
                    ChatGptUpstreamException.StatusFor(failedCode),
                    failedCode,
                    ChatGptUpstreamException.Describe(failedCode, failedMessage ?? "the response failed", failedParam)));

            case "error":
                var (code, message, param) = ChatGptUpstreamException.Read(root);

                throw Fail(new ChatGptUpstreamException(
                    ChatGptUpstreamException.StatusFor(code),
                    code,
                    ChatGptUpstreamException.Describe(code, message, param)));

            default:
                return null;
        }
    }

    /// <summary>The POST, with one forced refresh and retry on a 401 — the token may have died early.</summary>
    private async Task<HttpResponseMessage> SendAsync(JsonObject body, CancellationToken cancellationToken)
    {
        var json = body.ToJsonString();
        var token = await session.AccessTokenAsync(cancellationToken);

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ResponsesPath)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    token = await session.ForceRefreshAsync(cancellationToken);
                    continue;
                }

                throw Fail(await ChatGptUpstreamException.FromResponseAsync(response, cancellationToken));
            }
        }
    }

    private ChatGptUpstreamException Fail(ChatGptUpstreamException error)
    {
        session.ReportFailure(error, usagePause);
        return error;
    }

    private static T Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new ChatGptUpstreamException(400, null, "ChatGPT: the request body was empty.");
        }
        catch (JsonException ex)
        {
            throw new ChatGptUpstreamException(400, null, $"ChatGPT: the request body is not valid JSON: {ex.Message}");
        }
    }

    internal sealed class StreamState
    {
        public bool Terminal { get; set; }

        public bool Length { get; set; }

        public int? InputTokens { get; private set; }

        public int? OutputTokens { get; private set; }

        public void ReadUsage(JsonElement root)
        {
            if (root.TryGetProperty("response", out var response)
                && response.ValueKind == JsonValueKind.Object
                && response.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object)
            {
                InputTokens = usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt32(out var i) ? i : null;
                OutputTokens = usage.TryGetProperty("output_tokens", out var output) && output.TryGetInt32(out var o) ? o : null;
            }
        }
    }
}
