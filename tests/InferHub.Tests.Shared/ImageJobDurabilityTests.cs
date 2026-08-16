using System.Text.Json;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;

namespace InferHub.Tests;

/// <summary>
/// What phase 56 promises about a job that outlives the process that made it — and, just as
/// importantly, what it refuses to promise.
/// </summary>
/// <remarks>
/// <para>
/// These drive the store and a real directory rather than a mesh, for
/// <see cref="ImageJobRetentionTests"/>'s reason: the properties under test are about a clock and a
/// filesystem, and a suite that had to run a diffusion job to reach them would either be slow or
/// would fake the thing it asserts. A real <c>FileImageJobArchive</c> over a real temp directory is
/// the part that cannot be stubbed — a fake archive would echo whatever this file already believed.
/// </para>
/// <para>
/// The mesh-level half is <c>ImagePrivacyTests.NoPromptSurvivesOnDiskWhenJobsArePersisted</c>, where
/// a real request with a real prompt goes through a real hub and the directory is searched for it.
/// </para>
/// </remarks>
public class ImageJobDurabilityTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 13, 10, 26, 10, 7, 7, 7];

    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "inferhub-image-archive-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The default is the whole of v3.23: no directory, no file, no listing — a deployment that
    /// changes no config behaves identically.
    /// </summary>
    [Fact]
    public void NothingIsWrittenWhenPersistenceIsOff()
    {
        var store = new ImageJobStore(
            new ImageJobOptions { DataDirectory = directory },
            archive: ImageJobArchives.Create(new ImageJobOptions { DataDirectory = directory }));

        Succeed(store, "client");

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void AFinishedJobAndItsPixelsSurviveTheProcessThatMadeThem()
    {
        var first = NewStore();
        var record = Succeed(first, "client");

        var second = NewStore();
        var restored = second.Find(record.Id, "client");

        Assert.NotNull(restored);
        Assert.Equal(ImageJobStates.Succeeded, restored!.State);
        Assert.Equal("sd-test", restored.Model);
        Assert.Equal(7.5, restored.Units);

        var image = second.TryTakeContent(record.Id, "client", 0);

        Assert.NotNull(image);
        Assert.Equal(Png, image!.Bytes);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(new ImageSize(512, 512), image.Size);
        Assert.Equal(42, image.Seed);
    }

    /// <summary>
    /// It is still <em>somebody's</em> job after a restart. A record whose owner was forgotten would
    /// be a picture the next caller could fetch, which is a tenancy hole with a filesystem in it.
    /// </summary>
    [Fact]
    public void ARestoredJobIsStillOnlyVisibleToTheClientThatCreatedIt()
    {
        var record = Succeed(NewStore(), "mine");
        var restored = NewStore();

        Assert.Null(restored.Find(record.Id, "theirs"));
        Assert.Null(restored.TryTakeContent(record.Id, "theirs", 0));
        Assert.NotNull(restored.Find(record.Id, "mine"));
    }

    /// <summary>
    /// <b>D2, the load-bearing one.</b> Restarting a hub is not a way to keep a picture longer than
    /// the window allows, and the window is applied before the first request rather than by the
    /// five-second sweeper.
    /// </summary>
    [Fact]
    public void RetentionIsAppliedOnLoadAndTheFilesGoWithIt()
    {
        var time = new FakeTime(DateTimeOffset.UnixEpoch);
        var record = Succeed(NewStore(time, retentionSeconds: 60), "client");

        time.Advance(TimeSpan.FromSeconds(61));

        var restarted = NewStore(time, retentionSeconds: 60);

        // Gone entirely, not resurrected-then-swept: nothing about it is fetchable at any point
        // after the restart, and its bytes are not on the disk waiting for a timer.
        Assert.Null(restarted.Find(record.Id, "client"));
        Assert.Equal(0, restarted.RetainedBytes());
        Assert.Empty(Directory.GetFiles(directory));
    }

    /// <summary>
    /// <b>D3.</b> A job that was in flight comes back terminal with a reason, never re-dispatched —
    /// and the reason a caller reads says the hub restarted rather than "not found".
    /// </summary>
    [Fact]
    public void AJobThatWasInFlightComesBackFailedAndIsNeverResumed()
    {
        var first = NewStore();

        first.TryCreate(Guid.NewGuid(), "client", AJob(), out var queued);
        first.TryCreate(Guid.NewGuid(), "client", AJob(), out var running);
        first.TryTransition(running.Id, ImageJobStates.Running, nodeId: "node-a");

        var second = NewStore();

        foreach (var id in new[] { queued.Id, running.Id })
        {
            var restored = second.Find(id, "client");

            Assert.NotNull(restored);
            Assert.Equal(ImageJobStates.Failed, restored!.State);
            Assert.Equal(ImageJobReasons.HubRestarted, restored.Reason);
            Assert.Contains("not resumed", restored.Error);
        }

        // And nothing is waiting to be picked up: a pump that found a queued job here would be the
        // silent re-dispatch 47 D7 refuses, arrived at through a restart.
        Assert.Empty(second.Queued());
    }

    /// <summary>
    /// <b>D4.</b> Read-once means read once from the disk too — otherwise durability quietly switches
    /// off the rule that <c>KeepAfterRead</c> exists to make somebody turn off on purpose.
    /// </summary>
    [Fact]
    public void DeliveryUnlinksTheFileAndARestartDoesNotBringItBack()
    {
        var store = NewStore();
        var record = Succeed(store, "client");

        Assert.NotEmpty(Directory.GetFiles(directory, "*.bin"));
        Assert.NotNull(store.TryTakeContent(record.Id, "client", 0));
        Assert.Empty(Directory.GetFiles(directory, "*.bin"));

        var restarted = NewStore();
        var restored = restarted.Find(record.Id, "client");

        // The record survives so a late fetch is a 410 that says `delivered` rather than a 404 that
        // reads like a bug — and there is nothing behind it.
        Assert.Equal(ImageJobStates.Expired, restored!.State);
        Assert.Equal(ImageJobReasons.Delivered, restored.Reason);
        Assert.Null(restarted.TryTakeContent(record.Id, "client", 0));
    }

    [Fact]
    public void KeepAfterReadSurvivesToo()
    {
        var store = NewStore(keepAfterRead: true);
        var record = Succeed(store, "client");

        Assert.NotNull(store.TryTakeContent(record.Id, "client", 0));

        var restarted = NewStore(keepAfterRead: true);

        Assert.NotNull(restarted.TryTakeContent(record.Id, "client", 0));
        Assert.NotNull(restarted.TryTakeContent(record.Id, "client", 0));
    }

    /// <summary>
    /// The ceiling is a property of <em>now</em>. An operator who lowered it while the hub was down
    /// gets the lower one honoured on the first boot rather than on the first render.
    /// </summary>
    [Fact]
    public void ALoweredByteCeilingIsHonouredOnLoad()
    {
        var time = new FakeTime(DateTimeOffset.UnixEpoch);
        var generous = NewStore(time, maxRetainedBytes: Png.Length * 4);

        Succeed(generous, "client");
        time.Advance(TimeSpan.FromSeconds(1));
        var newest = Succeed(generous, "client");

        var narrowed = NewStore(time, maxRetainedBytes: Png.Length);

        Assert.True(narrowed.RetainedBytes() <= Png.Length);
        Assert.NotNull(narrowed.TryTakeContent(newest.Id, "client", 0));
    }

    /// <summary>
    /// <b>Rule 7, structurally.</b> The archived record has no field that could hold a prompt, a
    /// negative prompt, an uploaded picture or a mask — and there is deliberately no flag to add one,
    /// because a field is an invitation (25 D3).
    /// </summary>
    [Fact]
    public void TheArchivedRecordHasNoFieldThatCouldHoldAPrompt()
    {
        Succeed(NewStore(), "client");

        var document = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(directory, "*.json").Single()));

        var forbidden = new[] { "prompt", "negative", "image", "mask", "input", "text", "content" };

        foreach (var property in document.RootElement.EnumerateObject())
        {
            Assert.DoesNotContain(
                forbidden,
                word => property.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
                    && !property.Name.Equals("images", StringComparison.OrdinalIgnoreCase)
                    && !property.Name.Equals("promptAugmented", StringComparison.OrdinalIgnoreCase));
        }

        // `images` is the one that had to survive that filter, so it is checked rather than excused:
        // it carries what is true *about* each picture, and the pixels are a file of their own.
        foreach (var image in document.RootElement.GetProperty("images").EnumerateArray())
        {
            Assert.False(image.TryGetProperty("bytes", out _));
            Assert.False(image.TryGetProperty("data", out _));
            Assert.Equal("image/png", image.GetProperty("mediaType").GetString());
        }

        // …and promptAugmented is a boolean about a recipe constant, which is 49 D2's line.
        Assert.Equal(JsonValueKind.False, document.RootElement.GetProperty("promptAugmented").ValueKind);
    }

    /// <summary>
    /// An unreadable archive costs the jobs in it, never the boot. Same stance as
    /// <c>FileProfileStore</c>'s torn tail: a hub that will not start because of yesterday's image is
    /// a worse outcome than one that lost yesterday's image.
    /// </summary>
    [Fact]
    public void AnUnreadableRecordCostsThatJobAndNotTheStartup()
    {
        var record = Succeed(NewStore(), "client");
        var errors = new List<string>();

        File.WriteAllText(Path.Combine(directory, $"{record.Id}.json"), "{ this is not json");

        var restarted = new ImageJobStore(
            Options(),
            archive: new FileImageJobArchive(directory, (message, _) => errors.Add(message)));

        Assert.Null(restarted.Find(record.Id, "client"));
        Assert.Contains(errors, message => message.Contains("could not read the archived image job"));
    }

    /// <summary>
    /// Bytes that cannot be read back are <b>absent</b>, not empty. Handing somebody a truncated PNG
    /// is the failure that surfaces three steps later in a viewer.
    /// </summary>
    [Fact]
    public void AMissingImageFileMakesTheJobExpiredRatherThanEmpty()
    {
        var record = Succeed(NewStore(), "client");

        File.Delete(Path.Combine(directory, $"{record.Id}.0.bin"));

        var restarted = NewStore();
        var restored = restarted.Find(record.Id, "client");

        Assert.Equal(ImageJobStates.Expired, restored!.State);
        Assert.Equal(0, restored.ImageCount);
        Assert.Null(restarted.TryTakeContent(record.Id, "client", 0));
    }

    /// <summary>
    /// An incomplete write is invisible to every route, so nothing would ever expire it — which
    /// makes a full disk, the ordinary failure mode of writing pictures to one, a way to leave
    /// somebody's image on it permanently. Load is where "left over from a previous process"
    /// becomes knowable.
    /// </summary>
    [Fact]
    public void AnIncompleteWriteLeftByAPreviousProcessIsNotAPictureThatLivesForever()
    {
        var record = Succeed(NewStore(), "client");

        // Exactly the shape a crash or a full disk leaves behind between the write and the move.
        var orphan = Path.Combine(directory, $"{Guid.NewGuid()}.0.bin.tmp");
        File.WriteAllBytes(orphan, Png);

        var restarted = NewStore();

        Assert.False(File.Exists(orphan));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

        // …and the real job is untouched by the sweep, which is the half that would be easy to break.
        Assert.NotNull(restarted.TryTakeContent(record.Id, "client", 0));
    }

    [Fact]
    public void AnUnknownPersistenceValueIsARefusalThatNamesTheKey()
    {
        Assert.True(new ImageJobOptions().TryValidate(out _));
        Assert.True(new ImageJobOptions { Persistence = "  FILE " }.TryValidate(out _));
        Assert.True(new ImageJobOptions { Persistence = "" }.TryValidate(out _));

        Assert.False(new ImageJobOptions { Persistence = "postgres" }.TryValidate(out var error));
        Assert.Contains("Images:Jobs:Persistence", error);
        Assert.Contains("'none' or 'file'", error);

        Assert.False(
            new ImageJobOptions { Persistence = "file", DataDirectory = " " }.TryValidate(out var missing));
        Assert.Contains("Images:Jobs:DataDirectory", missing);
    }

    private ImageJobOptions Options(
        int retentionSeconds = 300,
        long? maxRetainedBytes = null,
        bool keepAfterRead = false) => new()
        {
            Persistence = ImageJobOptions.PersistenceFile,
            DataDirectory = directory,
            RetentionSeconds = retentionSeconds,
            KeepAfterRead = keepAfterRead,
            MaxRetainedBytes = maxRetainedBytes ?? new ImageJobOptions().MaxRetainedBytes
        };

    /// <summary>A fresh store over the same directory, which is what a restart is.</summary>
    private ImageJobStore NewStore(
        TimeProvider? time = null,
        int retentionSeconds = 300,
        long? maxRetainedBytes = null,
        bool keepAfterRead = false)
    {
        var options = Options(retentionSeconds, maxRetainedBytes, keepAfterRead);

        return new ImageJobStore(options, time, ImageJobArchives.Create(options));
    }

    private static ImageJobRecord Succeed(ImageJobStore store, string clientId)
    {
        store.TryCreate(Guid.NewGuid(), clientId, AJob(), out var record);
        store.TryTransition(record.Id, ImageJobStates.Running);
        store.TrySucceed(record.Id, [new ImageJobImage(Png, "image/png", new ImageSize(512, 512), 42)], units: 7.5);
        return record;
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    /// <summary>
    /// A minimal generation request, so a store test says <em>what a job is</em> rather than
    /// repeating five positional arguments (phase 57 changed <c>TryCreate</c> to take the request).
    /// </summary>
    private static ImageGenerationRequest AJob(string model = "sd-test") =>
        new(model, "a prompt", null, 1, null, null, null, null);

}
