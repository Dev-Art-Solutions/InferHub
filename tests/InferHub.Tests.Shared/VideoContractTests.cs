using System.Text.Json;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;

namespace InferHub.Tests;

/// <summary>
/// The parts of phase 57 that are arithmetic and vocabulary: the grid, the status mapping, the
/// object's keys, the two units.
/// </summary>
/// <remarks>
/// These are here rather than in a mesh suite because none of them needs a process — and because the
/// thing most likely to break them is a later phase widening the catalogue, which will edit exactly
/// these functions.
/// </remarks>
public class VideoContractTests
{
    private static ImageJobRecord ARecord(
        string state = ImageJobStates.Queued,
        int? step = null,
        int? totalSteps = null)
    {
        var store = new ImageJobStore(new ImageJobOptions());
        var id = Guid.NewGuid();

        store.TryCreate(id, "client", AVideoRequest(), out var record);

        if (step is { } s)
        {
            store.TryTransition(id, ImageJobStates.Running);
            store.ReportProgress(id, s, totalSteps);
        }

        // Through `running`, because `queued → succeeded` is not a legal transition — the table in
        // ImageJobStates is the authority and a helper that reached a state the product cannot would
        // be testing a record no hub can produce.
        if (state != ImageJobStates.Queued && state != record.State)
        {
            store.TryTransition(id, ImageJobStates.Running);
            store.TryTransition(id, state);
        }

        return record;
    }

    private static VideoGenerationRequest AVideoRequest(double? seconds = 5) =>
        new("wan-t2v-1.3b", "a paper boat", null, new ImageSize(832, 480), seconds, 30, 5.0, 7);

    // ---- the grid (D4) ---------------------------------------------------------------------------

    [Fact]
    public void AVideoSizeIsRefusedOnTheSixteenGridEvenWhenAnImageSizeWouldPass()
    {
        // 840x480 is a perfectly good IMAGE size — a multiple of 8 — and WanPipeline.check_inputs
        // raises on it. Accepting it here would move the refusal to a ValueError four minutes into a
        // job, which is the whole failure shape this grid exists to prevent.
        Assert.True(ImageSize.TryParse("840x480", out _, out _));
        Assert.False(VideoSizes.TryParse("840x480", out _, out var error));
        Assert.Contains("multiple of 16", error);

        Assert.True(VideoSizes.TryParse("832x480", out var size, out _));
        Assert.Equal(new ImageSize(832, 480), size);
    }

    [Fact]
    public void TheSecondsFieldIsAcceptedAsANumberAndAsAString()
    {
        // OpenAI's own examples spell it "4" and typed SDKs send 4. Refusing one of two spellings the
        // ecosystem uses interchangeably would fail at the one job adopting a dialect is for.
        foreach (var body in new[] { """{"model":"m","prompt":"p","seconds":4}""", """{"model":"m","prompt":"p","seconds":"4"}""" })
        {
            var request = VideoGenerationRequest.TryParse(body, _ => null, VideoLimits.Default, out var error, out _);

            Assert.NotNull(request);
            Assert.Equal(4d, request!.Seconds);
            Assert.Empty(error);
        }
    }

    [Fact]
    public void AnUnparseableOrOversizedDurationIsRefusedAtTheEdge()
    {
        Assert.Null(VideoGenerationRequest.TryParse(
            """{"model":"m","prompt":"p","seconds":0}""", _ => null, VideoLimits.Default, out _, out var param));

        Assert.Equal("seconds", param);

        Assert.Null(VideoGenerationRequest.TryParse(
            """{"model":"m","prompt":"p","seconds":600}""", _ => null, VideoLimits.Default, out var message, out _));

        Assert.Contains("at most", message);
    }

    // ---- the payload (rule 7) --------------------------------------------------------------------

    [Fact]
    public void TheToolPayloadNamesTheVideoOpAndOmitsWhatTheCallerDidNotSay()
    {
        var payload = JsonDocument.Parse(
            new VideoGenerationRequest("m", "a prompt", null, null, null, null, null, null).ToToolPayload());

        Assert.Equal("video", payload.RootElement.GetProperty("op").GetString());

        // Absent, not zero: "use the recipe's default" is said by omission, because a number invented
        // at the edge would be the hub declaring a model's native geometry (46 D6).
        foreach (var absent in new[] { "width", "height", "seconds", "steps", "guidance", "seed", "negative_prompt" })
        {
            Assert.False(payload.RootElement.TryGetProperty(absent, out _), absent);
        }
    }

    // ---- the status mapping (D1) -----------------------------------------------------------------

