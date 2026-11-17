using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Services;
using InferHub.Node.Configuration;
using InferHub.Node.Update;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InferHub.Tests;

/// <summary>
/// Phase 151 across real sockets: a release list that answers as GitHub's does (assets that redirect to an object store
/// that counts), a hub that supplies one release from it — and then GitHub is stopped, and the node's own 101 feed and
/// downloader, behind the real sealed handler, find and fetch the setup from the hub.
/// </summary>
public sealed class ReleaseSupplyMeshTests : IAsyncDisposable
{
    private const string Secret = "release-supply-secret";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-release-mesh-" + Guid.NewGuid().ToString("N"));

    private WebApplication? hub;
    private FakeGitHub? github;

    [Fact]
    public async Task TheLatestReleaseSuppliedOnceIsWhatASealedNodeFindsAndDownloadsWithGitHubGone()
    {
        var (relay, fake) = await StartAsync();

        var job = await SupplyAsync(relay, new { release = "latest" });
        Assert.Equal("done", job.GetProperty("state").GetString());
        Assert.Equal("v3.104.0", job.GetProperty("supplied")[0].GetString());
        // The setup came through the object store once; the checksum beside it too. Nothing of the pre-release or the draft.
        Assert.Equal(["InferHub-Node-Setup-3.104.0-win-x64.exe", "InferHub-Node-Setup-3.104.0-win-x64.exe.sha256"], fake.Objects.Order());
        await fake.DisposeAsync();
        github = null;

        using var node = SealedClient(relay);
        var feed = new GitHubReleaseFeed(node, new Uri($"{relay}/node/relay/{NodeRelay.Releases}"));

        var found = await feed.NewestAboveAsync(new Version(3, 103, 0), CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(new Version(3, 104, 0), found.Version);
        Assert.StartsWith($"{relay}/node/relay/releases/download/v3.104.0/", found.SetupUrl.ToString());
        Assert.Equal("https://github.example/releases/tag/v3.104.0", found.PageUrl);
        Assert.Null(await feed.NewestAboveAsync(new Version(3, 104, 0), CancellationToken.None));

        var path = await new UpdateDownloader(node).DownloadAsync(found, Path.Combine(directory, "node"), CancellationToken.None);
        Assert.Equal(FakeGitHub.Setup("3.104.0"), await File.ReadAllBytesAsync(path));

        using var status = await StatusAsync(relay);
        var supply = status.RootElement.GetProperty("relay").GetProperty("supply");
        Assert.Equal(1, supply.GetProperty("releases").GetInt32());
        Assert.Equal(0, supply.GetProperty("models").GetInt32());
    }

    [Fact]
    public async Task ANamedReleaseAndOneCarriedInOnADiskAreBothListedNewestFirst()
    {
        var (relay, _) = await StartAsync();

        Assert.Equal("done", (await SupplyAsync(relay, new { release = "3.103.0" })).GetProperty("state").GetString());

        var disk = Directory.CreateDirectory(Path.Combine(directory, "import", "disk1")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(disk, NodeReleaseAssets.SetupName("3.102.0")), FakeGitHub.Setup("3.102.0"));
        await File.WriteAllTextAsync(Path.Combine(disk, NodeReleaseAssets.SetupName("3.102.0") + ".sha256"), FakeGitHub.Checksum("3.102.0").ToUpperInvariant());
        await File.WriteAllBytesAsync(Path.Combine(disk, NodeReleaseAssets.SetupName("3.101.0")), FakeGitHub.Setup("3.101.0"));

        var imported = await ImportAsync(relay, new { path = "disk1" });
        Assert.Equal("done", imported.GetProperty("state").GetString());
        Assert.Equal("v3.102.0", Assert.Single(imported.GetProperty("supplied").EnumerateArray()).GetString());
        Assert.Contains("no InferHub-Node-Setup-3.101.0-win-x64.exe.sha256 beside it", imported.GetProperty("notes")[0].GetString());

        using var node = SealedClient(relay);
        using var list = JsonDocument.Parse(await node.GetStringAsync($"{relay}/node/relay/releases"));
        Assert.Equal(["v3.102.0", "v3.103.0"], list.RootElement.EnumerateArray().Select(r => r.GetProperty("tag_name").GetString()));

        var found = await new GitHubReleaseFeed(node, new Uri($"{relay}/node/relay/releases")).NewestAboveAsync(new Version(3, 100, 0), CancellationToken.None);
        Assert.Equal(new Version(3, 103, 0), found!.Version);
    }

    [Fact]
    public async Task ASetupThatIsNotWhatItsChecksumSaysIsNeverSupplied()
    {
        var (relay, fake) = await StartAsync();
        fake.Corrupt = true;

        var job = await SupplyAsync(relay, new { release = "v3.104.0" });
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Contains("nothing was supplied", job.GetProperty("error").GetString());

        using var node = SealedClient(relay);
        Assert.Equal("[]", await node.GetStringAsync($"{relay}/node/relay/releases"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory, "supply", "blobs")));
    }

