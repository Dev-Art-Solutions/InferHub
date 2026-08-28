using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase-70 D7: a node that would answer a streamed synthesis with a file is not a candidate for
/// one, and a fleet full of them still serves every buffered request it always did.
/// </summary>
/// <remarks>
/// <c>UploadLimitsTests</c>' shape one declaration over (53 D5). The bug this stops is not a crash:
/// it is a caller asking for a stream, being routed to a v3.36 node, and receiving
/// <c>tool returned 1 file(s) for a streaming request</c> — a true sentence about the wrong thing,
/// which sends an operator to look at their voices.
/// </remarks>
public class StreamedSpeechRoutingTests
{
    [Fact]
    public void AFleetOfOlderNodesStillServesEveryBufferedSynthesis()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        registry.Upsert("old", Registration("old-node", streamedSpeech: null), now);
        registry.ReportModels("old", Models("old-node", streamedSpeech: null), now);

        Assert.Single(registry.FindNodesWithModel("amy", "speak"));
        Assert.Empty(registry.FindNodesWithModel("amy", "speak", requireStreamedSpeech: true));
    }

    [Fact]
    public void OnlyANodeThatDeclaredItIsACandidateForAStream()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        registry.Upsert("old", Registration("old-node", streamedSpeech: null), now);
        registry.ReportModels("old", Models("old-node", streamedSpeech: null), now);
        registry.Upsert("new", Registration("new-node", streamedSpeech: true), now);
        registry.ReportModels("new", Models("new-node", streamedSpeech: true), now);

        Assert.Equal(2, registry.FindNodesWithModel("amy", "speak").Count);
        Assert.Equal(
            "new-node",
            Assert.Single(registry.FindNodesWithModel("amy", "speak", requireStreamedSpeech: true)).NodeId);
    }

    /// <summary>The null-is-not-a-declaration rule capabilities have had since phase 40.</summary>
    [Fact]
    public void AReportThatSaysNothingAboutStreamingDoesNotEraseWhatTheNodeAlreadySaid()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        registry.Upsert("conn", Registration("node", streamedSpeech: true), now);
        registry.ReportModels("conn", Models("node", streamedSpeech: null), now);

        Assert.Single(registry.FindNodesWithModel("amy", "speak", requireStreamedSpeech: true));
    }

    [Fact]
    public void TheRouterAsksTheSameQuestionTheRegistryAnswers()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        registry.Upsert("old", Registration("old-node", streamedSpeech: null), now);
        registry.ReportModels("old", Models("old-node", streamedSpeech: null), now);

        var options = Options.Create(new RouterOptions());
        var router = new Router(registry, new ConversationAffinity(options), new ThroughputTracker(), options);

        Assert.NotNull(router.Route("amy", capability: "speak"));
        Assert.Null(router.Route("amy", capability: "speak", requireStreamedSpeech: true));
    }

    /// <summary>
    /// The two narrowings are independent. A node may pull a 300 MB upload and still be too old to
    /// stream a synthesis, and confusing the two would make an upgrade for one silently grant the
    /// other.
    /// </summary>
    [Fact]
    public void StreamedUploadsAndStreamedSpeechAreSeparateDeclarations()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        registry.Upsert(
            "conn",
            Registration("node", streamedSpeech: null) with { SupportsStreamedAttachments = true },
            now);

        Assert.Single(registry.FindNodesWithModel("amy", "speak", requireStreamedAttachments: true));
        Assert.Empty(registry.FindNodesWithModel("amy", "speak", requireStreamedSpeech: true));
    }

    private static NodeRegistration Registration(string nodeId, bool? streamedSpeech) => new(
        nodeId,
        nodeId,
        "http://127.0.0.1:11434",
        "3.37.0",
        Capabilities: [new NodeCapability("speak", ["amy"])],
        SupportsStreamedSpeech: streamedSpeech);

    private static NodeModels Models(string nodeId, bool? streamedSpeech) => new(
        nodeId,
        [new ModelInfo("amy", null, null)],
        DateTimeOffset.UtcNow,
        [new NodeCapability("speak", ["amy"])],
        SupportsStreamedSpeech: streamedSpeech);
}
