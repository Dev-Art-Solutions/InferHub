using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InferHub.Node.Update;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 101 on the node: which release counts (D3), what a checksum is, what the manager does when told to
/// update and when it decides to by itself (D2, D4), and what it says after the restart its setup caused.
/// </summary>
public class NodeUpdateTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "inferhub-update-" + Guid.NewGuid().ToString("N"));

    private static Version Current => NodeVersion.Parsed;

    private static Version Next => new(Current.Major, Current.Minor + 1, 0);

    private static Version After => new(Current.Major, Current.Minor + 2, 0);

    public void Dispose()
    {
        try
        {
            Directory.Delete(scratch, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // ------------------------------------------------------------------ the feed (D3)

    [Fact]
    public void TheNewestReleaseWithItsSetupAttachedWins()
    {
        var feed = Feed(
            Release(After, assets: false),                 // newest, but its setup is not built yet
            Release(Next),
            Release(Current),                              // this build
            Release(new Version(Current.Major, Current.Minor + 3, 0), draft: true),
            Release(new Version(Current.Major, Current.Minor + 4, 0), prerelease: true));

        var picked = GitHubReleaseFeed.Pick(feed, Current);

        Assert.NotNull(picked);
        Assert.Equal(Next, picked!.Version);
        Assert.Equal(GitHubReleaseFeed.SetupName(Next), picked.SetupName);
        Assert.EndsWith(".sha256", picked.ChecksumUrl.AbsolutePath);
        Assert.Equal($"https://example.invalid/releases/v{NodeVersion.Format(Next)}", picked.PageUrl);
    }

    [Fact]
    public void NothingNewerIsNoRelease()
    {
        Assert.Null(GitHubReleaseFeed.Pick(Feed(Release(Current), Release(new Version(1, 0, 0))), Current));
        Assert.Null(GitHubReleaseFeed.Pick(Feed(Release(Next, assets: false)), Current));
    }

    [Fact]
    public void AFeedThatIsNotAListIsAnError()
    {
        using var document = JsonDocument.Parse("""{"message":"API rate limit exceeded"}""");
        var error = Assert.Throws<UpdateException>(() => GitHubReleaseFeed.Pick(document.RootElement, Current));
        Assert.Contains("list of releases", error.Message);
    }

    [Theory]
    [InlineData("v3.66.0", true, "3.66.0")]
    [InlineData("3.66.1", true, "3.66.1")]
    [InlineData("3.66.0+bba53a7", true, "3.66.0")]
    [InlineData("v3.66.0-rc1", false, null)]
    [InlineData("3.66", false, null)]
    [InlineData("latest", false, null)]
    public void TagsParseOnlyAsReleases(string tag, bool parses, string? expected)
    {
        Assert.Equal(parses, NodeVersion.TryParseTag(tag, out var version));

        if (parses)
        {
            Assert.Equal(expected, NodeVersion.Format(version));
        }
    }

    [Fact]
    public void ChecksumsParseInEveryShapeTheyArePublishedIn()
    {
        var hex = new string('a', 64);

        Assert.Equal(hex, UpdateDownloader.ParseChecksum($"{hex}  setup.exe\n", "setup.exe"));
        Assert.Equal(hex, UpdateDownloader.ParseChecksum(hex.ToUpperInvariant(), "setup.exe"));
        Assert.Equal(hex, UpdateDownloader.ParseChecksum($"{new string('b', 64)}  other.exe\n{hex} *setup.exe\n", "setup.exe"));
        Assert.Throws<UpdateException>(() => UpdateDownloader.ParseChecksum("not a hash", "setup.exe"));
    }

    // ------------------------------------------------------------------ the download

    [Fact]
    public async Task ASetupIsKeptOnlyWhenItMatchesItsChecksum()
    {
        var setup = Encoding.UTF8.GetBytes("pretend setup");
        var release = ReleaseOf(Next);
        var server = new FakeServer();
        server.Serve(release.SetupUrl, setup);
        server.Serve(release.ChecksumUrl, Encoding.UTF8.GetBytes($"{Sha(setup)}  {release.SetupName}\n"));

        var path = await new UpdateDownloader(new HttpClient(server)).DownloadAsync(release, scratch, CancellationToken.None);

        Assert.Equal(setup, File.ReadAllBytes(path));

        // A second apply reuses a verified copy rather than downloading 35 MB again.
        server.Serve(release.SetupUrl, Encoding.UTF8.GetBytes("would be a different file"));
        Assert.Equal(path, await new UpdateDownloader(new HttpClient(server)).DownloadAsync(release, scratch, CancellationToken.None));
        Assert.Equal(setup, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ASetupThatDoesNotMatchIsDeletedAndNeverRun()
    {
        var release = ReleaseOf(Next);
        var server = new FakeServer();
        server.Serve(release.SetupUrl, Encoding.UTF8.GetBytes("truncated"));
        server.Serve(release.ChecksumUrl, Encoding.UTF8.GetBytes(new string('0', 64)));

        var error = await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateDownloader(new HttpClient(server)).DownloadAsync(release, scratch, CancellationToken.None));

        Assert.Contains("does not match its published SHA-256", error.Message);
        Assert.Empty(Directory.GetFiles(scratch));
    }

    // ------------------------------------------------------------------ the manager (D2, D4, D5)

    [Fact]
    public async Task ACheckReportsTheReleaseAndEveryFlagAsConfigured()
    {
        var (manager, _, _) = Manager(new UpdateOptions { Check = true, AllowFromHub = true }, Next);

        Assert.Equal(NodeUpdatePhase.Unknown, manager.State("n").State);
        var outcome = await manager.CheckAsync(CancellationToken.None);
        var state = manager.State("n");

        Assert.True(outcome.Accepted, outcome.Message);
        Assert.Equal(NodeUpdatePhase.Available, state.State);
        Assert.Equal(NodeVersion.Format(Next), state.Available);
        Assert.Equal(NodeVersion.Current, state.Current);
        Assert.True(state.Check);
        Assert.False(state.Auto);
        Assert.True(state.AllowFromHub);
        Assert.True(state.CanApply);
        Assert.NotNull(state.LastCheckedUtc);
    }

    [Fact]
    public async Task AFeedThatCannotBeReachedIsAFailedCheckNotAnException()
    {
        var (manager, _, _) = Manager(new UpdateOptions { Check = true }, feedError: new HttpRequestException("no route to host"));

        var outcome = await manager.CheckAsync(CancellationToken.None);

        Assert.False(outcome.Accepted);
        Assert.Equal(NodeUpdatePhase.Failed, manager.State("n").State);
        Assert.Contains("no route to host", manager.State("n").LastError);
    }

    [Fact]
    public async Task TheHubIsRefusedUnlessTheOperatorAllowedIt()
    {
        var (manager, applier, _) = Manager(new UpdateOptions { Check = true }, Next);

        var outcome = await manager.ApplyAsync("admin", fromHub: true, CancellationToken.None);

        Assert.False(outcome.Accepted);
        Assert.Contains("AllowFromHub is off", outcome.Message);
        Assert.Null(applier.Launched);
    }

    [Fact]
    public async Task ANodeThatCannotApplySaysWhyAndDownloadsNothing()
    {
        var (manager, applier, server) = Manager(new UpdateOptions { Check = true, AllowFromHub = true }, Next, canApply: false);

        var outcome = await manager.ApplyAsync("admin", fromHub: true, CancellationToken.None);

        Assert.False(outcome.Accepted);
        Assert.Equal(applier.WhyNot, outcome.Message);
        Assert.Equal(0, server.Requests);
        Assert.False(manager.State("n").CanApply);
        Assert.Equal(applier.WhyNot, manager.State("n").WhyNot);
    }

    [Fact]
    public async Task ApplyingDownloadsVerifiesMarksAndStartsTheSetup()
    {
        var (manager, applier, _) = Manager(new UpdateOptions { Check = true, AllowFromHub = true }, Next);

        var outcome = await manager.ApplyAsync("admin@console", fromHub: true, CancellationToken.None);

        Assert.True(outcome.Accepted, outcome.Message);
        Assert.Equal(NodeUpdatePhase.Applying, manager.State("n").State);
        Assert.Equal(Path.Combine(manager.UpdatesDirectory, GitHubReleaseFeed.SetupName(Next)), applier.Launched);
        Assert.True(File.Exists(applier.Launched));
        Assert.True(File.Exists(Path.Combine(manager.UpdatesDirectory, UpdateManager.MarkerName)));

        // One at a time: a second press while the setup runs is refused, not a second setup.
        var again = await manager.ApplyAsync("admin@console", fromHub: true, CancellationToken.None);
        Assert.False(again.Accepted);
        Assert.Contains("already", again.Message);
    }

    [Fact]
    public async Task ASetupThatWouldNotStartLeavesNoMarkerAndAFailure()
    {
        var (manager, applier, _) = Manager(new UpdateOptions { Check = true, AllowFromHub = true }, Next);
        applier.Throw = new UpdateException("the setup exited with code 5 before it started");

        var outcome = await manager.ApplyAsync("admin", fromHub: true, CancellationToken.None);

        Assert.False(outcome.Accepted);
        Assert.Equal(NodeUpdatePhase.Failed, manager.State("n").State);
        Assert.Contains("code 5", manager.State("n").LastError);
        Assert.False(File.Exists(Path.Combine(manager.UpdatesDirectory, UpdateManager.MarkerName)));

        // And the next try is allowed: a failure does not leave the node believing it is mid-update.
        applier.Throw = null;
        Assert.True((await manager.ApplyAsync("admin", fromHub: true, CancellationToken.None)).Accepted);
    }

    [Fact]
    public void AfterTheRestartTheMarkerSaysWhatTheUpdateCameTo()
    {
        var (updated, _, _) = Manager(new UpdateOptions(), Next);
        WriteMarker(updated, to: NodeVersion.Current);
        updated.ReadMarker();

        Assert.StartsWith("updated 3.0.0 → " + NodeVersion.Current, updated.State("n").LastUpdate);
        Assert.Null(updated.State("n").LastError);
        Assert.False(File.Exists(Path.Combine(updated.UpdatesDirectory, UpdateManager.MarkerName)));

        var (stuck, _, _) = Manager(new UpdateOptions(), Next);
        WriteMarker(stuck, to: NodeVersion.Format(Next));
        stuck.ReadMarker();

        Assert.Equal(NodeUpdatePhase.Failed, stuck.State("n").State);
        Assert.Contains($"still runs {NodeVersion.Current}", stuck.State("n").LastError);
        Assert.Contains("setup.log", stuck.State("n").LastError);
        Assert.False(File.Exists(Path.Combine(stuck.UpdatesDirectory, UpdateManager.MarkerName)));
    }

    [Fact]
    public async Task WithAutoTheNodeUpdatesItselfOnceIdle()
    {
        var inFlight = 1;
        var (manager, applier, _) = Manager(
            new UpdateOptions { Check = true, Auto = true, FirstCheckDelay = TimeSpan.Zero, DrainTimeout = TimeSpan.FromMinutes(5) },
            Next,
            inFlight: () => Volatile.Read(ref inFlight));

        await manager.StartAsync(CancellationToken.None);

        try
        {
            // A job is running: the release is found and reported, and nothing is applied yet.
            await WaitAsync(() => manager.State("n").State == NodeUpdatePhase.Available);
            await Task.Delay(300);
            Assert.Null(applier.Launched);

            Volatile.Write(ref inFlight, 0);
            await WaitAsync(() => applier.Launched is not null, seconds: 15);
            Assert.Equal(NodeUpdatePhase.Applying, manager.State("n").State);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WithoutAutoTheReleaseIsOnlyReported()
    {
        var (manager, applier, _) = Manager(
            new UpdateOptions { Check = true, AllowFromHub = true, FirstCheckDelay = TimeSpan.Zero },
            Next);

        await manager.StartAsync(CancellationToken.None);

        try
        {
            await WaitAsync(() => manager.State("n").State == NodeUpdatePhase.Available);
            await Task.Delay(300);
            Assert.Null(applier.Launched);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WithCheckOffTheNodeAsksNobody()
    {
        var (manager, _, server) = Manager(new UpdateOptions { FirstCheckDelay = TimeSpan.Zero }, Next);

        await manager.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await manager.StopAsync(CancellationToken.None);

        Assert.Equal(NodeUpdatePhase.Unknown, manager.State("n").State);
        Assert.Equal(0, server.Requests);
    }

    [Theory]
    [InlineData(false, true, "needs Update:Check=true")]
    [InlineData(true, false, null)]
    public void AutoWithoutCheckIsRefusedAtStart(bool check, bool auto, string? failure)
    {
        var result = new UpdateOptionsValidator().Validate(null, new UpdateOptions { Check = check, Auto = auto });

        if (failure is null)
        {
            Assert.True(result.Succeeded);
        }
        else
        {
            Assert.Contains(failure, result.FailureMessage);
        }
    }

    [Fact]
    public void TheFeedIsNotPolledFasterThanItsRateLimitAllows()
    {
        var result = new UpdateOptionsValidator().Validate(null, new UpdateOptions { Check = true, Interval = TimeSpan.FromMinutes(1) });
        Assert.Contains("at least 5 minutes", result.FailureMessage);
    }

    // ------------------------------------------------------------------ helpers

    private (UpdateManager Manager, FakeApplier Applier, FakeServer Server) Manager(
        UpdateOptions options,
        Version? available = null,
        bool canApply = true,
        Exception? feedError = null,
        Func<int>? inFlight = null)
    {
        var server = new FakeServer();
        var release = available is null ? null : ReleaseOf(available);

        if (release is not null)
        {
            var bytes = Encoding.UTF8.GetBytes("setup " + release.Version);
            server.Serve(release.SetupUrl, bytes);
            server.Serve(release.ChecksumUrl, Encoding.UTF8.GetBytes($"{Sha(bytes)}  {release.SetupName}\n"));
        }

        var applier = new FakeApplier(canApply);
        var feed = new FakeFeed(release, feedError, server);
        var manager = new UpdateManager(
            Options.Create(options),
            feed,
            new UpdateDownloader(new HttpClient(server)),
            applier,
            Path.Combine(scratch, Guid.NewGuid().ToString("N")),
            inFlight ?? (() => 0),
            TimeProvider.System,
            NullLogger<UpdateManager>.Instance);

        return (manager, applier, server);
    }

    private static void WriteMarker(UpdateManager manager, string to)
    {
        Directory.CreateDirectory(manager.UpdatesDirectory);
        File.WriteAllText(
            Path.Combine(manager.UpdatesDirectory, UpdateManager.MarkerName),
            JsonSerializer.Serialize(new UpdateManager.Marker("3.0.0", to, Path.Combine(manager.UpdatesDirectory, "setup.log"), DateTimeOffset.UtcNow, "test")));
    }

    private static UpdateRelease ReleaseOf(Version version)
    {
        var name = GitHubReleaseFeed.SetupName(version);
        var tag = "v" + NodeVersion.Format(version);
        return new UpdateRelease(
            version,
            tag,
            name,
            new Uri($"https://example.invalid/download/{tag}/{name}"),
            new Uri($"https://example.invalid/download/{tag}/{name}.sha256"),
            $"https://example.invalid/releases/{tag}");
    }

    private static object Release(Version version, bool assets = true, bool draft = false, bool prerelease = false)
    {
        var release = ReleaseOf(version);
        return new
        {
            tag_name = release.Tag,
            draft,
            prerelease,
            html_url = release.PageUrl,
            assets = assets
                ? new[]
                {
                    new { name = release.SetupName, browser_download_url = release.SetupUrl.ToString() },
                    new { name = release.SetupName + ".sha256", browser_download_url = release.ChecksumUrl.ToString() },
                    new { name = "unrelated.zip", browser_download_url = "https://example.invalid/unrelated.zip" }
                }
                : []
        };
    }

    private static JsonElement Feed(params object[] releases)
        => JsonDocument.Parse(JsonSerializer.Serialize(releases)).RootElement.Clone();

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task WaitAsync(Func<bool> predicate, int seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);

        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(predicate(), "timed out");
    }

    private sealed class FakeFeed(UpdateRelease? release, Exception? error, FakeServer server) : IReleaseFeed
    {
        public Task<UpdateRelease?> NewestAboveAsync(Version current, CancellationToken cancellationToken)
        {
            server.Count();

            return error is not null
                ? Task.FromException<UpdateRelease?>(error)
                : Task.FromResult(release is not null && release.Version > current ? release : null);
        }
    }

    internal sealed class FakeApplier(bool canApply) : IUpdateApplier
    {
        public string? Launched { get; private set; }

        public Exception? Throw { get; set; }

        public bool CanApply => canApply;

        public string? WhyNot => canApply ? null : "this node was installed by hand (a test)";

        public Task LaunchAsync(string setupPath, string logPath, CancellationToken cancellationToken)
        {
            if (Throw is not null)
            {
                return Task.FromException(Throw);
            }

            Launched = setupPath;
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeServer : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> files = new();
        private int requests;

        public int Requests => Volatile.Read(ref requests);

        public void Serve(Uri uri, byte[] body) => files[uri.ToString()] = body;

        public void Count() => Interlocked.Increment(ref requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count();

            return Task.FromResult(files.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
