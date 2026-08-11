using System.Net.Http.Json;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Services;
using InferHub.Node.Capabilities;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 40. The unit of routing is <c>(capability, model)</c>, and the load-bearing test in here
/// is not the new behaviour — it is <see cref="ANodeThatDeclaresNothingIsRoutedExactlyAsBefore"/>.
/// A fleet is upgraded one box at a time, so the old registration shape has to keep working while
/// the new one exists beside it.
/// </summary>
public class CapabilityRoutingTests
{
    [Fact]
    public void ANodeThatDeclaresNothingIsRoutedExactlyAsBefore()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        // A v3.7 node: no Capabilities on either message, because the field did not exist.
        registry.Upsert("conn-old", Registration("node-old"), now);
        registry.ReportModels("conn-old", new NodeModels("node-old", [Model("llama3")], now), now);

        Assert.Single(registry.FindNodesWithModel("llama3"));
        Assert.Single(registry.FindNodesWithModel("llama3", CapabilityKinds.Chat));
        Assert.Single(registry.FindNodesWithModel("llama3", CapabilityKinds.Embed));

        var node = Assert.Single(registry.Snapshot(now));
        Assert.Equal([CapabilityKinds.Chat, CapabilityKinds.Embed], node.Capabilities!.Select(c => c.Kind));
    }

    [Fact]
    public void AnEmbedOnlyNodeIsNotAChatCandidate()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        registry.Upsert("conn-embed", Registration("node-embed"), now);
        registry.ReportModels(
            "conn-embed",
            new NodeModels("node-embed", [Model("shared")], now, [new NodeCapability(CapabilityKinds.Embed, ["shared"])]),
            now);

        Assert.Empty(registry.FindNodesWithModel("shared", CapabilityKinds.Chat));
        Assert.Single(registry.FindNodesWithModel("shared", CapabilityKinds.Embed));

        // The capability-less question is unchanged: the node does hold the model, and that is
        // what saturation and model placement ask about.
        Assert.Single(registry.FindNodesWithModel("shared"));
    }

    [Fact]
    public void ChatRoutesPastAnEmbedOnlyNodeToOneThatCanServeIt()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        registry.Upsert("conn-embed", Registration("node-embed"), now);
        registry.ReportModels(
            "conn-embed",
            new NodeModels("node-embed", [Model("shared")], now, [new NodeCapability(CapabilityKinds.Embed, ["shared"])]),
            now);

        registry.Upsert("conn-chat", Registration("node-chat"), now);
        registry.ReportModels(
            "conn-chat",
            new NodeModels("node-chat", [Model("shared")], now, [new NodeCapability(CapabilityKinds.Chat, ["shared"])]),
            now);

        var router = NewRouter(registry);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal("node-chat", router.Route("shared", capability: CapabilityKinds.Chat)?.NodeId);
            Assert.Equal("node-embed", router.Route("shared", capability: CapabilityKinds.Embed)?.NodeId);
        }
    }

    [Fact]
    public async Task AModelNobodyServesForThisCapabilityIs503WithRetryAfter()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        registry.Upsert("conn-embed", Registration("node-embed"), now);
        registry.ReportModels(
            "conn-embed",
            new NodeModels("node-embed", [Model("shared")], now, [new NodeCapability(CapabilityKinds.Embed, ["shared"])]),
            now);

        var outcome = await Dispatch(registry, "chat", "shared");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, outcome.ErrorStatus);
        Assert.Contains("chat", outcome.ErrorMessage);
        Assert.Contains("shared", outcome.ErrorMessage);
        Assert.NotNull(outcome.RetryAfterSeconds);
    }

    [Fact]
    public async Task AModelNobodyHoldsAtAllIsStillTheOriginal404()
    {
        var registry = new NodeRegistry();

        var outcome = await Dispatch(registry, "chat", "nothing-holds-this");

        // Byte-for-byte the message every release since 1.0 has returned: "not found" must not
        // start meaning "not right now" for a model that genuinely is not there.
        Assert.Equal(StatusCodes.Status404NotFound, outcome.ErrorStatus);
        Assert.Equal("model 'nothing-holds-this' not found", outcome.ErrorMessage);
        Assert.Null(outcome.RetryAfterSeconds);
    }

    [Fact]
    public async Task AClientOverItsLimitsIsRejectedBeforeTheCapabilityIsConsidered()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        registry.Upsert("conn-embed", Registration("node-embed"), now);
        registry.ReportModels(
            "conn-embed",
            new NodeModels("node-embed", [Model("shared")], now, [new NodeCapability(CapabilityKinds.Embed, ["shared"])]),
            now);

        // A client scoped to another model entirely. Phase-25 D4 says that is a 404 identical to
        // a model that does not exist — and it must stay a 404, or the capability 503 becomes a
        // way to ask which models exist behind a scope you were not given.
        var client = new InferHub.Coordinator.Auth.ResolvedClient(
            "scoped",
            new InferHub.Coordinator.Auth.ClientLimits { AllowedModels = ["something-else"] },
            null);

        var outcome = await Dispatch(registry, "chat", "shared", client);

        Assert.Equal(StatusCodes.Status404NotFound, outcome.ErrorStatus);
    }

    [Fact]
    public void CordonedNodesDoNotAppearInTheFleetCapabilitySummary()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        registry.Upsert("conn-a", Registration("node-a"), now);
        registry.ReportModels("conn-a", new NodeModels("node-a", [Model("llama3")], now), now);

        var before = Assert.Single(registry.CapabilitySummary(), s => s.Capability == CapabilityKinds.Chat);
        Assert.Equal(1, before.Nodes);
        Assert.Equal(["llama3"], before.Models);

        registry.Cordon("node-a");

        Assert.Empty(registry.CapabilitySummary());
    }

    [Fact]
    public void ADeclarationSurvivesAReRegistrationThatDoesNotCarryOne()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        registry.Upsert("conn-a", Registration("node-a"), now);
        registry.ReportModels(
            "conn-a",
            new NodeModels("node-a", [Model("shared")], now, [new NodeCapability(CapabilityKinds.Embed, ["shared"])]),
            now);

        // A reconnect re-registers before it re-reports. If that wiped the declaration there
        // would be a window in which an embed-only node takes chat traffic.
        registry.Upsert("conn-a", Registration("node-a"), now.AddSeconds(1));

        Assert.Empty(registry.FindNodesWithModel("shared", CapabilityKinds.Chat));
    }

    [Fact]
    public void AnUnknownCapabilityKindIsCarriedAndSimplyNeverMatched()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        registry.Upsert("conn-a", Registration("node-a"), now);
        registry.ReportModels(
            "conn-a",
            new NodeModels("node-a", [Model("wav2vec")], now, [new NodeCapability("dance", ["wav2vec"])]),
            now);

        // The mesh carries any string (D1) — a node running something this hub has never heard of
        // registers normally, is visible, and is routed for nothing it did not claim.
        Assert.Single(registry.FindNodesWithModel("wav2vec", "dance"));
        Assert.Empty(registry.FindNodesWithModel("wav2vec", CapabilityKinds.Chat));
        Assert.Equal("dance", Assert.Single(registry.CapabilitySummary()).Capability);
    }

    [Fact]
    public void TheNodeDeclaresWhatItHoldsAndTheOperatorCanSubtract()
    {
        IReadOnlyList<ModelInfo> models = [Model("llama3"), Model("nomic-embed-text")];

        var both = BackendCapabilities.Declare(models, new CapabilityOptions());
        Assert.Equal([CapabilityKinds.Chat, CapabilityKinds.Embed], both.Select(c => c.Kind));
        Assert.All(both, capability => Assert.Equal(["llama3", "nomic-embed-text"], capability.Models));

        var embedOnly = BackendCapabilities.Declare(models, new CapabilityOptions { Disabled = ["chat"] });
        Assert.Equal(CapabilityKinds.Embed, Assert.Single(embedOnly).Kind);

        // No models is already how a node is unrouted (phase-36 D7); it does not also need to
        // declare capabilities over nothing.
        Assert.Empty(BackendCapabilities.Declare([], new CapabilityOptions()));
    }

    [Fact]
    public void DisablingEveryKindTheBackendHasIsAStartupFailure()
    {
        var validator = new NodeOptionsValidator();

        var bothOff = validator.Validate(null, new NodeOptions
        {
            Capabilities = new CapabilityOptions { Disabled = ["chat", "embed"] }
        });

        Assert.True(bothOff.Failed);
        Assert.Contains(bothOff.Failures!, failure => failure.Contains("Leave one on"));

        // A typo is silent by construction — capability kinds are open strings on the wire — so
        // the name is checked here or not at all.
        var typo = validator.Validate(null, new NodeOptions
        {
            Capabilities = new CapabilityOptions { Disabled = ["chatt"] }
        });

        Assert.True(typo.Failed);
        Assert.Contains(typo.Failures!, failure => failure.Contains("chatt"));

        Assert.False(validator.Validate(null, new NodeOptions()).Failed);
    }

    // ---- solo mode: the same key, enforced by the node instead of the router ----------------

    [Fact]
    public async Task ASoloNodeRefusesADisabledCapabilityInBothDialects()
    {
        await using var host = await SoloHost.StartAsync(
            settings: "--Node:Capabilities:Disabled:0=embed");

        foreach (var path in new[] { "/api/embed", "/api/embeddings", "/v1/embeddings" })
        {
            var response = await host.Client.PostAsync(
                path,
                new StringContent("""{"model":"llama3","input":"hi","prompt":"hi"}""", System.Text.Encoding.UTF8, "application/json"));

            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("30", Assert.Single(response.Headers.GetValues("Retry-After")));
            Assert.Contains("embed", await response.Content.ReadAsStringAsync());
        }

        // …and the capability that is still on is untouched.
        var chat = await host.Client.PostAsync(
            "/api/chat",
            new StringContent("""{"model":"llama3","messages":[{"role":"user","content":"hi"}],"stream":false}""",
                System.Text.Encoding.UTF8,
                "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.OK, chat.StatusCode);
    }

    [Fact]
    public async Task ASoloNodeReportsItsCapabilitiesOnStatusAndOnV1Models()
    {
        await using var host = await SoloHost.StartAsync(
            settings: "--Node:Capabilities:Disabled:0=embed");

        var status = await host.Client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/status");
        var kinds = status.GetProperty("capabilities").EnumerateArray().Select(k => k.GetString()!).ToArray();
        Assert.Equal([CapabilityKinds.Chat], kinds);

        var models = await host.Client.GetFromJsonAsync<System.Text.Json.JsonElement>("/v1/models");
        var first = models.GetProperty("data").EnumerateArray().First();
        Assert.Equal(
            [CapabilityKinds.Chat],
            first.GetProperty("capabilities").EnumerateArray().Select(k => k.GetString()!).ToArray());
    }

    private static Task<InferenceCore.DispatchOutcome> Dispatch(
        NodeRegistry registry,
        string kind,
        string model,
        InferHub.Coordinator.Auth.ResolvedClient? client = null)
    {
        var options = Options.Create(new RouterOptions());

        return InferenceCore.DispatchAsync(
            kind,
            "{}",
            model,
            stream: false,
            conversationKey: null,
            TestUsage.Context(registry, client),
            new Router(registry, new ConversationAffinity(options), new ThroughputTracker(), options),
            registry,
            new ThrowingDispatcher(),
            new DisabledFallback(),
            new InferHub.Coordinator.Observability.Metrics(),
            NullLogger.Instance,
            CancellationToken.None);
    }

    private static Router NewRouter(NodeRegistry registry)
    {
        var options = Options.Create(new RouterOptions());
        return new Router(registry, new ConversationAffinity(options), new ThroughputTracker(), options);
    }

    private static ModelInfo Model(string name) => new(name, "digest", 1);

    private static NodeRegistration Registration(string nodeId)
        => new(nodeId, nodeId, "http://localhost:11434/", "3.8.0");

    /// <summary>Nothing in this suite should reach a node; if one does, say so loudly.</summary>
    private sealed class ThrowingDispatcher : IDispatcher
    {
        public Task<InferenceResult> DispatchAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
            => throw new InvalidOperationException("a job was dispatched that should never have been routed");

        public Task<System.Threading.Channels.ChannelReader<InferenceChunk>> DispatchStreamAsync(
            RoutableNode node,
            InferenceJob job,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("a job was dispatched that should never have been routed");

        public bool Complete(InferenceResult result) => false;

        public bool WriteChunk(InferenceChunk chunk) => false;

        public void FailForConnection(string connectionId, Exception? error = null)
        {
        }
    }

    private sealed class DisabledFallback : IFallbackDispatcher
    {
        public bool ShouldServe(string model, bool hasCapableNode) => false;

        public Task<FallbackResult> DispatchAsync(
            string kind,
            string rawJson,
            string model,
            bool stream,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("cloud burst is off in this suite");
    }
}
