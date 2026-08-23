using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using InferHub.Coordinator.Observability;
using InferHub.Shared.Anthropic;
using InferHub.Shared.Contracts;
using InferHub.Shared.Gemini;
using InferHub.Shared.OpenAi;
using InferHub.Shared.Upstream;

namespace InferHub.Coordinator.Services;

public interface IProviderDispatcher
{
    /// <summary>
    /// Whether this request may go to a provider. False for every request unless some enabled
    /// provider maps the model and that provider's trigger condition actually holds.
    /// </summary>
    bool ShouldServe(string model, bool hasCapableNode);

    Task<ProviderResult> DispatchAsync(
        string kind,
        string rawJson,
        string model,
        bool stream,
        CancellationToken cancellationToken);
}

/// <summary>Ollama-shaped, exactly like a node's — the endpoint formatters cannot tell.</summary>
/// <param name="ServedBy">
/// What the response header says: <c>fallback</c> for the projected legacy provider, and
/// <c>provider:&lt;id&gt;</c> for a named one (61 D4).
/// </param>
public sealed record ProviderResult(ChannelReader<InferenceChunk>? Stream, string? ResponseJson, string ServedBy);

/// <summary>
/// Forwards a request the fleet cannot serve to the provider that claimed the model. It is a proxy
/// hop, not a cache: the request body goes out in flight and the response streams straight through,
/// and the coordinator retains neither (rule 7, 22 D4).
///
/// The wire work is an <see cref="IUpstreamDialect"/> — <see cref="OpenAiUpstreamClient"/> for the
/// OpenAI-shaped upstreams, <see cref="AnthropicUpstreamClient"/> since phase 63 and
/// <see cref="GeminiUpstreamClient"/> since 64 — so the translation exists once per dialect and this
/// file gains one arm per vendor, not a branch per request.
/// </summary>
public sealed class ProviderDispatcher(
    IHttpClientFactory httpClientFactory,
    INodeRegistry registry,
    IProviderRegistry providers,
    Metrics metrics,
    ILogger<ProviderDispatcher> logger) : IProviderDispatcher
{
    public const string HttpClientName = "inferhub-provider";

    public bool ShouldServe(string model, bool hasCapableNode)
    {
        if (providers.Resolve(model) is not { } route)
        {
            return false;
        }

        if (!hasCapableNode)
        {
            return true;
        }

        return route.Definition.NormalizedTrigger() == FallbackOptions.TriggerNoNodeOrSaturated
            && IsSaturated(model);
    }

    public async Task<ProviderResult> DispatchAsync(
        string kind,
        string rawJson,
        string model,
        bool stream,
        CancellationToken cancellationToken)
    {
        var route = providers.Resolve(model)
            ?? throw new InvalidOperationException($"model '{model}' is not mapped to any provider");

        // The upstream knows the request by *its* name for the model, not ours.
        var upstreamJson = RewriteModel(rawJson, route.UpstreamModel);

        metrics.RecordProviderDispatched(route.Id, model);

        // Loud on purpose. A user must be able to find every request that left their machines, and
        // now also which vendor it went to.
        logger.LogInformation(
            "Provider dispatch: serving {Kind} for {Model} from provider '{Provider}' as {UpstreamModel}",
            kind,
            model,
            route.Id,
            route.UpstreamModel);

        if (!stream)
        {
            using var http = CreateHttpClient(route);
            var client = Dialect(route, http);

            var responseJson = kind == "chat"
                ? await client.ChatAsync(upstreamJson, cancellationToken)
                : await client.GenerateAsync(upstreamJson, cancellationToken);

            // Answer in the model the caller asked for; they never named the upstream one.
            return new ProviderResult(null, RewriteModel(responseJson, model), route.ServedBy);
        }

        return new ProviderResult(
            StreamAsync(kind, upstreamJson, model, route, cancellationToken),
            null,
            route.ServedBy);
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
        ProviderRoute route,
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
                using var http = CreateHttpClient(route);

                await foreach (var chunk in Dialect(route, http).StreamAsync(kind, upstreamJson, cancellationToken))
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
                logger.LogWarning(ex, "Provider '{Provider}' stream for {Model} failed", route.Id, model);

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

    // Saturation is defined once, in FleetSaturation — the request queue (phase 25) must agree
    // with a provider burst about what "full" means, or the two features fight at the boundary.
    private bool IsSaturated(string model) => FleetSaturation.IsSaturated(registry, model);

    /// <summary>
    /// One dialect per provider type. The switch is exhaustive because
    /// <see cref="ProviderOptionsValidator"/> already refused an unknown type at startup — a
    /// provider that failed to be understood must not become a request that fails at dispatch,
    /// hours later, in front of a user.
    /// </summary>
    private static IUpstreamDialect Dialect(ProviderRoute route, HttpClient http)
        => route.Definition.NormalizedType() switch
        {
            // Phase 62. OpenRouter *is* the OpenAI dialect — the identity is the claim, not an
            // implementation detail. What its type buys is configuration, one method down.
            ProviderDefinition.TypeOpenAiCompatible or ProviderDefinition.TypeOpenRouter
                => new OpenAiUpstreamClient(http),

            // Phase 63, and the opposite claim: Anthropic's /v1/messages is a real second dialect,
            // which is what 61 D3 extracted the interface for.
            ProviderDefinition.TypeAnthropic
                => new AnthropicUpstreamClient(http, route.Definition.MaxTokens),

            // Phase 64. The third dialect, and the one whose model id does not travel in the body
            // at all — it is a path segment, which is why nothing here rewrites a URL.
            ProviderDefinition.TypeGemini
                => new GeminiUpstreamClient(http, route.Definition.ThinkingBudget),

            var type => throw new InvalidOperationException($"provider type '{type}' has no dialect")
        };

    /// <summary>
    /// Each dialect configures its own client, because the credential is part of the dialect: a
    /// Bearer token sent to Anthropic is a 401 that reads like a bad key (63 D1).
    /// </summary>
    private HttpClient CreateHttpClient(ProviderRoute route)
    {
        var pooled = httpClientFactory.CreateClient(HttpClientName);

        if (route.Definition.NormalizedType() == ProviderDefinition.TypeAnthropic)
        {
            return AnthropicUpstreamClient.Configure(
                pooled,
                route.Definition.ResolvedBaseUrl()!,
                route.Definition.ApiKey,
                route.Definition.TimeoutSeconds,
                route.Definition.ResolvedAnthropicVersion());
        }

        if (route.Definition.NormalizedType() == ProviderDefinition.TypeGemini)
        {
            // A third credential header, and the third time a Bearer token would be a 401 that
            // reads like a bad key (63 D1's reason, Google's spelling).
            return GeminiUpstreamClient.Configure(
                pooled,
                route.Definition.ResolvedBaseUrl()!,
                route.Definition.ApiKey,
                route.Definition.TimeoutSeconds);
        }

        var http = OpenAiUpstreamClient.Configure(
            pooled,
            route.Definition.ResolvedBaseUrl()!,
            route.Definition.ApiKey,
            route.Definition.TimeoutSeconds);

        if (route.Definition.NormalizedType() == ProviderDefinition.TypeOpenRouter)
        {
            // Absent unless the operator wrote one down (62 D2): these two put a deployment on
            // OpenRouter's public rankings, and this hub does not volunteer somebody else's.
            AddIfPresent(http, "HTTP-Referer", route.Definition.Referer);
            AddIfPresent(http, "X-OpenRouter-Title", route.Definition.Title);
        }

        return http;
    }

    private static void AddIfPresent(HttpClient http, string header, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation(header, value.Trim());
        }
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