    [Fact]
    public async Task WhatWasNotSuppliedIsRefusedAndGitHubIsNeverAskedForANode()
    {
        var (relay, fake) = await StartAsync();
        using var node = SealedClient(relay);

        var missing = await node.GetAsync($"{relay}/node/relay/releases/download/v3.104.0/{NodeReleaseAssets.SetupName("3.104.0")}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(NodeRelay.NotSupplied, missing.Headers.GetValues(NodeRelay.SupplyHeader).Single());
        Assert.Contains("POST /api/admin/supply", await missing.Content.ReadAsStringAsync());
        Assert.Equal("[]", await node.GetStringAsync($"{relay}/node/relay/releases"));
        Assert.Equal(0, fake.Requests);

        // The relay's door is the node's: no secret, no list.
        using var stranger = new HttpClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync($"{relay}/node/relay/releases")).StatusCode);

        // Only the files a supplied release holds: not another name under its tag.
        Assert.Equal("done", (await SupplyAsync(relay, new { release = "v3.104.0" })).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await node.GetAsync($"{relay}/node/relay/releases/download/v3.104.0/other.exe")).StatusCode);
    }

    [Fact]
    public async Task ARequestThatNamesNoReleaseOrTwoThingsIsABadRequest()
    {
        var (relay, _) = await StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(relay).PostAsJsonAsync("/api/admin/supply", new { release = "3.104.0-rc1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(relay).PostAsJsonAsync("/api/admin/supply", new { release = "desktop/v0.3.0" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin(relay).PostAsJsonAsync("/api/admin/supply", new { release = "latest", ollama = "tiny:1b" })).StatusCode);

        // A tag GitHub does not list, or lists without its setup yet, fails naming why.
        Assert.Contains("lists no release v3.200.0", (await SupplyAsync(relay, new { release = "v3.200.0" })).GetProperty("error").GetString());
        Assert.Contains("minutes after the tag", (await SupplyAsync(relay, new { release = "v3.107.0" })).GetProperty("error").GetString());
    }

    [Fact]
    public async Task WithTheSupplyOffTheHubSaysSoAndTheSiteChecklistNamesTheGap()
    {
        var (relay, _) = await StartAsync(supply: false);
        using var node = SealedClient(relay);

        var off = await node.GetAsync($"{relay}/node/relay/releases");
        Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
        Assert.Contains("Relay:Supply:Enabled=false", await off.Content.ReadAsStringAsync());

        var report = SiteEndpoints.Report(hub!.Services, DateTimeOffset.UtcNow);
        var line = Assert.Single(report.Checks, check => check.Id == "updates-supplied");
        Assert.False(line.Ok);
        Assert.Equal(SiteCheck.Recommended, line.Severity);
    }

    public async ValueTask DisposeAsync()
    {
        if (hub is not null)
        {
            await hub.StopAsync();
            await hub.DisposeAsync();
        }

        if (github is not null)
        {
            await github.DisposeAsync();
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The node's side: the real sealed handler with the hub as its coordinator, as a sealed node's factory clients have it.</summary>
    private static HttpClient SealedClient(string relay)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Node:Sealed"] = "true",
                ["Coordinator:Url"] = relay,
                ["Coordinator:EnrollmentSecret"] = Secret
            })
            .Build();

        return new HttpClient(new SealedEgressHandler(new SealedEgress(configuration)) { InnerHandler = new SocketsHttpHandler() });
    }

    private async Task<(string Relay, FakeGitHub Fake)> StartAsync(bool supply = true)
    {
        github = await FakeGitHub.StartAsync();
        var options = new RelayOptions
        {
            Enabled = true,
            Releases = github.Url + "/repos/x/releases",
            Supply =
            {
                Enabled = supply,
                DataDirectory = Path.Combine(directory, "supply"),
                ImportDirectory = Path.Combine(directory, "import"),
                KeepFreeGiB = 0
            }
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.Configure<ApiKeyOptions>(o => o.NodeEnrollmentSecret = Secret);
        builder.Services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<RelayOptions>>(new Fixed(options));
        builder.Services.AddSingleton<NodeAuthFilter>();
        builder.Services.AddNodeRelay();

        hub = builder.Build();
        hub.MapNodeRelay();
        hub.MapRelaySupply();
        hub.MapGet("/api/status", (IServiceProvider services) => Results.Ok(new
        {
            relay = StatusEndpoint.BuildRelayBlock(
                services.GetService<RelayCache>(), services.GetService<RelaySupply>(), services.GetService<SupplyJobs>())
        }));
        await hub.StartAsync();
        return (hub.Urls.First(), github);
    }

    private static HttpClient Admin(string relay) => new() { BaseAddress = new Uri(relay) };

    private static async Task<JsonElement> SupplyAsync(string relay, object request)
        => await WaitAsync(relay, await Admin(relay).PostAsJsonAsync("/api/admin/supply", request));

    private static async Task<JsonElement> ImportAsync(string relay, object request)
        => await WaitAsync(relay, await Admin(relay).PostAsJsonAsync("/api/admin/supply/import", request));

    private static async Task<JsonElement> WaitAsync(string relay, HttpResponseMessage started)
    {
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var id = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        for (var i = 0; i < 400; i++)
        {
            var job = await Admin(relay).GetFromJsonAsync<JsonElement>($"/api/admin/supply/jobs/{id}");

            if (job.GetProperty("state").GetString() != "running")
            {
                return job;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("the supply job did not finish");
    }

    private static async Task<JsonDocument> StatusAsync(string relay)
        => JsonDocument.Parse(await Admin(relay).GetStringAsync("/api/status"));

    private sealed class Fixed(RelayOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<RelayOptions>
    {
        public RelayOptions CurrentValue => value;

        public RelayOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<RelayOptions, string?> listener) => null;
    }

    /// <summary>
    /// GitHub's release list for a repo: a stable release, a newer one, a pre-release and a draft above them, and one tagged
    /// before its setup was attached. Each asset redirects to an object store on another host name, which counts.
    /// </summary>
    private sealed class FakeGitHub : IAsyncDisposable
    {
        private WebApplication app = null!;
        private int requests;
        private bool disposed;

        public string Url { get; private set; } = null!;

        public bool Corrupt { get; set; }

        public int Requests => requests;

        public ConcurrentBag<string> Objects { get; } = [];

        public static byte[] Setup(string version) => Encoding.UTF8.GetBytes($"MZ setup for {version} " + new string('x', 5000));

        public static string Checksum(string version) => Convert.ToHexStringLower(SHA256.HashData(Setup(version)));

        public static async Task<FakeGitHub> StartAsync()
        {
            var fake = new FakeGitHub();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            fake.app = builder.Build();
            fake.app.Use(async (context, next) =>
            {
                Interlocked.Increment(ref fake.requests);
                await next();
            });

            fake.app.MapGet("/repos/x/releases", (HttpContext context) =>
            {
                Assert.False(string.IsNullOrEmpty(context.Request.Headers.UserAgent.ToString()));
                return Results.Json(new object[]
                {
                    Release(fake, "v3.107.0", withAssets: false),
                    Release(fake, "v3.106.0", draft: true),
                    Release(fake, "v3.105.0-rc1", prerelease: true),
                    Release(fake, "v3.104.0"),
                    Release(fake, "v3.103.0")
                });
            });

            fake.app.MapGet("/download/{tag}/{name}", (string tag, string name)
                => Results.Redirect($"{fake.Url.Replace("127.0.0.1", "localhost")}/objects/{name}?sig=secret"));

            fake.app.MapGet("/objects/{name}", async (string name, HttpContext context) =>
            {
                fake.Objects.Add(name);
                var version = name[NodeReleaseAssets.SetupPrefix.Length..name.IndexOf(NodeReleaseAssets.SetupSuffix, StringComparison.Ordinal)];
                var body = name.EndsWith(".sha256", StringComparison.Ordinal)
                    ? Encoding.UTF8.GetBytes($"{Checksum(version)}  {NodeReleaseAssets.SetupName(version)}\n")
                    : Setup(version);

                if (fake.Corrupt && !name.EndsWith(".sha256", StringComparison.Ordinal))
                {
                    body = body.Select((b, i) => i == 10 ? (byte)(b ^ 0xFF) : b).ToArray();
                }

                context.Response.ContentLength = body.Length;
                await context.Response.Body.WriteAsync(body);
            });

            await fake.app.StartAsync();
            fake.Url = fake.app.Urls.First();
            return fake;
        }

        private static object Release(FakeGitHub fake, string tag, bool draft = false, bool prerelease = false, bool withAssets = true)
        {
            var version = tag.TrimStart('v');
            var setup = NodeReleaseAssets.SetupName(version);
            return new
            {
                tag_name = tag,
                draft,
                prerelease,
                html_url = $"https://github.example/releases/tag/{tag}",
                assets = withAssets
                    ? new object[]
                    {
                        new { name = setup, size = Setup(version).Length, browser_download_url = $"{fake.Url}/download/{tag}/{setup}" },
                        new { name = setup + ".sha256", size = 100, browser_download_url = $"{fake.Url}/download/{tag}/{setup}.sha256" },
                        new { name = "InferHub-Desktop-win-x64.zip", size = 1, browser_download_url = $"{fake.Url}/download/{tag}/desktop.zip" }
                    }
                    : []
            };
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
