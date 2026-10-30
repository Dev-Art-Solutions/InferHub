using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Services;
using InferHub.Node.Backends.HuggingFace;
using InferHub.Shared.Contracts;
using InferHub.Shared.HuggingFace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InferHub.Tests;

/// <summary>
/// Phase 124 across real sockets: an upstream that answers as registry.ollama.ai and Hugging Face do (a first hop, then
/// a redirect to a CDN that counts), a hub that fetches a model from it once — and then the upstream is stopped, and
/// the node's own Hugging Face client pulls through the relay as if nothing happened.
/// </summary>
public sealed class RelaySupplyMeshTests : IAsyncDisposable
{
    private const string Secret = "relay-supply-secret";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-supply-mesh-" + Guid.NewGuid().ToString("N"));

    private WebApplication? hub;
    private Upstream? upstream;

    [Fact]
    public async Task AnOllamaModelFetchedOnceIsServedWithTheUpstreamGone()
    {
        var (relay, fake) = await StartAsync();

        var job = await SupplyAsync(relay, new { ollama = "tiny:1b" });
        Assert.Equal("done", job.GetProperty("state").GetString());
        Assert.Equal(fake.Blob.Length + fake.Config.Length, job.GetProperty("doneBytes").GetInt64());
        await fake.DisposeAsync();
        upstream = null;

        var manifest = await GetAsync($"{relay}/node/relay/ollama-registry/v2/library/tiny/manifests/1b");
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal(NodeRelay.Supplied, Header(manifest, NodeRelay.SupplyHeader));
        Assert.Equal(fake.Manifest, await manifest.Content.ReadAsStringAsync());
        Assert.Equal(RelaySupply.OllamaManifestType, manifest.Content.Headers.ContentType!.MediaType);

        var part = await GetAsync($"{relay}/node/relay/ollama-registry/v2/library/tiny/blobs/sha256:{fake.Sha}", range: (1000, 1999));
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(fake.Blob.AsSpan(1000, 1000).ToArray(), await part.Content.ReadAsByteArrayAsync());

        var head = await SendAsync(HttpMethod.Head, $"{relay}/node/relay/ollama-registry/v2/library/tiny/blobs/sha256:{fake.Sha}");
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(fake.Blob.Length, head.Content.Headers.ContentLength);

        // Not supplied and not Only: relayed as before — and with the upstream gone, that is the 502 3.82 gave.
        Assert.Equal(HttpStatusCode.BadGateway, (await GetAsync($"{relay}/node/relay/ollama-registry/v2/library/other/manifests/1b")).StatusCode);
    }

    [Fact]
    public async Task AHuggingFaceRepoFetchedOnceIsPulledByTheNodesOwnClientWithTheUpstreamGone()
    {
        var (relay, fake) = await StartAsync();

        var job = await SupplyAsync(relay, new { huggingFace = "org/Tiny-GGUF", include = new[] { "*.gguf", "config.json" } });
        Assert.Equal("done", job.GetProperty("state").GetString());
        Assert.Single(fake.CdnRequests);
        // The hub's own token reached Hugging Face's first hop, never the CDN.
        Assert.All(fake.FirstHops, auth => Assert.Equal("Bearer hub_token", auth));
        Assert.All(fake.CdnRequests, auth => Assert.Equal(string.Empty, auth));
        await fake.DisposeAsync();
        upstream = null;

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add(NodeRelay.SecretHeader, Secret);
        var client = new HuggingFaceClient(http, new Uri($"{relay}/node/relay/hf/"), token: null);
        var reference = new HfReference("org", "Tiny-GGUF");

        var commit = await client.CommitAsync(reference, CancellationToken.None);
        Assert.Equal(Commit, commit);
        var tree = await client.TreeAsync(reference, commit, CancellationToken.None);
        Assert.Equal(["config.json", "tiny.gguf"], tree.Select(file => file.Path).Order());
        Assert.All(tree, file => Assert.NotNull(file.Sha256));

        var target = Path.Combine(directory, "node", "tiny.gguf");
        await client.DownloadAsync(reference, commit, tree.Single(file => file.Path == "tiny.gguf"), target, _ => { }, CancellationToken.None);
        Assert.Equal(fake.Blob, await File.ReadAllBytesAsync(target));

        var config = Path.Combine(directory, "node", "config.json");
        await client.DownloadAsync(reference, commit, tree.Single(file => file.Path == "config.json"), config, _ => { }, CancellationToken.None);
        Assert.Equal(fake.Config, await File.ReadAllBytesAsync(config));

        using var info = await client.ModelInfoAsync(reference, CancellationToken.None);
        Assert.Equal("text-generation", info.RootElement.GetProperty("pipeline_tag").GetString());
    }

