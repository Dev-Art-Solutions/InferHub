using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using InferHub.Coordinator.Observability;
using InferHub.Shared.Contracts;
using InferHub.Shared.OpenAi;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

public interface IFallbackDispatcher
{
    /// <summary>
    /// Whether this request may burst. False for every request unless fallback is enabled, the
    /// model is mapped and allowed, and the trigger condition actually holds.
    /// </summary>
    bool ShouldServe(string model, bool hasCapableNode);

    Task<FallbackResult> DispatchAsync(
        string kind,
        string rawJson,
        string model,
        bool stream,
        CancellationToken cancellationToken);
}

/// <summary>Ollama-shaped, exactly like a node's — the endpoint formatters cannot tell.</summary>
public sealed record FallbackResult(ChannelReader<InferenceChunk>? Stream, string? ResponseJson);

/// <summary>
/// Forwards a request the fleet cannot serve to a configured OpenAI-compatible upstream. It is a
/// proxy hop, not a cache: the request body goes out in flight and the response streams straight
/// through, and the coordinator retains neither (rule 7).
///
/// The wire work is <see cref="OpenAiUpstreamClient"/>, the same class the node's OpenAI backend
/// drives — the translation exists once.
/// </summary>
public sealed class FallbackDispatcher(
    IHttpClientFactory httpClientFactory,
    INodeRegistry registry,
    IOptions<FallbackOptions> options,
    Metrics metrics,
    ILogger<FallbackDispatcher> logger) : IFallbackDispatcher
{
    public const string HttpClientName = "inferhub-fallback";

    public bool ShouldServe(string model, bool hasCapableNode)
    {
        var fallback = options.Value;

        if (!fallback.Enabled || string.IsNullOrWhiteSpace(fallback.BaseUrl))
        {
            return false;
        }

        if (ResolveUpstreamModel(model) is null)
        {
            return false;
        }

        if (!hasCapableNode)
        {
            return true;
        }

        return fallback.NormalizedTrigger() == FallbackOptions.TriggerNoNodeOrSaturated
            && IsSaturated(model);
    }

    public async Task<FallbackResult> DispatchAsync(
        string kind,
        string rawJson,
        string model,
        bool stream,
        CancellationToken cancellationToken)
    {
        var upstreamModel = ResolveUpstreamModel(model)
            ?? throw new InvalidOperationException($"model '{model}' is not mapped for fallback");

        // The upstream knows the request by *its* name for the model, not ours.
        var upstreamJson = RewriteModel(rawJson, upstreamModel);

        metrics.RecordFallbackDispatched(model);

        // Loud on purpose. A user must be able to find every request that left their machines.
        logger.LogInformation(
            "Cloud burst: no node for {Model}; serving {Kind} from the fallback upstream as {UpstreamModel}",
            model,
            kind,
            upstreamModel);

        if (!stream)
        {
            using var http = CreateHttpClient();
            var client = new OpenAiUpstreamClient(http);

            var responseJson = kind == "chat"
                ? await client.ChatAsync(upstreamJson, cancellationToken)
                : await client.GenerateAsync(upstreamJson, cancellationToken);

            // Answer in the model the caller asked for; they never named the upstream one.
            return new FallbackResult(null, RewriteModel(responseJson, model));
        }

        return new FallbackResult(StreamAsync(kind, upstreamJson, model, cancellationToken), null);
    }

    /// <summary>
    /// Pumps the upstream stream into the same channel shape the dispatcher hands back for a
    /// node, so <c>StreamingInferenceResult</c> and <c>OpenAiStreamingResult</c> need no idea
    /// this came from anywhere else. Nothing is buffered beyond one chunk in flight.
    /// </summary>
    private ChannelReader<InferenceChunk> StreamAsync(
        string kind,
        string upstreamJson,
        string model,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<InferenceChunk>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        var jobId = Guid.NewGuid();

        _ = Task.Run(async () =>
        {
            try
            {
                using var http = CreateHttpClient();

                await foreach (var chunk in new OpenAiUpstreamClient(http)
                    .StreamAsync(kind, upstreamJson, cancellationToken))
                {
                    var responseJson = RewriteModel(chunk, model);
                    var done = IsDone(responseJson);

                    await channel.Writer.WriteAsync(
                        new InferenceChunk(jobId, responseJson, done),
                        cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // The client walked away. Nothing to say and nobody to say it to.
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cloud burst stream for {Model} failed", model);

                // Same contract the node path honours: a terminal error chunk beats a hung stream.
                await channel.Writer.WriteAsync(
                    new InferenceChunk(jobId, ErrorChunk(ex.Message), true),
                    CancellationToken.None);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        return channel.Reader;
    }

    /// <summary>
    /// Every node advertising the model is already at its declared cap. A node that declared no
    /// cap is never saturated — we have no number to compare against, and inventing one would
    /// burst to a paid upstream on a guess.
    /// </summary>
    private bool IsSaturated(string model)
    {
        var capable = registry.FindNodesWithModel(model);

        if (capable.Count == 0)
        {
            return true;
        }

        var snapshots = registry
            .Snapshot(DateTimeOffset.UtcNow)
            .ToDictionary(node => node.ConnectionId, StringComparer.Ordinal);

        foreach (var node in capable)
        {
            if (!snapshots.TryGetValue(node.ConnectionId, out var snapshot)
                || snapshot.MaxConcurrency is not { } cap
                || snapshot.LocalInFlight < cap)
            {
                return false;
            }
        }

        return true;
    }

    private string? ResolveUpstreamModel(string model)
    {
        var fallback = options.Value;

        if (!fallback.ModelMap.TryGetValue(model, out var upstreamModel)
            || string.IsNullOrWhiteSpace(upstreamModel))
        {
            return null;
        }

        if (fallback.AllowedModels.Count > 0
            && !fallback.AllowedModels.Any(allowed =>
                string.Equals(allowed?.Trim(), model, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return upstreamModel;
    }

    private HttpClient CreateHttpClient()
    {
        var fallback = options.Value;

        return OpenAiUpstreamClient.Configure(
            httpClientFactory.CreateClient(HttpClientName),
            fallback.BaseUrl!,
            fallback.ApiKey,
            fallback.TimeoutSeconds);
    }

    // The body already exists as a string; swapping one field is cheaper and safer than a
    // round-trip through a typed DTO that would drop fields it does not know about.
    private static string RewriteModel(string json, string model)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject body)
            {
                return json;
            }

            body["model"] = JsonValue.Create(model);
            return body.ToJsonString();
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static bool IsDone(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            return document.RootElement.TryGetProperty("done", out var done)
                && done.ValueKind is JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ErrorChunk(string message)
        => JsonSerializer.Serialize(new { error = message, done = true });
}
