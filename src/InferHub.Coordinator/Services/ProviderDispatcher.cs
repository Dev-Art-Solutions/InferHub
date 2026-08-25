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
    /// Whether this request goes to a provider, whether the fleet may still catch it if that call
    /// fails, and whether the request is refused outright. <see cref="ProviderDecision.No"/> for
    /// every request unless some enabled provider maps the model and its policy holds (phase 65).
    /// </summary>
    ProviderDecision Decide(string model, bool hasCapableNode, ProviderSteer steer);

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
/// The routing answer for one request (phase 65): serve it from a provider, leave it to the fleet,
/// or refuse it before anything leaves the hub.
/// </summary>
/// <param name="NodeIsBackstop">
/// Whether a capable node may still answer if the provider call fails. True for the overflow
/// policies and for <c>prefer</c>; <b>false</b> for <c>only</c> and for a request that named a
/// provider by header — answering those from the fleet would be the hub overruling an instruction it
/// had already accepted (65 D3).
/// </param>
public readonly record struct ProviderDecision(
    bool Serve,
    bool NodeIsBackstop,
    int? ErrorStatus,
    string? ErrorMessage)
{
    /// <summary>The fleet's, as it is for every model on a hub that configured no provider.</summary>
    public static readonly ProviderDecision No = new(false, false, null, null);

    public static ProviderDecision Yes(bool nodeIsBackstop) => new(true, nodeIsBackstop, null, null);

    public static ProviderDecision Refuse(int status, string message) => new(false, false, status, message);

    public bool IsRefusal => ErrorStatus is not null;
}

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

    /// <summary>
    /// One place decides, because the two client dialects and the two failure paths must agree
    /// (phase 65). The order is: a <c>node</c> steer wins over any policy, then the map is
    /// consulted, then a named steer is checked against what the map already permits, and only then
    /// does the policy get a say.
    /// </summary>
    public ProviderDecision Decide(string model, bool hasCapableNode, ProviderSteer steer)
    {
        // The privacy direction, and it is answered before anything else is looked up: a caller who
        // said "keep this one local" gets that on a hub with four providers and on a hub with none.
        if (steer.NodeOnly)
        {
            return ProviderDecision.No;
        }

        if (providers.Resolve(model) is not { } route)
        {
            // A steer can never create a route the configuration does not contain (65 D4, track D4).
            return steer.ProviderId is { } wanted
                ? Refused(wanted, model)
                : ProviderDecision.No;
        }

        if (steer.ProviderId is { } named)
        {
            // Deliberately the same sentence for an unknown id, a disabled one and a real one that
            // maps a different model: a client with a key must not be able to enumerate the
            // operator's vendors by probing. /api/status answers that, and is admin-gated.
            if (!string.Equals(named, route.Id, StringComparison.OrdinalIgnoreCase) || route.Legacy)
            {
                return Refused(named, model);
            }

            return ProviderDecision.Yes(nodeIsBackstop: false);
        }

        return route.PolicyFor(model) switch
        {
            // Asked always. A node holding the same name never serves it, which is the whole point:
            // it is the answer to a collision between a local model and a provider's, not a louder
            // `prefer`.
            ProviderPolicy.Only => ProviderDecision.Yes(nodeIsBackstop: false),

            // Asked first, fleet as the backstop. Falling back to a local node is not a second
            // disclosure, so this one may do it quietly (65 D3).
            ProviderPolicy.Prefer => ProviderDecision.Yes(nodeIsBackstop: true),

            _ when !hasCapableNode => ProviderDecision.Yes(nodeIsBackstop: false),

            // Saturation burst is an optimisation, not a promise — hence the backstop.
            ProviderPolicy.NoNodeOrSaturated when IsSaturated(model)
                => ProviderDecision.Yes(nodeIsBackstop: true),

            _ => ProviderDecision.No
        };
    }

    /// <summary>
    /// The one refusal sentence a steered request can get. It names the pair the caller typed and
    /// nothing else (65 D4).
    /// </summary>
    /// <summary>
    /// The refusal, counted on the way out (phase 66). Counting it here rather than at the two
    /// endpoints is what keeps the number honest: <see cref="Decide"/> is the one place that can
    /// refuse, and a second call site would eventually refuse without counting.
    /// </summary>
    private ProviderDecision Refused(string providerId, string model)
    {
        metrics.RecordProviderRefused();
        return ProviderDecision.Refuse(StatusCodes.Status400BadRequest, Unserved(providerId, model));
    }

    private static string Unserved(string providerId, string model)
        => $"no provider '{providerId}' serves model '{model}' on this hub. The "
           + $"{ProviderSteer.HeaderName} header can only choose among the providers already "
           + $"configured for a model; '{ProviderSteer.NodeValue}' keeps the request on the fleet.";

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

            string responseJson;

            try
            {
                responseJson = kind == "chat"
                    ? await client.ChatAsync(upstreamJson, cancellationToken)
                    : await client.GenerateAsync(upstreamJson, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Phase 66. The caller still gets the exception — InferenceCore decides whether a
                // node may catch this request — but the vendor's own sentence stops here, on the
                // console, instead of only in a log line nobody is tailing.
                metrics.RecordProviderFailed(route.Id, model, ex.Message);
                throw;
            }

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

                // A mid-stream failure is the one the fleet cannot catch — the headers are already
                // out and the caller is reading chunks — so it is the failure most worth counting.
                metrics.RecordProviderFailed(route.Id, model, ex.Message);

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
