using System.Text.Json;
using InferHub.Shared.Contracts;

namespace InferHub.Tests;

/// <summary>
/// The wire contract phase 53 adds, checked with the serializer that actually carries it.
/// </summary>
/// <remarks>
/// SignalR's JSON hub protocol serializes with camelCase naming, and every field here declares its
/// own <c>JsonPropertyName</c>. A flag that does not survive the round trip is the worst possible
/// failure for this phase: the hub sends a job whose bytes are elsewhere, the node reads the flag as
/// <c>false</c>, never pulls them, and runs the tool on <b>no file at all</b> — which a worker
/// answers cheerfully with a 200. That is not hypothetical; it is what this suite was written after.
/// </remarks>
public class StreamedUploadContractTests
{
    private static readonly JsonSerializerOptions SignalRShaped = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void TheStreamedFlagSurvivesTheRoundTrip()
    {
        var job = new ToolJob(
            Guid.NewGuid(),
            "transcribe",
            "whisper-small",
            "{}",
            Attachments: null,
            HasStreamedAttachments: true);

        var round = JsonSerializer.Deserialize<ToolJob>(
            JsonSerializer.Serialize(job, SignalRShaped),
            SignalRShaped);

        Assert.NotNull(round);
        Assert.True(round.HasStreamedAttachments);
        Assert.Null(round.Attachments);
        Assert.Equal(job.JobId, round.JobId);
    }

    [Fact]
    public void AJobFromBeforeThisPhaseReadsAsNotStreamed()
    {
        // A v3.20 hub sends no such field. Absent must mean false, not "unset and therefore true of
        // whatever the node last saw" — phase-40 D1's mixed-fleet rule.
        var round = JsonSerializer.Deserialize<ToolJob>(
            """{"jobId":"6f1f8f4e-6c3d-4c4e-9f1a-2b3c4d5e6f70","capability":"echo","model":"echo","payload":"{}"}""",
            SignalRShaped);

        Assert.NotNull(round);
        Assert.False(round.HasStreamedAttachments);
    }

    [Fact]
    public void EveryAttachmentFrameKindSurvivesTheRoundTrip()
    {
        AttachmentChunk[] frames =
        [
            AttachmentChunk.Start(0, "file", "audio/wav"),
            AttachmentChunk.Data(0, [1, 2, 3, 250]),
            AttachmentChunk.End(0)
        ];

        var round = JsonSerializer.Deserialize<AttachmentChunk[]>(
            JsonSerializer.Serialize(frames, SignalRShaped),
            SignalRShaped)!;

        Assert.Equal(AttachmentChunkKinds.Start, round[0].Kind);
        Assert.Equal("file", round[0].Name);
        Assert.Equal("audio/wav", round[0].MediaType);

        // The bytes are the only thing here that cannot be reconstructed from anything else.
        Assert.Equal([1, 2, 3, 250], round[1].Bytes);
        Assert.Equal(AttachmentChunkKinds.End, round[2].Kind);
    }

    [Fact]
    public void ANodeThatSaysNothingAboutStreamedUploadsIsReadAsNotSupportingThem()
    {
        var round = JsonSerializer.Deserialize<NodeRegistration>(
            """{"nodeId":"n1","name":"n","ollamaEndpoint":"http://x","version":"3.20.0"}""",
            SignalRShaped);

        Assert.NotNull(round);
        Assert.Null(round.SupportsStreamedAttachments);
    }
}
