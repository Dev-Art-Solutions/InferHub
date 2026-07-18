using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using InferHub.Coordinator.OpenAi;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

public class OpenAiStreamingTests
{
    private const string Id = "chatcmpl-test";
    private const string Model = "llama3";

    private static InferenceChunk Chunk(string content, bool done = false)
        => new(
            Guid.NewGuid(),
            $$"""{"model":"llama3","message":{"role":"assistant","content":"{{content}}"},"done":{{(done ? "true" : "false")}}}""",
            done);

    private static InferenceChunk TerminalChunk(int promptTokens, int evalTokens)
        => new(
            Guid.NewGuid(),
            $$"""
            {"model":"llama3","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","prompt_eval_count":{{promptTokens}},"eval_count":{{evalTokens}}}
            """,
            true);

    // A terminal Ollama chunk carrying a tool call, the way Ollama emits it: on the final
    // message chunk (done:true), arguments as a nested object, no call id of its own.
    private static InferenceChunk ToolCallTerminalChunk()
        => new(
            Guid.NewGuid(),
            """
            {"model":"llama3","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"get_weather","arguments":{"city":"Sofia"}}}]},"done":true,"done_reason":"stop","prompt_eval_count":11,"eval_count":7}
            """,
            true);

    // The way the live qwen2.5 node actually streams a call: tool_calls on a NON-terminal
    // chunk (done:false), with the done flag arriving on a separate later chunk.
    private static InferenceChunk ToolCallDeltaChunk()
        => new(
            Guid.NewGuid(),
            """
            {"model":"llama3","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"get_weather","arguments":{"city":"Sofia"}}}]},"done":false}
            """,
            false);

    private static async Task<string> RunAsync(
        IEnumerable<InferenceChunk> chunks,
        bool includeUsage = false,
        Exception? failWith = null)
    {
        var channel = Channel.CreateUnbounded<InferenceChunk>();

        foreach (var chunk in chunks)
        {
            await channel.Writer.WriteAsync(chunk);
        }

        if (failWith is not null)
        {
            channel.Writer.TryComplete(failWith);
        }
        else
        {
            channel.Writer.TryComplete();
        }

        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        var result = new OpenAiStreamingResult(
            channel.Reader,
            new ChatStreamFormatter(Id, 1700000000, Model, includeUsage),
            NullLogger.Instance);

        await result.ExecuteAsync(context);

        Assert.Equal("text/event-stream", context.Response.ContentType);
        return Encoding.UTF8.GetString(body.ToArray());
    }

    private static IReadOnlyList<string> DataFrames(string sse)
        => sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(frame => frame.Trim())
            .Where(frame => frame.StartsWith("data: ", StringComparison.Ordinal))
            .Select(frame => frame["data: ".Length..])
            .ToArray();

    [Fact]
    public async Task EachFrameIsPrefixedAndBlankLineSeparated()
    {
        var sse = await RunAsync([Chunk("He"), Chunk("llo"), TerminalChunk(3, 2)]);

        Assert.StartsWith("data: ", sse);
        Assert.Contains("\n\n", sse);

        // Every non-empty line must be a data line — no bare JSON, no stray events.
        foreach (var line in sse.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.StartsWith("data: ", line);
        }
    }

    [Fact]
    public async Task StreamTerminatesWithTheDoneSentinel()
    {
        var sse = await RunAsync([Chunk("hi"), TerminalChunk(1, 1)]);

        Assert.EndsWith("data: [DONE]\n\n", sse);
        Assert.Equal("[DONE]", DataFrames(sse)[^1]);
    }

    [Fact]
    public async Task FirstDeltaCarriesRoleAndLaterDeltasDoNot()
    {
        var sse = await RunAsync([Chunk("He"), Chunk("llo"), TerminalChunk(3, 2)]);
        var frames = DataFrames(sse);

        var first = JsonDocument.Parse(frames[0]).RootElement;
        Assert.Equal("chat.completion.chunk", first.GetProperty("object").GetString());
        Assert.Equal(Id, first.GetProperty("id").GetString());
        var firstDelta = first.GetProperty("choices")[0].GetProperty("delta");
        Assert.Equal("assistant", firstDelta.GetProperty("role").GetString());
        Assert.Equal("He", firstDelta.GetProperty("content").GetString());

        var second = JsonDocument.Parse(frames[1]).RootElement;
        var secondDelta = second.GetProperty("choices")[0].GetProperty("delta");
        Assert.False(secondDelta.TryGetProperty("role", out _));
        Assert.Equal("llo", secondDelta.GetProperty("content").GetString());
    }

    [Fact]
    public async Task EveryChunkSharesTheSameCompletionId()
    {
        var sse = await RunAsync([Chunk("a"), Chunk("b"), TerminalChunk(1, 2)]);

        foreach (var frame in DataFrames(sse).Where(f => f != "[DONE]"))
        {
            Assert.Equal(Id, JsonDocument.Parse(frame).RootElement.GetProperty("id").GetString());
        }
    }

