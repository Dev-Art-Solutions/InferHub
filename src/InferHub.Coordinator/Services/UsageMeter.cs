using System.Text.Json;
using System.Threading.Channels;
using InferHub.Coordinator.Auth;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Attaches a client id to token counts the mesh already produces. Blocking responses carry
/// <c>prompt_eval_count</c>/<c>eval_count</c> in the body; streams carry them in the terminal
/// chunk. Nothing here reads message content — the parser touches exactly three fields.
///
/// <para>Mid-stream disconnect records <b>nothing</b>, deliberately. Ollama only reports counts
/// in the terminal chunk, so a stream that never delivers it has no numbers to record — and a
/// meter that invents numbers is worse than one that under-counts an aborted request. This is
/// the documented, tested choice.</para>
/// </summary>
public sealed class UsageMeter(
    IUsageLedger ledger,
    AdmissionControl admission,
    ILogger<UsageMeter> logger)
{
    /// <summary>Meter a blocking response. Failures are logged, never thrown — metering must not fail the request it meters.</summary>
    public void RecordResponse(ResolvedClient client, string kind, string model, string responseJson, bool fallback)
    {
        var (promptTokens, completionTokens) = ParseCounts(responseJson);

        if (promptTokens == 0 && completionTokens == 0)
        {
            return;
        }

        Record(client, kind, model, promptTokens, completionTokens, fallback);
    }

    /// <summary>
    /// Wraps a chunk stream in a pass-through that meters the terminal chunk and releases the
    /// client's concurrency lease when the stream ends, however it ends.
    /// </summary>
    public ChannelReader<InferenceChunk> WrapStream(
        ChannelReader<InferenceChunk> source,
        ResolvedClient client,
        string kind,
        string model,
        bool fallback,
        IDisposable? lease)
    {
        var channel = Channel.CreateUnbounded<InferenceChunk>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        _ = Task.Run(async () =>
        {
            InferenceChunk? terminal = null;

            try
            {
                await foreach (var chunk in source.ReadAllAsync(CancellationToken.None))
                {
                    if (chunk.Done)
                    {
                        terminal = chunk;
                    }

                    await channel.Writer.WriteAsync(chunk, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                return;
            }
            finally
            {
                lease?.Dispose();

                if (terminal is { } done)
                {
                    var (promptTokens, completionTokens) = ParseCounts(done.ResponseJson);
                    if (promptTokens != 0 || completionTokens != 0)
                    {
                        Record(client, kind, model, promptTokens, completionTokens, fallback);
                    }
                }
            }

            channel.Writer.TryComplete();
        }, CancellationToken.None);

        return channel.Reader;
    }

    /// <summary>Meter an embed response (<c>prompt_eval_count</c> only — embeddings generate nothing).</summary>
    public void RecordEmbedResponse(ResolvedClient client, string model, string responseJson)
    {
        var (promptTokens, _) = ParseCounts(responseJson);

        if (promptTokens == 0)
        {
            return;
        }

        Record(client, "embed", model, promptTokens, 0, fallback: false);
    }

    private void Record(ResolvedClient client, string kind, string model, long promptTokens, long completionTokens, bool fallback)
    {
        var now = DateTimeOffset.UtcNow;

        // Feed the client's rate windows first — admission must see this even if the ledger is slow.
        admission.RecordTokens(client.Id, promptTokens + completionTokens, now);

        try
        {
            var pending = ledger.RecordAsync(
                new UsageRecord(client.Id, model, kind, promptTokens, completionTokens, fallback, now));

            if (!pending.IsCompletedSuccessfully)
            {
                // A persistent ledger writes off the request path; a lost write is logged, not thrown.
                _ = Observe(pending);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Usage record for client {ClientId} was not written", client.Id);
        }

        async Task Observe(ValueTask task)
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Usage record for client {ClientId} was not written", client.Id);
            }
        }
    }

    // The one JSON parse in the usage path. It reads three integer fields and nothing else:
    // no messages, no response text, no hashes (rule 7).
    private static (long PromptTokens, long CompletionTokens) ParseCounts(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object)
            {
                return (0, 0);
            }

            long prompt = 0;
            long completion = 0;

            if (root.TryGetProperty("prompt_eval_count", out var promptElement)
                && promptElement.ValueKind is JsonValueKind.Number)
            {
                prompt = promptElement.GetInt64();
            }

            if (root.TryGetProperty("eval_count", out var evalElement)
                && evalElement.ValueKind is JsonValueKind.Number)
            {
                completion = evalElement.GetInt64();
            }

            return (Math.Max(0, prompt), Math.Max(0, completion));
        }
        catch (JsonException)
        {
            return (0, 0);
        }
    }
}