    [Fact]
    public async Task UnderOnlyWhatIsNotSuppliedIsRefusedAndTheUpstreamIsNeverAsked()
    {
        var (relay, fake) = await StartAsync(only: true);

        var refused = await GetAsync($"{relay}/node/relay/ollama-registry/v2/library/tiny/manifests/1b");
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(NodeRelay.NotSupplied, Header(refused, NodeRelay.SupplyHeader));
        Assert.Contains("Relay:Supply:Only", await refused.Content.ReadAsStringAsync());
        Assert.Equal(0, fake.Requests);

        // The node's own client ends with the hub's sentence, not "Hugging Face has no…".
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add(NodeRelay.SecretHeader, Secret);
        var client = new HuggingFaceClient(http, new Uri($"{relay}/node/relay/hf/"), token: null);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CommitAsync(new HfReference("org", "Tiny-GGUF"), CancellationToken.None));
        Assert.Contains("the coordinator does not supply 'org/Tiny-GGUF'", error.Message);
        Assert.Contains("POST /api/admin/supply", error.Message);

        // The admin's fetch is what reaches the upstream, Only or not.
        Assert.Equal("done", (await SupplyAsync(relay, new { ollama = "tiny:1b" })).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, (await GetAsync($"{relay}/node/relay/ollama-registry/v2/library/tiny/manifests/1b")).StatusCode);

