using System.Threading.Channels;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 138 D4: a request reaches a community group only when the client accepts, in that request, that machines of other
/// owners see the prompt — otherwise a 403 that says what and how. Every other model is untouched.
/// </summary>
public sealed class CommunityAcceptTests
{
    private const string Community = "llama-8b-community";

    private static NodeRegistry Registry() => new() { IsCommunityGroup = model => model == Community };

    private static Task<InferenceCore.DispatchOutcome> Ask(NodeRegistry registry, string model, bool accepted) =>
        InferenceCore.DispatchAsync(
            "chat",
            $$"""{"model":"{{model}}","messages":[{"role":"user","content":"hi"}]}""",
            model,
            stream: false,
            conversationKey: null,
            TestUsage.Context(registry) with { CommunityAccepted = accepted },
            new NoRouteRouter(),
            registry,
            new UnusedDispatcher(),
            new NeverFallback(),
            new Metrics(),
            NullLogger.Instance,
            CancellationToken.None);

    [Fact]
    public async Task WithoutAcceptanceACommunityGroupIsRefusedAndTheRefusalSaysWhyAndHow()
    {
        var refused = await Ask(Registry(), Community, accepted: false);

        Assert.Equal(StatusCodes.Status403Forbidden, refused.ErrorStatus);
        Assert.Contains("machines of other owners", refused.ErrorMessage);
        Assert.Contains("X-InferHub-Community: accept", refused.ErrorMessage);
    }

    [Fact]
    public async Task WithAcceptanceTheRequestGoesOnAndOtherModelsNeverAsk()
    {
        // Accepted: routed as any request (here nobody holds it, so not the 403).
        Assert.NotEqual(StatusCodes.Status403Forbidden, (await Ask(Registry(), Community, accepted: true)).ErrorStatus);

        // A model that is not a community group never needs the header.
        Assert.NotEqual(StatusCodes.Status403Forbidden, (await Ask(Registry(), "llama3", accepted: false)).ErrorStatus);

        // A hub with no groups at all: nothing asked.
        Assert.NotEqual(StatusCodes.Status403Forbidden, (await Ask(new NodeRegistry(), Community, accepted: false)).ErrorStatus);
    }

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