    [Fact]
    public async Task TerminalChunkCarriesFinishReason()
    {
        var sse = await RunAsync([Chunk("hi"), TerminalChunk(1, 1)]);
        var frames = DataFrames(sse);

        // Last frame is [DONE]; the one before it is the terminal chunk.
        var terminal = JsonDocument.Parse(frames[^2]).RootElement;
        Assert.Equal("stop", terminal.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public async Task NoUsageChunkUnlessIncludeUsageIsSet()
    {
        var sse = await RunAsync([Chunk("hi"), TerminalChunk(7, 3)], includeUsage: false);

        foreach (var frame in DataFrames(sse).Where(f => f != "[DONE]"))
        {
            Assert.False(JsonDocument.Parse(frame).RootElement.TryGetProperty("usage", out _));
        }
    }

    [Fact]
    public async Task IncludeUsageEmitsAUsageOnlyChunkBeforeDone()
    {
        var sse = await RunAsync([Chunk("hi"), TerminalChunk(7, 3)], includeUsage: true);
        var frames = DataFrames(sse);

        Assert.Equal("[DONE]", frames[^1]);

        var usageFrame = JsonDocument.Parse(frames[^2]).RootElement;
        Assert.Empty(usageFrame.GetProperty("choices").EnumerateArray());

        var usage = usageFrame.GetProperty("usage");
        Assert.Equal(7, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(3, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(10, usage.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task ToolCallStreamsAsADeltaToolCallsFrameThenFinishReasonToolCalls()
    {
        var sse = await RunAsync([Chunk("Let me check"), ToolCallTerminalChunk()]);
        var frames = DataFrames(sse);

        // Last frame is [DONE]; the one before it carries the tool call and the finish_reason.
        var terminal = JsonDocument.Parse(frames[^2]).RootElement;
        var choice = terminal.GetProperty("choices")[0];
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());

        var delta = choice.GetProperty("delta");

        // A tool-call delta and a text delta are not the same frame: content is null/absent.
        Assert.False(
            delta.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null);

        var call = delta.GetProperty("tool_calls")[0];
        Assert.Equal(0, call.GetProperty("index").GetInt32());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.StartsWith("call_", call.GetProperty("id").GetString());

        var function = call.GetProperty("function");
        Assert.Equal("get_weather", function.GetProperty("name").GetString());

        // Arguments are a JSON *string*, as the OpenAI SDK's ChoiceDeltaToolCall expects.
        var arguments = function.GetProperty("arguments");
        Assert.Equal(JsonValueKind.String, arguments.ValueKind);
        Assert.Equal("""{"city":"Sofia"}""", arguments.GetString());
    }

    [Fact]
    public async Task ToolCallOnANonTerminalChunkStillResolvesFinishReasonToolCallsOnTheTerminalChunk()
    {
        // Regression for the shape the real Ollama node emits: the tool call lands on a
        // done:false chunk and the terminal chunk carries no tool_calls. A stateless
        // per-chunk finish_reason would have said "stop" here, and a strict agent loop keys
        // tool execution off finish_reason == "tool_calls".
        var sse = await RunAsync([ToolCallDeltaChunk(), TerminalChunk(11, 7)]);
        var frames = DataFrames(sse);

        // The tool-call frame carries the call, finish_reason still null.
        var callFrame = JsonDocument.Parse(frames[0]).RootElement.GetProperty("choices")[0];
        Assert.True(callFrame.GetProperty("finish_reason").ValueKind == JsonValueKind.Null);
        var call = callFrame.GetProperty("delta").GetProperty("tool_calls")[0];
        Assert.Equal(0, call.GetProperty("index").GetInt32());
        Assert.Equal("get_weather", call.GetProperty("function").GetProperty("name").GetString());

        // The terminal frame (before [DONE]) resolves to tool_calls, not stop.
        var terminal = JsonDocument.Parse(frames[^2]).RootElement.GetProperty("choices")[0];
        Assert.Equal("tool_calls", terminal.GetProperty("finish_reason").GetString());
    }

    [Fact]
    public async Task OrdinaryTextDeltasCarryNoToolCallsField()
    {
        var sse = await RunAsync([Chunk("He"), Chunk("llo"), TerminalChunk(3, 2)]);

        foreach (var frame in DataFrames(sse).Where(f => f != "[DONE]"))
        {
            var choices = JsonDocument.Parse(frame).RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0)
            {
                continue;
            }

            Assert.False(choices[0].GetProperty("delta").TryGetProperty("tool_calls", out _));
        }
    }

    [Fact]
    public async Task NodeLossMidStreamClosesTheStreamInsteadOfHanging()
    {
        // The client already holds a 200 and a partial answer. Truncate honestly and close.
        var sse = await RunAsync(
            [Chunk("par"), Chunk("tial")],
            failWith: new NodeDisconnectedException("conn-1", "node went away"));

        var frames = DataFrames(sse);

        Assert.Equal("[DONE]", frames[^1]);

        var truncation = JsonDocument.Parse(frames[^2]).RootElement;
        Assert.Equal("stop", truncation.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public async Task NodeLossStillEmitsTheContentReceivedSoFar()
    {
        var sse = await RunAsync(
            [Chunk("par"), Chunk("tial")],
            failWith: new NodeDisconnectedException("conn-1", "node went away"));

        var frames = DataFrames(sse);
        var content = frames
            .Where(f => f != "[DONE]")
            .Select(f => JsonDocument.Parse(f).RootElement.GetProperty("choices"))
            .Where(choices => choices.GetArrayLength() > 0)
            .Select(choices => choices[0].GetProperty("delta"))
            .Where(delta => delta.TryGetProperty("content", out _))
            .Select(delta => delta.GetProperty("content").GetString())
            .ToArray();

        Assert.Equal("partial", string.Concat(content));
    }
}