        using var status = await StatusAsync(relay);
        var supply = status.RootElement.GetProperty("relay").GetProperty("supply");
        Assert.Equal(1, supply.GetProperty("models").GetInt32());
        Assert.True(supply.GetProperty("only").GetBoolean());
        Assert.Equal(2, supply.GetProperty("refused").GetInt64());
        Assert.False(status.RootElement.GetProperty("relay").TryGetProperty("cache", out _));
    }

    [Fact]
    public async Task BytesThatAreNotWhatTheUpstreamNamedAreNeverSupplied()
    {
        var (relay, fake) = await StartAsync();
        fake.Corrupt = true;

        var job = await SupplyAsync(relay, new { ollama = "tiny:1b" });
        Assert.Equal("failed", job.GetProperty("state").GetString());
        Assert.Contains("hashes to", job.GetProperty("error").GetString());

        var listing = await Admin(relay).GetFromJsonAsync<JsonElement>("/api/admin/supply");
        Assert.Empty(listing.GetProperty("models").EnumerateArray());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory, "supply", "partial")));
    }

    [Theory]
    [InlineData("manifests", "registry.ollama.ai")]
    // Ollama 0.40 writes a new pull here (found on the live box, 2026-10-31).
    [InlineData("manifests-v2", "ollama.com")]
    public async Task AnOllamaModelsDirectoryCarriedInOnADiskIsImported(string manifests, string host)
    {
        var (relay, fake) = await StartAsync(only: true);
        var models = Path.Combine(directory, "import", "disk1", ".ollama", "models");
        Directory.CreateDirectory(Path.Combine(models, manifests, host, "library", "tiny"));
        Directory.CreateDirectory(Path.Combine(models, "blobs"));
        await File.WriteAllTextAsync(Path.Combine(models, manifests, host, "library", "tiny", "1b"), fake.Manifest);
        await File.WriteAllBytesAsync(Path.Combine(models, "blobs", "sha256-" + fake.Sha), fake.Blob);
        await File.WriteAllBytesAsync(Path.Combine(models, "blobs", "sha256-" + fake.ConfigSha), fake.Config);

        var job = await ImportAsync(relay, new { path = "disk1/.ollama" });
        Assert.Equal("done", job.GetProperty("state").GetString());
        Assert.Equal(["library/tiny:1b"], job.GetProperty("supplied").EnumerateArray().Select(name => name.GetString()));

        var blob = await GetAsync($"{relay}/node/relay/ollama-registry/v2/library/tiny/blobs/sha256:{fake.Sha}");
        Assert.Equal(fake.Blob, await blob.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, fake.Requests);

        // The source is the medium's, and it is left as it was.
        Assert.True(File.Exists(Path.Combine(models, "blobs", "sha256-" + fake.Sha)));
    }

    [Fact]
    public async Task AHuggingFaceCacheIsImportedAndACorruptBlobInItIsRefused()
    {
        var (relay, fake) = await StartAsync(only: true);
        var repo = Path.Combine(directory, "import", "hf", "hub", "models--org--Tiny-GGUF");
        Directory.CreateDirectory(Path.Combine(repo, "refs"));
        Directory.CreateDirectory(Path.Combine(repo, "snapshots", Commit));
        await File.WriteAllTextAsync(Path.Combine(repo, "refs", "main"), Commit);
        await File.WriteAllBytesAsync(Path.Combine(repo, "snapshots", Commit, "tiny.gguf"), fake.Blob);
        await File.WriteAllBytesAsync(Path.Combine(repo, "snapshots", Commit, "config.json"), fake.Config);

        var job = await ImportAsync(relay, new { path = "hf" });
        Assert.Equal("done", job.GetProperty("state").GetString());

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add(NodeRelay.SecretHeader, Secret);
        var client = new HuggingFaceClient(http, new Uri($"{relay}/node/relay/hf/"), token: null);
        var reference = new HfReference("org", "Tiny-GGUF");
        var tree = await client.TreeAsync(reference, await client.CommitAsync(reference, CancellationToken.None), CancellationToken.None);
        Assert.Equal(fake.Sha, tree.Single(file => file.Path == "tiny.gguf").Sha256);

        var escape = await Admin(relay).PostAsJsonAsync("/api/admin/supply/import", new { path = "../" });
        Assert.Equal(HttpStatusCode.BadRequest, escape.StatusCode);
    }

    [Fact]
    public async Task WithTheSupplyOffTheRelayIsThreeEightyThreesAndTheRoutesSaySo()
    {
        var (relay, fake) = await StartAsync(supply: false);

        var manifest = await GetAsync($"{relay}/node/relay/ollama-registry/v2/library/tiny/manifests/1b");
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Null(Header(manifest, NodeRelay.SupplyHeader));
        Assert.Equal(1, fake.Requests);

        var off = await Admin(relay).PostAsJsonAsync("/api/admin/supply", new { ollama = "tiny:1b" });
        Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
        Assert.Contains("Relay:Supply:Enabled=false", await off.Content.ReadAsStringAsync());
        Assert.False(Directory.Exists(Path.Combine(directory, "supply")));
    }

    public async ValueTask DisposeAsync()
    {
        if (hub is not null)
        {
            await hub.StopAsync();
            await hub.DisposeAsync();
        }

        if (upstream is not null)
        {
            await upstream.DisposeAsync();
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<(string Relay, Upstream Fake)> StartAsync(bool supply = true, bool only = false)
    {
        upstream = await Upstream.StartAsync();
        var options = new RelayOptions
        {
            Enabled = true,
            HuggingFace = upstream.Url,
            OllamaRegistry = upstream.Url,
            Supply =
            {
                Enabled = supply,
                Only = only,
                DataDirectory = Path.Combine(directory, "supply"),
                ImportDirectory = Path.Combine(directory, "import"),
                HuggingFaceToken = "hub_token",
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
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

        hub = builder.Build();
        hub.MapNodeRelay();
        hub.MapRelaySupply();
        hub.MapGet("/api/status", (IServiceProvider services) => Results.Ok(new
        {
            relay = StatusEndpoint.BuildRelayBlock(
                services.GetService<RelayCache>(), services.GetService<RelaySupply>(), services.GetService<SupplyJobs>())
        }));
        await hub.StartAsync();
        return (hub.Urls.First(), upstream);
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

    private static Task<HttpResponseMessage> GetAsync(string url, (long From, long To)? range = null)
        => SendAsync(HttpMethod.Get, url, range);

    private static async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, (long From, long To)? range = null)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(NodeRelay.SecretHeader, Secret);

        if (range is { } r)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(r.From, r.To);
        }

        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(",", values)
            : null;

    private sealed class Fixed(RelayOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<RelayOptions>
    {
        public RelayOptions CurrentValue => value;

        public RelayOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<RelayOptions, string?> listener) => null;
    }

    /// <summary>An Ollama model (a config and one layer) and a Hugging Face repo (a GGUF over LFS, a small config), behind a CDN that counts.</summary>
    private sealed class Upstream : IAsyncDisposable
    {
        private WebApplication app = null!;
        private int requests;

        public byte[] Blob { get; } = RandomBytes(200_000);

        public byte[] Config { get; } = Encoding.UTF8.GetBytes("{\"model_format\":\"gguf\"}");

        public string Sha => Hex(SHA256.HashData(Blob));

        public string ConfigSha => Hex(SHA256.HashData(Config));

        public string Manifest => JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            mediaType = RelaySupply.OllamaManifestType,
            config = new { mediaType = "application/vnd.docker.container.image.v1+json", digest = "sha256:" + ConfigSha, size = Config.Length },
            layers = new[] { new { mediaType = "application/vnd.ollama.image.model", digest = "sha256:" + Sha, size = Blob.Length } }
        });

        public string Url { get; private set; } = null!;

        public bool Corrupt { get; set; }

        public int Requests => requests;

        public ConcurrentQueue<string> FirstHops { get; } = new();

        public ConcurrentQueue<string> CdnRequests { get; } = new();

        public static async Task<Upstream> StartAsync()
        {
            var fake = new Upstream();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            fake.app = builder.Build();
            fake.app.Use(async (context, next) =>
            {
                Interlocked.Increment(ref fake.requests);
                await next();
            });

            fake.app.MapGet("/v2/library/tiny/manifests/1b", (HttpContext context) =>
            {
                context.Response.Headers["Docker-Content-Digest"] = "sha256:" + Hex(SHA256.HashData(Encoding.UTF8.GetBytes(fake.Manifest)));
                return Results.Text(fake.Manifest, RelaySupply.OllamaManifestType);
            });

            fake.app.MapGet("/v2/library/tiny/blobs/{digest}", (string digest)
                => Results.Redirect($"{fake.Url.Replace("127.0.0.1", "localhost")}/cdn/{digest["sha256:".Length..]}?sig=secret"));

            fake.app.MapGet("/api/models/org/Tiny-GGUF/revision/{revision}", (string revision, HttpContext context) =>
            {
                fake.FirstHops.Enqueue(context.Request.Headers.Authorization.ToString());
                return revision is "main" or Commit
                    ? Results.Json(new { id = "org/Tiny-GGUF", sha = Commit, pipeline_tag = "text-generation" })
                    : Results.NotFound();
            });

            fake.app.MapGet("/api/models/org/Tiny-GGUF/tree/{commit}", (HttpContext context) =>
            {
                fake.FirstHops.Enqueue(context.Request.Headers.Authorization.ToString());
                return Results.Json(new object[]
                {
                    new { type = "file", path = "tiny.gguf", size = 134, oid = "pointer", lfs = new { oid = fake.Sha, size = fake.Blob.Length } },
                    new { type = "file", path = "config.json", size = fake.Config.Length, oid = GitOid(fake.Config) },
                    new { type = "file", path = "README.md", size = 3, oid = GitOid("hi\n"u8.ToArray()) },
                    new { type = "directory", path = "sub", size = 0, oid = "x" }
                });
            });

            fake.app.MapGet("/org/Tiny-GGUF/resolve/{commit}/{**file}", (string file, HttpContext context) =>
            {
                fake.FirstHops.Enqueue(context.Request.Headers.Authorization.ToString());

                if (file == "config.json")
                {
                    return Results.Bytes(fake.Config, "application/json");
                }

                context.Response.Headers["X-Linked-Etag"] = $"\"{fake.Sha}\"";
                return Results.Redirect($"{fake.Url.Replace("127.0.0.1", "localhost")}/cdn/{fake.Sha}?X-Amz-Signature=secret");
            });

            fake.app.MapGet("/cdn/{sha}", async (string sha, HttpContext context) =>
            {
                fake.CdnRequests.Enqueue(context.Request.Headers.Authorization.ToString());
                var body = sha == fake.ConfigSha ? fake.Config : fake.Blob;

                if (fake.Corrupt)
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

        private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

        private static string GitOid(byte[] bytes)
            => Hex(SHA1.HashData(Encoding.ASCII.GetBytes($"blob {bytes.Length}\0").Concat(bytes).ToArray()));

        private static byte[] RandomBytes(int length)
        {
            var bytes = new byte[length];
            new Random(124).NextBytes(bytes);
            return bytes;
        }

        private bool disposed;

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
