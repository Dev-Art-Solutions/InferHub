using System.Text.Json;
using InferHub.Shared.Contracts;
using InferHub.Shared.OpenAi;

namespace InferHub.Shared.Brio;

/// <summary>Everything a caller can observe about a Brio answer, decided once for both hosts.</summary>
public sealed record BrioOutcome
{
    public int Status { get; init; }

    /// <summary>The engine's reply, verbatim, on a 200.</summary>
    public string? Json { get; init; }

    public string? Error { get; init; }

    public string ErrorType { get; init; } = OpenAiErrorTypes.ApiError;

    public string? ErrorCode { get; init; }

    public int? RetryAfterSeconds { get; init; }

    /// <summary>What the engine read — the shared prefix plus every option token (D3).</summary>
    public long Tokens { get; init; }

    public bool IsError => Status >= 400;
}

/// <summary>
/// The codes a node puts on a refused <c>score</c> job (phase 94, D5), beside
/// <see cref="ToolErrorCodes"/>.
/// </summary>
public static class BrioErrorCodes
{
    /// <summary>The engine serves one model and this was not it — a 404 at the edge, as for chat.</summary>
    public const string ModelNotFound = "model_not_found";
}

/// <summary>
/// <see cref="ToolResult"/> → <see cref="BrioOutcome"/>, the same renderer at the hub and on a solo
/// node (phase-37 D6's line: what a caller observes is decided once).
/// </summary>
/// <remarks>
/// Nothing here reads the error <em>text</em> to choose a status — the node stated the kind of
/// failure and this renders it, which is phase-29 D6 and 41's <c>RetryAfterSeconds</c> again.
/// </remarks>
public static class BrioRenderer
{
    public static BrioOutcome Render(ToolResult result)
    {
        if (result.Success)
        {
            var json = result.Payload ?? "";

            if (TokensRead(json) is not { } tokens)
            {
                // A 200 the engine did not shape as an answer is not an answer, and passing it on
                // would bill nothing for work that may have been done.
                return new BrioOutcome
                {
                    Status = 502,
                    Error = "the scoring engine returned something that is not a Brio answer"
                };
            }

            return new BrioOutcome { Status = 200, Json = json, Tokens = tokens };
        }

        if (result.RetryAfterSeconds is { } retryAfter)
        {
            return new BrioOutcome
            {
                Status = 503,
                Error = NodeErrorText.Readable(result.Error ?? "the scoring engine is busy"),
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

        return new BrioOutcome
        {
            Status = 502,
            Error = NodeErrorText.Readable(result.Error),
            ErrorCode = result.ErrorCode
        };
    }

    /// <summary>
    /// <c>usage.total_tokens</c>, or prompt + read when an engine leaves the total out; null when the
    /// reply is not a JSON object with a <c>usage</c> block at all.
    /// </summary>
    public static long? TokensRead(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (Count(usage, "total_tokens") is { } total)
            {
                return total;
            }

            return (Count(usage, "prompt_tokens") ?? 0) + (Count(usage, "read_tokens") ?? 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? Count(JsonElement usage, string name)
        => usage.TryGetProperty(name, out var value) && value.TryGetInt64(out var count) && count >= 0 ? count : null;
}