    [Fact]
    public void CancellingRendersAsInProgressAndExpiredRendersAsCompleted()
    {
        // The dialect has four words and this project has seven states. `cancelling` is still running
        // (47 D3 — it may finish), and a job whose bytes are gone still HAPPENED: calling that
        // `failed` would say the render did not.
        Assert.Equal(VideoStatuses.Queued, VideoStatuses.From(ImageJobStates.Queued));
        Assert.Equal(VideoStatuses.InProgress, VideoStatuses.From(ImageJobStates.Running));
        Assert.Equal(VideoStatuses.InProgress, VideoStatuses.From(ImageJobStates.Cancelling));
        Assert.Equal(VideoStatuses.Completed, VideoStatuses.From(ImageJobStates.Succeeded));
        Assert.Equal(VideoStatuses.Completed, VideoStatuses.From(ImageJobStates.Expired));
        Assert.Equal(VideoStatuses.Failed, VideoStatuses.From(ImageJobStates.Failed));
        Assert.Equal(VideoStatuses.Failed, VideoStatuses.From(ImageJobStates.Cancelled));
    }

    [Fact]
    public void ProgressNeverReachesOneHundredBeforeTheBytesExist()
    {
        // A client that sees 100 and stops polling has stopped one round trip before the bytes are
        // there. The last step's frame is capped at 99 and only a terminal state says 100.
        Assert.Equal(0, VideoRenderer.Progress(ARecord()));
        Assert.Equal(99, VideoRenderer.Progress(ARecord(step: 30, totalSteps: 30)));
        Assert.Equal(46, VideoRenderer.Progress(ARecord(step: 14, totalSteps: 30)));
        Assert.Equal(100, VideoRenderer.Progress(ARecord(ImageJobStates.Succeeded)));
    }

    // ---- the object (28 D5) ----------------------------------------------------------------------

    [Fact]
    public void TheVideoObjectOmitsAGeometryNobodyHasStatedAndCarriesOneSomebodyDid()
    {
        var store = new ImageJobStore(new ImageJobOptions());
        store.TryCreate(Guid.NewGuid(), "client", AVideoRequest(seconds: null), out var unstated);
        store.TryCreate(Guid.NewGuid(), "client", new VideoGenerationRequest(
            "m", "p", null, null, null, null, null, null), out var silent);

        var stated = JsonDocument.Parse(VideoRenderer.Object(unstated, null)).RootElement;
        var absent = JsonDocument.Parse(VideoRenderer.Object(silent, null)).RootElement;

        Assert.Equal("832x480", stated.GetProperty("size").GetString());
        Assert.False(stated.TryGetProperty("seconds", out _));

        // A caller who named neither gets neither reported. A zero would be the hub declaring a
        // model's native resolution, which is exactly what 46 D6 keeps out of the edge.
        Assert.False(absent.TryGetProperty("size", out _));
        Assert.False(absent.TryGetProperty("seconds", out _));

        Assert.Equal("video", stated.GetProperty("object").GetString());
        Assert.StartsWith("video_", stated.GetProperty("id").GetString());
        Assert.Equal(0, stated.GetProperty("progress").GetInt32());
    }

    [Fact]
    public void AnIdentifierRoundTripsAndAnythingElseIsSimplyNotFound()
    {
        var id = Guid.NewGuid();

        Assert.True(VideoRenderer.TryParseIdentifier(VideoRenderer.Identifier(id), out var parsed));
        Assert.Equal(id, parsed);

        // A bare GUID is accepted because it costs nothing and somebody will paste one; anything else
        // is false rather than a 400, because "that is not a valid id" tells a caller their guess was
        // well-formed enough to be checked (phase-25 D4's instinct, applied to a shape).
        Assert.True(VideoRenderer.TryParseIdentifier(id.ToString(), out _));
        Assert.False(VideoRenderer.TryParseIdentifier("video_nonsense", out _));
        Assert.False(VideoRenderer.TryParseIdentifier(null, out _));
    }

    // ---- the units (D6) --------------------------------------------------------------------------

    [Fact]
    public void MegapixelStepsCountEveryFrameAndAMissingNumberMetersNothing()
    {
        // 832×480 × 81 frames × 30 steps ≈ 970 megapixel-steps, against an SDXL image's 31. That
        // ratio IS the claim: a counter that billed a five-second clip like one picture would be
        // wrong in the direction that scales with how much somebody uses the expensive path.
        var produced = new GeneratedVideo(new ImageSize(832, 480), 30, 7, 81, 16, 5.0625);

        Assert.Equal(970.4, VideoRenderer.MegapixelSteps(produced), 1);
        Assert.Equal(31.5, ImageRenderer.Units([new ImageJobImage([], "image/png", new ImageSize(1024, 1024), null, Steps: 30)]), 1);

        // A worker that described nothing still produced a video; it just cannot be metered, and
        // inventing a number would bill for work nobody can point at.
        Assert.Equal(0, VideoRenderer.MegapixelSteps(null));
        Assert.Equal(0, VideoRenderer.MegapixelSteps(new GeneratedVideo(null, 30, null, 81, 16, 5)));
    }

