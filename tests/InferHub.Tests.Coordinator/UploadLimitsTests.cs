using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;

namespace InferHub.Tests;

/// <summary>
/// The arithmetic behind phase-53 D6, and the router filter behind D5 — the two pieces whose
/// off-by-one costs somebody a 413 with no key named in it, or a job sent to a node that cannot
/// take it.
/// </summary>
public class UploadLimitsTests
{
    [Fact]
    public void TheBodyCeilingClearsTheDefaultAttachmentCapWithRoomForTheEnvelope()
    {
        var limit = UploadLimits.RequestBodyLimitFor(ToolAttachmentLimits.DefaultMaxBytes, 0);

        Assert.True(limit > ToolAttachmentLimits.DefaultMaxBytes);
        Assert.Equal(ToolAttachmentLimits.DefaultMaxBytes + UploadLimits.EnvelopeBytes, limit);
    }

    [Fact]
    public void TheBodyCeilingFollowsWhicheverKeyIsHigher()
    {
        // The streamed key is the one an operator raises, and Kestrel's own default (30 000 000)
        // has to move with it or the 413 comes from Kestrel with none of our sentence in it.
        var limit = UploadLimits.RequestBodyLimitFor(25L * 1024 * 1024, 512L * 1024 * 1024);

        Assert.Equal(512L * 1024 * 1024 + UploadLimits.EnvelopeBytes, limit);
        Assert.True(limit > 30_000_000, "the derived ceiling must clear Kestrel's default, which is the whole point of D6");
    }

    [Fact]
    public void AnUnsetAttachmentCapFallsBackToTheDefaultRatherThanToZero()
    {
        // A zero here would be a body ceiling of 64 KB — a "limit" that refuses every real upload,
        // arrived at by arithmetic rather than by anybody choosing it.
        Assert.Equal(
            ToolAttachmentLimits.DefaultMaxBytes + UploadLimits.EnvelopeBytes,
            UploadLimits.RequestBodyLimitFor(0, 0));
    }

    [Fact]
    public void OnlyANodeThatDeclaredStreamedAttachmentsIsACandidateForAStreamedJob()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        registry.Upsert("conn-old", Registration("old-node", streamed: null), now);
        registry.Upsert("conn-new", Registration("new-node", streamed: true), now);

        registry.ReportModels("conn-old", Models("old-node", streamed: null), now);
        registry.ReportModels("conn-new", Models("new-node", streamed: true), now);

        // Buffered traffic keeps routing to the whole fleet — that is the half a regression here
        // would break silently, and it is the same shape as phase-40 D1's undeclared node.
        Assert.Equal(2, registry.FindNodesWithModel("whisper-small", "transcribe").Count);

        var streamedCandidates = registry.FindNodesWithModel(
            "whisper-small",
            "transcribe",
            requireStreamedAttachments: true);

        Assert.Equal("new-node", Assert.Single(streamedCandidates).NodeId);
    }

    [Fact]
    public void AMessageThatSaysNothingAboutStreamedUploadsDoesNotEraseWhatTheNodeAlreadySaid()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        registry.Upsert("conn", Registration("node", streamed: true), now);

        // A model report from a node that carries no declaration must not un-declare it — the same
        // null-is-not-a-declaration rule capabilities have had since phase 40.
        registry.ReportModels("conn", Models("node", streamed: null), now);

        Assert.Single(registry.FindNodesWithModel("whisper-small", "transcribe", requireStreamedAttachments: true));
    }

    private static NodeRegistration Registration(string nodeId, bool? streamed) => new(
        nodeId,
        nodeId,
        "http://127.0.0.1:11434",
        "3.21.0",
        SupportsStreamedAttachments: streamed);

    private static NodeModels Models(string nodeId, bool? streamed) => new(
        nodeId,
        [new ModelInfo("whisper-small", null, null)],
        DateTimeOffset.UtcNow,
        [new NodeCapability("transcribe", ["whisper-small"])],
        streamed);
}
