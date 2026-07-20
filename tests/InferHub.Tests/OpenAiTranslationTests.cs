using System.Text.Json;
using InferHub.Coordinator.OpenAi;
using InferHub.Shared.Ollama;

namespace InferHub.Tests;

public class OpenAiTranslationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static ChatCompletionRequest ChatRequest(string json)
        => JsonSerializer.Deserialize<ChatCompletionRequest>(json, JsonOptions)!;

    private static CompletionRequest LegacyRequest(string json)
        => JsonSerializer.Deserialize<CompletionRequest>(json, JsonOptions)!;

    private static JsonElement Translate(string openAiJson)
    {
        var ollama = RequestTranslator.ToOllamaChat(ChatRequest(openAiJson));
        return JsonDocument.Parse(ollama).RootElement.Clone();
    }

    // ---- request: messages and prompt ------------------------------------------------

    [Fact]
    public void MessagesPassThroughUnchanged()
    {
        var body = Translate("""
        {
          "model": "llama3",
          "messages": [
            {"role":"system","content":"You are helpful."},
            {"role":"user","content":"Hi!"}
          ]
        }
        """);

        Assert.Equal("llama3", body.GetProperty("model").GetString());

        var messages = body.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("You are helpful.", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("Hi!", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public void TextContentPartsAreFlattenedToAString()
    {
        var body = Translate("""
        {
          "model": "llama3",
          "messages": [
            {"role":"user","content":[{"type":"text","text":"one"},{"type":"text","text":"two"}]}
          ]
        }
        """);

        Assert.Equal("one\ntwo", body.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    // ---- request: vision (phase 29) --------------------------------------------------

    // A 1x1 PNG. Real bytes, because the translator validates the base64 it forwards.
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private const string JpegBase64 = "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAEBAQEBAQEBAQEB";

    [Fact]
    public void DataUrlImagePartsBecomeOllamaImages()
    {
        var body = Translate($$$"""
        {
          "model": "llava",
          "messages": [
            {"role":"user","content":[
              {"type":"text","text":"What is this?"},
              {"type":"image_url","image_url":{"url":"data:image/png;base64,{{{PngBase64}}}"}}
            ]}
          ]
        }
        """);

        var message = body.GetProperty("messages")[0];

        // Text and images are two fields in Ollama, not one array of parts.
        Assert.Equal("What is this?", message.GetProperty("content").GetString());

        var images = message.GetProperty("images");
        Assert.Equal(1, images.GetArrayLength());
        Assert.Equal(PngBase64, images[0].GetString());
    }

    [Fact]
    public void MultipleImagesKeepTheirArrayOrder()
    {
        var body = Translate($$$"""
        {
          "model": "llava",
          "messages": [
            {"role":"user","content":[
              {"type":"image_url","image_url":{"url":"data:image/png;base64,{{{PngBase64}}}"}},
              {"type":"text","text":"Compare these."},
              {"type":"image_url","image_url":{"url":"data:image/jpeg;base64,{{{JpegBase64}}}"}}
            ]}
          ]
        }
        """);

        var images = body.GetProperty("messages")[0].GetProperty("images");
        Assert.Equal(2, images.GetArrayLength());
        Assert.Equal(PngBase64, images[0].GetString());
        Assert.Equal(JpegBase64, images[1].GetString());
    }

    [Fact]
    public void TextOnlyMessagesCarryNoImagesKey()
    {
        // A stray empty images array on every ordinary message would be a wire change for
        // requests that have nothing to do with vision.
        var body = Translate("""
        {"model":"llama3","messages":[{"role":"user","content":"Hi!"}]}
        """);

        Assert.False(body.GetProperty("messages")[0].TryGetProperty("images", out _));
    }

    [Fact]
    public void RemoteImageUrlsAreRejectedWithAReason()
    {
        // Fetching a caller-supplied URL would make the coordinator an SSRF proxy and pull
        // third-party bytes through a hop that retains nothing. Inlining is the caller's job.
        var ex = Assert.Throws<OpenAiRequestException>(() => Translate("""
        {
          "model": "llava",
          "messages": [
            {"role":"user","content":[{"type":"image_url","image_url":{"url":"http://x/y.png"}}]}
          ]
        }
        """));

        Assert.Contains("does not fetch remote images", ex.Message);
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void MalformedBase64IsRejectedAtTheEdge()
    {
        var ex = Assert.Throws<OpenAiRequestException>(() => Translate("""
        {
          "model": "llava",
          "messages": [
            {"role":"user","content":[{"type":"image_url","image_url":{"url":"data:image/png;base64,!!!not-base64!!!"}}]}
          ]
        }
        """));

        Assert.Contains("base64", ex.Message);
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void AudioContentPartsAreStillRejectedRatherThanDropped()
    {
        // Dropping it would let the model answer confidently about something it was never
        // shown. Unsupported means rejected, not silently ignored.
        var ex = Assert.Throws<OpenAiRequestException>(() => Translate("""
        {
          "model": "llama3",
          "messages": [
            {"role":"user","content":[{"type":"input_audio","input_audio":{"data":"x","format":"wav"}}]}
          ]
        }
        """));

        Assert.Contains("input_audio", ex.Message);
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public void ToolCallsAndToolCallIdSurviveOnMessages()
    {
        var body = Translate("""
        {
          "model": "llama3",
          "messages": [
            {"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"f","arguments":"{}"}}]},
            {"role":"tool","content":"42","tool_call_id":"call_1"}
          ]
        }
        """);

        var messages = body.GetProperty("messages");
        Assert.Equal("call_1", messages[0].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("call_1", messages[1].GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public void PriorToolCallArgumentsAreParsedFromStringBackToAnObjectForOllama()
    {
        // OpenAI clients (and our own streamed delta.tool_calls) serialize arguments as a JSON
        // *string*; Ollama emits and expects an object. Without this, the streamed call → tool
        // result → answer loop reaches the model as a string it can't read and it answers empty.
        var body = Translate("""
        {
          "model": "llama3",
          "messages": [
            {"role":"assistant","content":"","tool_calls":[
              {"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Sofia\"}"}}]},
            {"role":"tool","content":"18C, sunny","tool_call_id":"call_1"}
          ]
        }
        """);

        var arguments = body.GetProperty("messages")[0]
            .GetProperty("tool_calls")[0]
            .GetProperty("function")
            .GetProperty("arguments");

        Assert.Equal(JsonValueKind.Object, arguments.ValueKind);
        Assert.Equal("Sofia", arguments.GetProperty("city").GetString());
    }

    [Fact]
    public void PriorToolCallArgumentsAlreadyAnObjectPassThroughUnchanged()
    {
        // A client that (non-spec) sent an object must not break — leave it an object.
        var body = Translate("""
        {
          "model": "llama3",
          "messages": [
            {"role":"assistant","content":"","tool_calls":[
              {"id":"call_1","type":"function","function":{"name":"f","arguments":{"a":1}}}]}
          ]
        }
        """);

        var arguments = body.GetProperty("messages")[0]
            .GetProperty("tool_calls")[0]
            .GetProperty("function")
            .GetProperty("arguments");

        Assert.Equal(JsonValueKind.Object, arguments.ValueKind);
        Assert.Equal(1, arguments.GetProperty("a").GetInt32());
    }

    [Fact]
    public void LegacyPromptMapsToOllamaPrompt()
    {
        var ollama = RequestTranslator.ToOllamaGenerate(LegacyRequest("""
        {"model":"llama3","prompt":"Once upon a time"}
        """));
        var body = JsonDocument.Parse(ollama).RootElement;

        Assert.Equal("llama3", body.GetProperty("model").GetString());
        Assert.Equal("Once upon a time", body.GetProperty("prompt").GetString());
    }

    // ---- request: sampling options ---------------------------------------------------

    [Fact]
    public void SamplingParametersMapOntoOptionsWithTheSameNames()
    {
        var body = Translate("""
        {
          "model": "llama3",
          "messages": [{"role":"user","content":"hi"}],
          "temperature": 0.4,
          "top_p": 0.9,
          "presence_penalty": 0.5,
          "frequency_penalty": 0.25,
          "seed": 7
        }
        """);

        var options = body.GetProperty("options");
        Assert.Equal(0.4, options.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(0.9, options.GetProperty("top_p").GetDouble(), 3);
        Assert.Equal(0.5, options.GetProperty("presence_penalty").GetDouble(), 3);
        Assert.Equal(0.25, options.GetProperty("frequency_penalty").GetDouble(), 3);
        Assert.Equal(7, options.GetProperty("seed").GetInt64());
    }

    [Fact]
    public void MaxTokensMapsToNumPredict()
    {
        var body = Translate("""
        {"model":"llama3","messages":[{"role":"user","content":"hi"}],"max_tokens":128}
        """);

        Assert.Equal(128, body.GetProperty("options").GetProperty("num_predict").GetInt32());
    }

    [Fact]
    public void MaxCompletionTokensWinsOverMaxTokens()
    {
        // max_completion_tokens supersedes max_tokens in the current API.
        var body = Translate("""
        {"model":"llama3","messages":[{"role":"user","content":"hi"}],"max_tokens":128,"max_completion_tokens":256}
        """);

        Assert.Equal(256, body.GetProperty("options").GetProperty("num_predict").GetInt32());
    }

    [Fact]
    public void StopAsStringBecomesAnArray()
    {
        var body = Translate("""
        {"model":"llama3","messages":[{"role":"user","content":"hi"}],"stop":"END"}
        """);

        var stop = body.GetProperty("options").GetProperty("stop");
        Assert.Equal(JsonValueKind.Array, stop.ValueKind);
        Assert.Equal("END", stop[0].GetString());
    }

    [Fact]
    public void StopAsArrayStaysAnArray()
    {
        var body = Translate("""
        {"model":"llama3","messages":[{"role":"user","content":"hi"}],"stop":["A","B"]}
        """);

        var stop = body.GetProperty("options").GetProperty("stop");
        Assert.Equal(2, stop.GetArrayLength());
        Assert.Equal("A", stop[0].GetString());
        Assert.Equal("B", stop[1].GetString());
    }

    [Fact]
    public void NoSamplingParametersMeansNoOptionsKey()
    {
        var body = Translate("""
        {"model":"llama3","messages":[{"role":"user","content":"hi"}]}
        """);

        Assert.False(body.TryGetProperty("options", out _));
    }

    // ---- request: response_format, tools, stream -------------------------------------

    [Fact]
    public void JsonObjectResponseFormatBecomesFormatJson()
    {
        var body = Translate("""
        {"model":"llama3","messages":[{"role":"user","content":"hi"}],"response_format":{"type":"json_object"}}
        """);

        Assert.Equal("json", body.GetProperty("format").GetString());
    }

    [Fact]
    public void JsonSchemaResponseFormatBecomesTheBareSchemaObject()
    {
        var body = Translate("""
        {
          "model": "llama3",
          "messages": [{"role":"user","content":"hi"}],
          "response_format": {
            "type": "json_schema",
            "json_schema": {"name":"person","schema":{"type":"object","properties":{"name":{"type":"string"}}}}
          }
        }
        """);

        var format = body.GetProperty("format");
        Assert.Equal("object", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("properties").TryGetProperty("name", out _));
    }

    [Fact]
    public void ToolsAndToolChoicePassThroughAsIs()
    {
        var body = Translate("""
        {
          "model": "llama3",
          "messages": [{"role":"user","content":"hi"}],
          "tools": [{"type":"function","function":{"name":"get_weather","parameters":{"type":"object"}}}],
          "tool_choice": "auto"
        }
        """);

        Assert.Equal("get_weather", body.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("auto", body.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public void StreamFlagPassesThrough()
    {
        var streaming = Translate("""
        {"model":"llama3","messages":[{"role":"user","content":"hi"}],"stream":true}
        """);
        Assert.True(streaming.GetProperty("stream").GetBoolean());

        var blocking = Translate("""
        {"model":"llama3","messages":[{"role":"user","content":"hi"}]}
        """);
        Assert.False(blocking.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public void IgnoredFieldsDoNotLeakIntoTheOllamaBody()
    {
        // user / logit_bias / logprobs are accepted and ignored — but they must not ride
        // through into the node request either.
        var body = Translate("""
        {
          "model": "llama3",
          "messages": [{"role":"user","content":"hi"}],
          "user": "u-1",
          "logprobs": true,
          "logit_bias": {"1": 5}
        }
        """);

        Assert.False(body.TryGetProperty("user", out _));
        Assert.False(body.TryGetProperty("logprobs", out _));
        Assert.False(body.TryGetProperty("logit_bias", out _));
    }

    // ---- request: rejections ---------------------------------------------------------

    [Fact]
    public void NGreaterThanOneIsRejected()
    {
        var ex = Assert.Throws<OpenAiRequestException>(() => Translate("""
        {"model":"llama3","messages":[{"role":"user","content":"hi"}],"n":2}
        """));

        Assert.Equal("n > 1 is not supported", ex.Message);
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("n", ex.Param);
        Assert.Equal("invalid_request_error", ex.Type);
    }

    [Fact]
    public void NEqualToOneIsAccepted()
    {
        var body = Translate("""
        {"model":"llama3","messages":[{"role":"user","content":"hi"}],"n":1}
        """);

        Assert.Equal("llama3", body.GetProperty("model").GetString());
    }

    [Fact]
    public void MissingModelIsRejected()
    {
        var ex = Assert.Throws<OpenAiRequestException>(() => Translate("""
        {"messages":[{"role":"user","content":"hi"}]}
        """));

        Assert.Equal("model", ex.Param);
    }

    [Fact]
    public void EmptyMessagesIsRejected()
    {
        var ex = Assert.Throws<OpenAiRequestException>(() => Translate("""
        {"model":"llama3","messages":[]}
        """));

        Assert.Equal("messages", ex.Param);
    }

    // ---- response: chat --------------------------------------------------------------

    private static ChatResponse Ollama(string json)
        => JsonSerializer.Deserialize<ChatResponse>(json, JsonOptions)!;

    [Fact]
    public void BlockingChatResponseMapsToTheChatCompletionShape()
    {
        var ollama = Ollama("""
        {
          "model": "llama3",
          "message": {"role":"assistant","content":"Hello there."},
          "done": true,
          "done_reason": "stop",
          "prompt_eval_count": 11,
          "eval_count": 4
        }
        """);

        var response = ResponseTranslator.ToChatCompletion(ollama, "chatcmpl-x", 1700000000, "llama3");

        Assert.Equal("chatcmpl-x", response.Id);
        Assert.Equal("chat.completion", response.Object);
        Assert.Equal(1700000000, response.Created);
        Assert.Equal("llama3", response.Model);

        var choice = Assert.Single(response.Choices);
        Assert.Equal(0, choice.Index);
        Assert.Equal("assistant", choice.Message.Role);
        Assert.Equal("Hello there.", choice.Message.Content);
        Assert.Equal("stop", choice.FinishReason);

        Assert.NotNull(response.Usage);
        Assert.Equal(11, response.Usage!.PromptTokens);
        Assert.Equal(4, response.Usage.CompletionTokens);
        Assert.Equal(15, response.Usage.TotalTokens);
    }

    [Fact]
    public void FinishReasonIsLengthWhenOllamaSaysSo()
    {
        var ollama = Ollama("""
        {"model":"llama3","message":{"role":"assistant","content":"trunc"},"done":true,"done_reason":"length"}
        """);

        var response = ResponseTranslator.ToChatCompletion(ollama, "id", 0, "llama3");

        Assert.Equal("length", response.Choices[0].FinishReason);
    }

    [Fact]
    public void FinishReasonIsToolCallsWhenTheMessageCarriesThem()
    {
        var ollama = Ollama("""
        {
          "model": "llama3",
          "message": {
            "role": "assistant",
            "content": "",
            "tool_calls": [{"function":{"name":"get_weather","arguments":{"city":"Sofia"}}}]
          },
          "done": true,
          "done_reason": "stop"
        }
        """);

        var response = ResponseTranslator.ToChatCompletion(ollama, "id", 0, "llama3");
        var choice = response.Choices[0];

        Assert.Equal("tool_calls", choice.FinishReason);

        var call = Assert.Single(choice.Message.ToolCalls!);
        Assert.Equal("function", call.Type);
        Assert.StartsWith("call_", call.Id);
        Assert.Equal("get_weather", call.Function.Name);

        // Ollama emits arguments as an object; OpenAI clients parse a JSON *string*.
        Assert.Equal("""{"city":"Sofia"}""", call.Function.Arguments);
    }

    [Fact]
    public void StreamedToolCallIsByteEquivalentToTheBlockingOneIdAside()
    {
        var ollama = Ollama("""
        {
          "model": "llama3",
          "message": {
            "role": "assistant",
            "content": "",
            "tool_calls": [{"function":{"name":"get_weather","arguments":{"city":"Sofia"}}}]
          },
          "done": true,
          "done_reason": "stop"
        }
        """);

        var blocking = ResponseTranslator.ToChatCompletion(ollama, "id", 0, "llama3").Choices[0];
        var streamed = ResponseTranslator.ToChatChunk(ollama, "id", 0, "llama3", isFirst: false).Choices[0];

        // Same terminal verdict on both surfaces.
        Assert.Equal("tool_calls", blocking.FinishReason);
        Assert.Equal("tool_calls", streamed.FinishReason);

        var blockingCall = Assert.Single(blocking.Message.ToolCalls!);
        var streamedCall = Assert.Single(streamed.Delta.ToolCalls!);

        Assert.Equal(0, streamedCall.Index);
        // Function name and stringified arguments are byte-identical; only the synthesized id
        // is minted per call and so differs.
        Assert.Equal(blockingCall.Type, streamedCall.Type);
        Assert.Equal(blockingCall.Function.Name, streamedCall.Function.Name);
        Assert.Equal(blockingCall.Function.Arguments, streamedCall.Function.Arguments);
        Assert.StartsWith("call_", streamedCall.Id);
    }

    [Fact]
    public void StreamedTextDeltaSerializesWithoutAToolCallsField()
    {
        var ollama = Ollama("""
        {"model":"llama3","message":{"role":"assistant","content":"hi"},"done":false}
        """);

        var json = JsonSerializer.Serialize(
            ResponseTranslator.ToChatChunk(ollama, "id", 0, "llama3", isFirst: true),
            JsonOptions);

        var delta = JsonDocument.Parse(json).RootElement
            .GetProperty("choices")[0]
            .GetProperty("delta");

        Assert.Equal("hi", delta.GetProperty("content").GetString());
        Assert.False(delta.TryGetProperty("tool_calls", out _));
    }

    [Fact]
    public void ToolCallArgumentsSerializeAsAJsonStringOnTheWire()
    {
        var ollama = Ollama("""
        {
          "model": "llama3",
          "message": {"role":"assistant","content":"","tool_calls":[{"function":{"name":"f","arguments":{"a":1}}}]},
          "done": true
        }
        """);

        var json = JsonSerializer.Serialize(
            ResponseTranslator.ToChatCompletion(ollama, "id", 0, "llama3"),
            JsonOptions);

        var arguments = JsonDocument.Parse(json).RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("tool_calls")[0]
            .GetProperty("function")
            .GetProperty("arguments");

        Assert.Equal(JsonValueKind.String, arguments.ValueKind);
        Assert.Equal("""{"a":1}""", arguments.GetString());
    }

    [Fact]
    public void MissingTokenCountsOmitUsageRatherThanReportingZeros()
    {
        var ollama = Ollama("""
        {"model":"llama3","message":{"role":"assistant","content":"hi"},"done":true}
        """);

        var response = ResponseTranslator.ToChatCompletion(ollama, "id", 0, "llama3");

        Assert.Null(response.Usage);

        var json = JsonSerializer.Serialize(response, JsonOptions);
        Assert.DoesNotContain("\"usage\"", json);
    }

    [Fact]
    public void StreamChunksCarryRoleOnlyOnTheFirstDelta()
    {
        var first = ResponseTranslator.ToChatChunk(
            Ollama("""{"model":"llama3","message":{"role":"assistant","content":"He"},"done":false}"""),
            "id", 0, "llama3", isFirst: true);

        var second = ResponseTranslator.ToChatChunk(
            Ollama("""{"model":"llama3","message":{"role":"assistant","content":"llo"},"done":false}"""),
            "id", 0, "llama3", isFirst: false);

        Assert.Equal("chat.completion.chunk", first.Object);
        Assert.Equal("assistant", first.Choices[0].Delta.Role);
        Assert.Equal("He", first.Choices[0].Delta.Content);
        Assert.Null(first.Choices[0].FinishReason);

        Assert.Null(second.Choices[0].Delta.Role);
        Assert.Equal("llo", second.Choices[0].Delta.Content);

        // role must be *absent*, not null — clients concatenate blindly.
        var json = JsonSerializer.Serialize(second, JsonOptions);
        Assert.DoesNotContain("\"role\"", json);
    }

    [Fact]
    public void TerminalStreamChunkCarriesAnEmptyDeltaAndAFinishReason()
    {
        var terminal = ResponseTranslator.ToChatChunk(
            Ollama("""{"model":"llama3","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}"""),
            "id", 0, "llama3", isFirst: false);

        Assert.Equal("stop", terminal.Choices[0].FinishReason);
        Assert.Null(terminal.Choices[0].Delta.Content);
        Assert.Null(terminal.Choices[0].Delta.Role);
    }

    [Fact]
    public void UsageChunkHasEmptyChoices()
    {
        var chunk = ResponseTranslator.ToUsageChunk(new OpenAiUsage(3, 5, 8), "id", 0, "llama3");

        Assert.Empty(chunk.Choices);
        Assert.Equal(8, chunk.Usage!.TotalTokens);
    }

    // ---- response: legacy completions ------------------------------------------------

    [Fact]
    public void GenerateResponseMapsToTheTextCompletionShape()
    {
        var ollama = JsonSerializer.Deserialize<GenerateResponse>("""
        {"model":"llama3","response":"...and they lived.","done":true,"prompt_eval_count":5,"eval_count":6}
        """, JsonOptions)!;

        var response = ResponseTranslator.ToCompletion(ollama, "cmpl-x", 42, "llama3");

        Assert.Equal("text_completion", response.Object);
        Assert.Equal("cmpl-x", response.Id);
        Assert.Equal("...and they lived.", response.Choices[0].Text);
        Assert.Equal("stop", response.Choices[0].FinishReason);
        Assert.Equal(11, response.Usage!.TotalTokens);
    }
}