    [Fact]
    public void AVideoIsMeteredInBothUnitsAndTheDurationIsTheMeasuredOne()
    {
        var result = new ToolResult(
            Guid.NewGuid(),
            true,
            """{"steps":30,"videos":[{"width":832,"height":480,"frames":81,"fps":16,"seconds":5.0625,"seed":7,"steps":30}]}""",
            null,
            [new ToolAttachment("video-0.mp4", "video/mp4", new byte[64])]);

        var outcome = VideoRenderer.Render(result, AVideoRequest());

        Assert.Equal(200, outcome.Status);
        Assert.Equal(UsageUnitKinds.MegapixelSteps, outcome.UnitKind);
        Assert.Equal(970.4, outcome.Units, 1);

        // The second unit rides beside the first (phase 42's audio precedent), and it is 5.0625 —
        // what was produced — rather than the 5 the request named.
        Assert.Equal(UsageUnitKinds.VideoSeconds, outcome.SecondaryUnitKind);
        Assert.Equal(5.0625, outcome.SecondaryUnits!.Value, 4);
        Assert.Equal(5.0625, outcome.Images[0].Seconds!.Value, 4);
        Assert.Equal("video/mp4", outcome.Images[0].MediaType);
    }

    [Fact]
    public void AnEmptyOrMissingAttachmentIsAGatewayErrorAndNotASuccessWithNothingInIt()
    {
        var id = Guid.NewGuid();

        Assert.Equal(502, VideoRenderer.Render(new ToolResult(id, true, "{}", null, []), AVideoRequest()).Status);

        Assert.Equal(502, VideoRenderer.Render(
            new ToolResult(id, true, "{}", null, [new ToolAttachment("v.mp4", "video/mp4", [])]),
            AVideoRequest()).Status);
    }

    // ---- the surfaces do not overlap (D10) -------------------------------------------------------

    [Fact]
    public void AJobIsOnlyVisibleToTheSurfaceThatSubmittedIt()
    {
        var store = new ImageJobStore(new ImageJobOptions());
        var videoId = Guid.NewGuid();
        var imageId = Guid.NewGuid();

        store.TryCreate(videoId, "client", AVideoRequest(), out _);
        store.TryCreate(imageId, "client", new ImageGenerationRequest("sd", "p", null, 1, null, null, null, null), out _);

        // The mismatch is a plain null and therefore the same 404 a nonexistent id earns: "that id is
        // real but it is a picture" tells a caller something about an id they were never meant to
        // reason about (phase-25 D4).
        Assert.NotNull(store.Find(videoId, "client", CapabilityKinds.IsVideo));
        Assert.Null(store.Find(imageId, "client", CapabilityKinds.IsVideo));
        Assert.Null(store.Find(videoId, "client", CapabilityKinds.IsImageKind));

        Assert.Single(store.ForClient("client", CapabilityKinds.IsVideo));
        Assert.Single(store.ForClient("client", CapabilityKinds.IsImageKind));
        Assert.Equal(2, store.ForClient("client").Count);
    }

    [Fact]
    public void DeletingAJobRemovesItRatherThanLeavingATombstone()
    {
        var store = new ImageJobStore(new ImageJobOptions());
        var id = Guid.NewGuid();

        store.TryCreate(id, "client", AVideoRequest(), out _);

        // Another client's delete is a no-op, and the owner's removes it: the dialect's DELETE means
        // gone, so a later GET is a 404 rather than a 410 about a retention window that had nothing
        // to do with what happened.
        Assert.False(store.Drop(id, "somebody-else"));
        Assert.True(store.Drop(id, "client"));
        Assert.Null(store.Find(id, "client"));
        Assert.False(store.Drop(id, "client"));
    }

    // ---- the capability predicates (D3) ----------------------------------------------------------

    [Fact]
    public void TheLicenceAndBudgetPredicateCoversVideoAndTheRoutingOneDoesNot()
    {
        // IsImageKind is what phase 50 asks about ROUTING; IsGenerativeMedia is what phase 57 asks
        // about WEIGHTS ON A CARD. Collapsing them would either route a video to the images API or
        // let an unlicensed video model past the gate.
        Assert.True(CapabilityKinds.IsGenerativeMedia(CapabilityKinds.Video));
        Assert.True(CapabilityKinds.IsGenerativeMedia(CapabilityKinds.Image));
        Assert.True(CapabilityKinds.IsGenerativeMedia(CapabilityKinds.ImageEdit));
        Assert.False(CapabilityKinds.IsGenerativeMedia(CapabilityKinds.Chat));

        Assert.False(CapabilityKinds.IsImageKind(CapabilityKinds.Video));
        Assert.True(CapabilityKinds.IsWellKnown(CapabilityKinds.Video));
    }
}
