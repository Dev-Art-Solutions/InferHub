using InferHub.Shared.Contracts;
using InferHub.Shared.Images;

namespace InferHub.Tests;

/// <summary>
/// What phase-47 D6 promises about a finished job's bytes: they expire, they are bounded, they are
/// read once, and <b>nothing touches disk, ever</b>.
/// </summary>
/// <remarks>
/// These drive the store directly rather than through a mesh, deliberately: the properties under
/// test are about time and about a byte ceiling, and a suite that had to run a real diffusion job to
/// reach them would either be slow or would fake the thing it is asserting. The mesh-level version
/// of "read once" lives in <see cref="ImageJobTests"/>, where a real 410 comes back over real HTTP.
/// </remarks>
public class ImageJobRetentionTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4];

    [Fact]
    public void ExpiryDropsTheBytesAndSaysWhy()
    {
        var time = new FakeTime(DateTimeOffset.UnixEpoch);
        var store = new ImageJobStore(new ImageJobOptions { RetentionSeconds = 60 }, time);

        var record = Succeed(store, "client");

        Assert.Equal(ImageJobStates.Succeeded, record.State);
        Assert.True(store.RetainedBytes() > 0);

        time.Advance(TimeSpan.FromSeconds(61));
        store.Sweep();

        Assert.Equal(ImageJobStates.Expired, record.State);
        Assert.Equal(ImageJobReasons.RetentionLapsed, record.Reason);
        Assert.Equal(0, store.RetainedBytes());
    }

    [Fact]
    public void TheByteCeilingEvictsCompletedResultsLruAndNeverAnInFlightOne()
    {
        var time = new FakeTime(DateTimeOffset.UnixEpoch);

        // Room for two of these and not three.
        var store = new ImageJobStore(new ImageJobOptions { MaxRetainedBytes = Png.Length * 2 }, time);

        var oldest = Succeed(store, "client");
        time.Advance(TimeSpan.FromSeconds(1));
        var middle = Succeed(store, "client");

        // An in-flight job holds no bytes and must survive the pressure that evicts finished ones —
        // a budget that could evict work in progress would fail the request it was protecting.
        store.TryCreate(Guid.NewGuid(), "client", "sd-test", 1, out var inFlight);
        store.TryTransition(inFlight.Id, ImageJobStates.Running);

        time.Advance(TimeSpan.FromSeconds(1));
        var newest = Succeed(store, "client");

        Assert.Equal(ImageJobStates.Expired, oldest.State);
        Assert.Equal(ImageJobReasons.Evicted, oldest.Reason);
        Assert.Equal(ImageJobStates.Succeeded, middle.State);
        Assert.Equal(ImageJobStates.Succeeded, newest.State);
        Assert.Equal(ImageJobStates.Running, inFlight.State);
        Assert.True(store.RetainedBytes() <= Png.Length * 2);
    }

    [Fact]
    public void ReadOnceDropsTheBytesOnDelivery()
    {
        var store = new ImageJobStore(new ImageJobOptions());
        var record = Succeed(store, "client");

        Assert.NotNull(store.TryTakeContent(record.Id, "client", 0));
        Assert.Null(store.TryTakeContent(record.Id, "client", 0));
        Assert.Equal(ImageJobStates.Expired, record.State);
        Assert.Equal(ImageJobReasons.Delivered, record.Reason);
    }

    [Fact]
    public void KeepAfterReadIsTheSettingThatMakesTheHubBrieflyAnImageCache()
    {
        var store = new ImageJobStore(new ImageJobOptions { KeepAfterRead = true });
        var record = Succeed(store, "client");

        Assert.NotNull(store.TryTakeContent(record.Id, "client", 0));
        Assert.NotNull(store.TryTakeContent(record.Id, "client", 0));
        Assert.Equal(ImageJobStates.Succeeded, record.State);
    }

    [Fact]
    public void TheTransitionTableRefusesWhatTheStateMachineDoesNotAllow()
    {
        // Read as a whole this is the state diagram the docs draw. The one that looks like a bug
        // and is not is cancelling → succeeded (D3).
        Assert.True(ImageJobStates.CanTransition(ImageJobStates.Queued, ImageJobStates.Running));
        Assert.True(ImageJobStates.CanTransition(ImageJobStates.Running, ImageJobStates.Cancelling));
        Assert.True(ImageJobStates.CanTransition(ImageJobStates.Cancelling, ImageJobStates.Succeeded));
        Assert.True(ImageJobStates.CanTransition(ImageJobStates.Succeeded, ImageJobStates.Expired));

        Assert.False(ImageJobStates.CanTransition(ImageJobStates.Queued, ImageJobStates.Cancelling));
        Assert.False(ImageJobStates.CanTransition(ImageJobStates.Succeeded, ImageJobStates.Running));
        Assert.False(ImageJobStates.CanTransition(ImageJobStates.Failed, ImageJobStates.Running));
        Assert.False(ImageJobStates.CanTransition(ImageJobStates.Cancelled, ImageJobStates.Succeeded));

        Assert.All(
            new[] { ImageJobStates.Succeeded, ImageJobStates.Failed, ImageJobStates.Cancelled, ImageJobStates.Expired },
            state => Assert.True(ImageJobStates.IsTerminal(state)));

        Assert.All(
            new[] { ImageJobStates.Queued, ImageJobStates.Running, ImageJobStates.Cancelling },
            state => Assert.False(ImageJobStates.IsTerminal(state)));
    }

    [Fact]
    public void ProgressIsMonotonicAndOnlyWhileTheJobIsRunning()
    {
        var store = new ImageJobStore(new ImageJobOptions());
        store.TryCreate(Guid.NewGuid(), "client", "sd-test", 1, out var record);

        // A queued job has no steps to report; a frame that arrived before the transition would
        // otherwise show progress on a job nothing has started.
        Assert.False(store.ReportProgress(record.Id, 1, 20));

        store.TryTransition(record.Id, ImageJobStates.Running);

        Assert.True(store.ReportProgress(record.Id, 3, 20));
        Assert.False(store.ReportProgress(record.Id, 2, 20));
        Assert.True(store.ReportProgress(record.Id, 4, 20));
        Assert.Equal(4, record.Step);
    }

    [Fact]
    public void AJobIsOnlyEverVisibleToTheClientThatCreatedIt()
    {
        var store = new ImageJobStore(new ImageJobOptions());
        var record = Succeed(store, "mine");

        Assert.NotNull(store.Find(record.Id, "mine"));
        Assert.Null(store.Find(record.Id, "theirs"));
        Assert.Null(store.TryTakeContent(record.Id, "theirs", 0));
        Assert.Empty(store.TryTakeAll(record.Id, "theirs"));

        // And the bytes are still there for their owner, which is what makes the refusal a refusal
        // rather than a race that happened to hide them.
        Assert.NotNull(store.TryTakeContent(record.Id, "mine", 0));
    }

    private static ImageJobRecord Succeed(ImageJobStore store, string clientId)
    {
        store.TryCreate(Guid.NewGuid(), clientId, "sd-test", 1, out var record);
        store.TryTransition(record.Id, ImageJobStates.Running);
        store.TrySucceed(record.Id, [new ImageJobImage(Png, "image/png", "512x512", 42)], units: 7.5);
        return record;
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
