using System.Net;
using System.Text;
using System.Text.Json;
using InferHub.Shared.Brio;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Backends.Colibri;

/// <summary>
/// One <c>POST /v1/brio</c> to colibri's gateway, and the engine's answer stated as a
/// <see cref="ToolResult"/> (94 D4/D5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The body goes as the caller wrote it.</b> No <c>cache_slot</c> is added: when it is absent the
/// gateway hashes <c>state</c> into a slot itself — 93 D2's rule keyed on the part that does not
/// change, already implemented where the KV lives. A second hash here could only disagree with it.
/// The phase-93 handler on this client still gives the body its <c>Content-Length</c> (93 D6).
/// </para>
/// <para>
/// <b>The loop is the engine's, deliberately.</b> colibri's own comment names the three things a
/// client that re-ran it would get wrong while staying plausible: photographing the shared prefix,
/// keeping the option list out of the prompt, and normalising by length.
/// </para>
/// </remarks>
internal static class BrioClient
{
    public const string Path = "brio";

    public static async Task<ToolResult> ScoreAsync(HttpClient http, ToolJob job, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            using var content = new StringContent(job.Payload, Encoding.UTF8, "application/json");
            response = await http.PostAsync(Path, content, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ToolResult.Failed(job.JobId, $"the colibri engine did not answer within {http.Timeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Failed(job.JobId, $"the colibri engine at {http.BaseAddress} is unreachable: {ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return ToolResult.Succeeded(job.JobId, body);
            }

            var message = ErrorMessage(body) ?? $"the colibri engine answered {(int)response.StatusCode}";

            // 93 D3: the engine's own queue is full. Not retried here — a second queue in front of
            // the first would be invisible to the hub's admission and its metrics.
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : 1;
                return ToolResult.Retry(job.JobId, message, retryAfter);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return ToolResult.Refused(job.JobId, message, BrioErrorCodes.ModelNotFound);
            }

            if ((int)response.StatusCode is >= 400 and < 500)
            {
                return ToolResult.Refused(job.JobId, message, ToolErrorCodes.InvalidRequest);
            }

            return ToolResult.Failed(job.JobId, message);
        }
    }

    /// <summary>The gateway's envelope is OpenAI's: <c>{"error":{"message":...}}</c>.</summary>
    internal static string? ErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("error", out var error)
                ? error.ValueKind switch
                {
                    JsonValueKind.Object when error.TryGetProperty("message", out var message)
                                              && message.ValueKind == JsonValueKind.String => message.GetString(),
                    JsonValueKind.String => error.GetString(),
                    _ => null
                }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
