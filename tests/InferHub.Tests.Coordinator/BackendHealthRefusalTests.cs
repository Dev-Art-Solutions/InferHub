using System.Threading.Channels;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 69, D3. The three questions in the order their fixes go in: does anybody hold this name,
/// can the ones that do answer, and is it the capability that is missing. Getting the order wrong
/// is not a cosmetic problem — a <c>404</c> for a fleet whose only holder has a dead inference
/// server sends an operator to pull a model that is already on the box.
/// </summary>
public class BackendHealthRefusalTests
{
    private const string ChatJob = """{"model":"llama3","messages":[{"role":"user","content":"hi"}]}""";

    [Fact]
    public async Task AModelHeldOnlyByASickNodeIs503NamingTheBackendRatherThan404()
    {
        var registry = FleetHolding("llama3", BackendHealth.Wedged);

        var outcome = await Route(registry);

        Assert.True(outcome.IsError);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, outcome.ErrorStatus);
        Assert.Equal("every node holding model 'llama3' reports an unhealthy inference backend", outcome.ErrorMessage);

        // A fleet-state answer carries a hint, exactly as the capability refusal has since phase 40.
        Assert.NotNull(outcome.RetryAfterSeconds);
    }

    [Fact]
    public async Task AModelNobodyHoldsIsStillAFlat404()
    {
        var outcome = await Route(new NodeRegistry());

        Assert.True(outcome.IsError);
        Assert.Equal(StatusCodes.Status404NotFound, outcome.ErrorStatus);
        Assert.Equal("model 'llama3' not found", outcome.ErrorMessage);
    }

    /// <summary>
    /// 69 D5. The refusal must read exactly as it did in v3.35 for a fleet that reports nothing —
    /// which is every fleet, until somebody upgrades a node.
    /// </summary>
    [Fact]
    public async Task AFleetThatReportsNoHealthAtAllRefusesExactlyAsItDidBefore()
    {
        var registry = FleetHolding("llama3", health: null);

        // The router is stubbed to find nothing, which on a healthy fleet means the model exists
        // and no node provides *this kind of work* — the phase-40 answer, unchanged.
        var outcome = await Route(registry, capability: "embed");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, outcome.ErrorStatus);
        Assert.Equal("no node currently provides 'embed' for model 'llama3'", outcome.ErrorMessage);
    }

    /// <summary>
    /// The mixed fleet: one holder is wedged and another is merely incapable. The backend is not
    /// the story here — a healthy node can still be asked — so the capability refusal wins.
    /// </summary>
    [Fact]
    public async Task OneSickHolderBesideAHealthyOneIsStillTheCapabilityRefusal()
    {
        var registry = FleetHolding("llama3", BackendHealth.Unreachable);
        var now = DateTimeOffset.UtcNow;

        registry.Upsert("connection-2", Registration("node-2"), now);
        registry.ReportModels(
            "connection-2",
            new NodeModels("node-2", [new ModelInfo("llama3", "digest", 1)], now),
            now);

        var outcome = await Route(registry, capability: "embed");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, outcome.ErrorStatus);
        Assert.Equal("no node currently provides 'embed' for model 'llama3'", outcome.ErrorMessage);
    }

    private static NodeRegistry FleetHolding(string model, BackendHealth? health)
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        registry.Upsert("connection-1", Registration("node-1"), now);
        registry.ReportModels(
            "connection-1",
            new NodeModels("node-1", [new ModelInfo(model, "digest", 1)], now),
            now);
        registry.Touch("connection-1", new Heartbeat("node-1", now, InFlight: 0, Backend: health), now);

        return registry;
    }

    private static Task<InferenceCore.DispatchOutcome> Route(NodeRegistry registry, string? capability = null)
        => InferenceCore.DispatchAsync(
            capability is "embed" ? "embed" : "chat",
            ChatJob,
            "llama3",
            stream: false,
            conversationKey: null,
            TestUsage.Context(registry),
            new NoRouteRouter(),
            registry,
            new UnusedDispatcher(),
            new NeverFallback(),
            new Metrics(),
            NullLogger.Instance,
            CancellationToken.None);

    private static NodeRegistration Registration(string nodeId)
        => new(nodeId, nodeId, "http://localhost:11434/", "3.36.0");

    /// <summary>Every case here is one where routing already came back empty.</summary>
    private sealed class NoRouteRouter : IRouter
    {
        public RoutableNode? Route(string model, string? conversationKey = null, string? excludeConnectionId = null, string? capability = null, bool requireStreamedAttachments = false, bool requireStreamedSpeech = false)
            => null;
    }

    private sealed class UnusedDispatcher : IDispatcher
    {
        public Task<InferenceResult> DispatchAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
            => throw new InvalidOperationException("nothing here should reach a node");

        public Task<ChannelReader<InferenceChunk>> DispatchStreamAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public bool Complete(InferenceResult result) => throw new NotImplementedException();

        public bool WriteChunk(InferenceChunk chunk) => throw new NotImplementedException();

        public void FailForConnection(string connectionId, Exception? error = null) => throw new NotImplementedException();
    }

    private sealed class NeverFallback : IProviderDispatcher
    {
        public ProviderDecision Decide(string model, bool hasCapableNode, ProviderSteer steer) => ProviderDecision.No;

        public Task<ProviderResult> DispatchAsync(string kind, string rawJson, string model, bool stream, CancellationToken cancellationToken)
            => throw new InvalidOperationException("no provider is configured in these tests");
    }
}
