using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Shared.Brio;
using InferHub.Shared.Contracts;
using InferHub.Shared.OpenAi;

namespace InferHub.Shared.LlamaCpp;

/// <summary>
/// llama.cpp's own routes, reached through the fleet as a <see cref="ToolJob"/> with capability
/// <see cref="CapabilityKinds.LlamaCpp"/> (phase 96, D4). The edge reads the operation and the model
/// and passes the body through; the engine validates the rest, as for Brio (94 D2).
/// </summary>
/// <remarks>
/// <para>
/// <b>An allowlist, not a proxy.</b> <c>/slots</c> answers with the prompt a slot last held (rule 7),
/// <c>/metrics</c> is the operator's, and <c>POST /lora-adapters</c> changes the scales every other
/// caller of that model gets. What is here is what one caller can ask without reading or changing
/// anybody else's request.
/// </para>
/// <para>
/// <b>No streaming.</b> <c>/v1/completions</c> and <c>/api/generate</c> stream from the same engine;
/// a <c>stream: true</c> here is refused with that sentence rather than buffered into one answer the
/// caller did not ask for.
/// </para>
/// </remarks>
public static class LlamaCppNative
{
    /// <summary>The routes a caller POSTs a body to.</summary>
    public static readonly IReadOnlyList<string> PostOperations =
        ["completion", "infill", "tokenize", "detokenize", "apply-template", "embedding"];

    /// <summary>The routes a caller GETs with <c>?model=</c>.</summary>
    public static readonly IReadOnlyList<string> GetOperations = ["props"];

    /// <summary>The two that generate — the ones metered in tokens.</summary>
    public static bool Generates(string operation) => operation is "completion" or "infill";

    public static bool IsPost(string? operation)
        => operation is not null && PostOperations.Contains(operation, StringComparer.Ordinal);

    public static bool IsGet(string? operation)
        => operation is not null && GetOperations.Contains(operation, StringComparer.Ordinal);

    /// <summary>The <see cref="ToolJob.Payload"/>: the operation and the caller's body, untouched.</summary>
    public static string Payload(string operation, string? body)
    {
        var node = new JsonObject { ["operation"] = operation };

        if (body is not null)
        {
            node["body"] = JsonNode.Parse(body);
        }

        return node.ToJsonString();
    }

    /// <summary>The node's half of <see cref="Payload"/>. Null when it is not one.</summary>
    public static (string Operation, string? Body)? ReadPayload(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("operation", out var operation)
                || operation.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var name = operation.GetString()!;

            if (!IsPost(name) && !IsGet(name))
            {
                return null;
            }

            return (name, root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object
                ? body.GetRawText()
                : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the edge reads of a POST body: that it is an object, names a model, and does not ask to
    /// stream. Null with <paramref name="error"/> set otherwise.
    /// </summary>
    public static string? ModelOf(string raw, out string error)
    {
        error = "";

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "request body must be a JSON object";
                return null;
            }

            if (!root.TryGetProperty("model", out var model)
                || model.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(model.GetString()))
            {
                error = "model is required: the fleet routes a llama.cpp call to the node that serves it";
                return null;
            }

            if (root.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True)
            {
                error = "the llama.cpp routes answer once; stream through /v1/completions or /api/generate, which reach the same engine";
                return null;
            }

            return model.GetString()!.Trim();
        }
        catch (JsonException)
        {
            error = "request body must be a JSON object";
            return null;
        }
    }

    /// <summary>
    /// <c>tokens_evaluated</c> and <c>tokens_predicted</c> from a <c>/completion</c> or <c>/infill</c>
    /// answer; zeros for anything else, which is not billed.
    /// </summary>
    public static (long Prompt, long Completion) Tokens(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return (0, 0);
            }

            return (Count(root, "tokens_evaluated"), Count(root, "tokens_predicted"));
        }
        catch (JsonException)
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// <see cref="ToolResult"/> → what the caller observes, the same at the hub and on a solo node.
    /// The node stated the kind of failure; nothing here reads the error text to pick a status.
    /// </summary>
    public static BrioOutcome Render(ToolResult result)
    {
        if (result.Success)
        {
            return new BrioOutcome { Status = 200, Json = result.Payload ?? "{}" };
        }

        if (result.RetryAfterSeconds is { } retryAfter)
        {
            return new BrioOutcome
            {
                Status = 503,
                Error = NodeErrorText.Readable(result.Error ?? "the llama.cpp engine is busy"),
                ErrorCode = "engine_busy",
                RetryAfterSeconds = retryAfter
            };
        }

        if (string.Equals(result.ErrorCode, BrioErrorCodes.ModelNotFound, StringComparison.Ordinal))
        {
            return new BrioOutcome
            {
                Status = 404,
                Error = NodeErrorText.Readable(result.Error),
                ErrorType = OpenAiErrorTypes.NotFound,
                ErrorCode = BrioErrorCodes.ModelNotFound
            };
        }

        if (ToolErrorCodes.IsClientError(result.ErrorCode))
        {
            return new BrioOutcome
            {
                Status = 400,
                Error = NodeErrorText.Readable(result.Error),
                ErrorType = OpenAiErrorTypes.InvalidRequest,
                ErrorCode = result.ErrorCode
            };
        }

        return new BrioOutcome { Status = 502, Error = NodeErrorText.Readable(result.Error), ErrorCode = result.ErrorCode };
    }

    private static long Count(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var count) && count >= 0 ? count : 0;
}
