using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InferHub.Tests;

/// <summary>
/// Phase 123 across real sockets: an upstream that answers as Hugging Face and registry.ollama.ai do — a first hop that
/// decides access and names the sha256, then a redirect to a CDN — and a hub whose relay keeps what passed through it.
/// The CDN's counter is the claim: the second node's bytes never left it.
/// </summary>
public sealed class RelayCacheMeshTests : IAsyncDisposable
{
    private const string Secret = "relay-cache-secret";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-relay-mesh-" + Guid.NewGuid().ToString("N"));

    private WebApplication? hub;
    private Upstream? upstream;

    [Fact]
    public async Task AHuggingFaceFileIsKeptAsItPassesAndTheNextNodeReadsItFromTheHub()
    {
        var (relay, fake) = await StartAsync();
        var url = $"{relay}/node/relay/hf/org/Tiny-GGUF/resolve/{Commit}/tiny.gguf";

        var first = await GetAsync(url, token: "hf_token");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("miss", Header(first, NodeRelay.CacheHeader));
        Assert.Equal(fake.Blob, await first.Content.ReadAsByteArrayAsync());
        await Until(() => Cache().Snapshot()!.Filled == 1);

        var second = await GetAsync(url, token: "hf_token");
        Assert.Equal("hit", Header(second, NodeRelay.CacheHeader));
        Assert.Equal(fake.Blob, await second.Content.ReadAsByteArrayAsync());

        var part = await GetAsync(url, token: "hf_token", range: new RangeHeaderValue(1000, 1999));
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal($"bytes 1000-1999/{fake.Blob.Length}", part.Content.Headers.ContentRange!.ToString());
        Assert.Equal(fake.Blob.AsSpan(1000, 1000).ToArray(), await part.Content.ReadAsByteArrayAsync());

        // D1: the first hop was asked all three times; the CDN served the body once.
        Assert.Equal(3, fake.FirstHops.Count);
        Assert.Single(fake.CdnRequests);
        // 122 D3 survives the hand-followed redirect: the token reached the first hop, never the CDN.
        Assert.All(fake.FirstHops, auth => Assert.Equal("Bearer hf_token", auth));
        Assert.All(fake.CdnRequests, request => Assert.Equal(string.Empty, request.Authorization));
    }

    [Fact]
    public async Task AFirstHopThatRefusesIsTheAnswerEvenWhenTheHubHoldsTheBytes()
    {
        var (relay, fake) = await StartAsync();
        var url = $"{relay}/node/relay/hf/org/Tiny-GGUF/resolve/{Commit}/tiny.gguf";

        await (await GetAsync(url, token: "hf_token")).Content.ReadAsByteArrayAsync();
        await Until(() => Cache().Snapshot()!.Filled == 1);

        // The repo is gated: a node without access does not get another node's download.
        var anonymous = await GetAsync(url, token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(Header(anonymous, NodeRelay.CacheHeader));
        Assert.Single(fake.CdnRequests);
    }

    [Fact]
    public async Task AnOllamaBlobPulledInPartsIsFetchedWholeOnceBesideIt()
    {
        var (relay, fake) = await StartAsync();
        var url = $"{relay}/node/relay/ollama-registry/v2/library/tiny/blobs/sha256:{fake.Sha}";

        var first = await GetAsync(url, range: new RangeHeaderValue(0, 999));
        Assert.Equal(HttpStatusCode.PartialContent, first.StatusCode);
        Assert.Equal("miss", Header(first, NodeRelay.CacheHeader));
        Assert.Equal(fake.Blob.AsSpan(0, 1000).ToArray(), await first.Content.ReadAsByteArrayAsync());

        await Until(() => Cache().Snapshot()!.Filled == 1);

        var next = await GetAsync(url, range: new RangeHeaderValue(1000, null));
        Assert.Equal(HttpStatusCode.PartialContent, next.StatusCode);
        Assert.Equal("hit", Header(next, NodeRelay.CacheHeader));
        Assert.Equal("sha256:" + fake.Sha, Header(next, "Docker-Content-Digest"));
        Assert.Equal(fake.Blob.AsSpan(1000).ToArray(), await next.Content.ReadAsByteArrayAsync());

        // One ranged request for the node, one whole one for the hub's own copy, and nothing since.
        Assert.Equal(["bytes=0-999", ""], fake.CdnRequests.Select(request => request.Range).Order().Reverse().ToArray());
    }

    [Fact]
    public async Task BytesThatAreNotWhatTheUpstreamNamedAreRelayedButNotKept()
    {
        var (relay, fake) = await StartAsync();
        fake.Corrupt = true;
        var url = $"{relay}/node/relay/hf/org/Tiny-GGUF/resolve/{Commit}/tiny.gguf";

        var first = await GetAsync(url, token: "hf_token");
        await first.Content.ReadAsByteArrayAsync();
        await Until(() => Cache().Snapshot()!.Discarded == 1);

        // The node's own check (98's sha256) is what refuses its copy; the hub only declines to keep it.
        Assert.Equal("miss", Header(await GetAsync(url, token: "hf_token"), NodeRelay.CacheHeader));
        Assert.Equal(0, Cache().Snapshot()!.Entries);
    }

    [Fact]
    public async Task WithTheCacheOffTheRelayIsThreeEightyTwos()
    {
        var (relay, fake) = await StartAsync(cache: false);
        var url = $"{relay}/node/relay/hf/org/Tiny-GGUF/resolve/{Commit}/tiny.gguf";

        var first = await GetAsync(url, token: "hf_token");
        Assert.Equal(fake.Blob, await first.Content.ReadAsByteArrayAsync());
        Assert.Null(Header(first, NodeRelay.CacheHeader));
        await (await GetAsync(url, token: "hf_token")).Content.ReadAsByteArrayAsync();

        Assert.Equal(2, fake.CdnRequests.Count);
        Assert.False(Directory.Exists(directory));
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

    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private RelayCache Cache() => hub!.Services.GetRequiredService<RelayCache>();

    private async Task<(string Relay, Upstream Fake)> StartAsync(bool cache = true)
    {
        upstream = await Upstream.StartAsync();
        var options = new RelayOptions
        {
            Enabled = true,
            HuggingFace = upstream.Url,
            OllamaRegistry = upstream.Url,
            Cache = { Enabled = cache, DataDirectory = directory, KeepFreeGiB = 0 }
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
        await hub.StartAsync();
        return (hub.Urls.First(), upstream);
    }

    private static async Task<HttpResponseMessage> GetAsync(string url, string? token = null, RangeHeaderValue? range = null)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(NodeRelay.SecretHeader, Secret);
        request.Headers.Range = range;

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(",", values)
            : null;

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }

    private sealed class Fixed(RelayOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<RelayOptions>
    {
        public RelayOptions CurrentValue => value;

        public RelayOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<RelayOptions, string?> listener) => null;
    }

    /// <summary>One gated Hugging Face file and one registry blob, both behind a redirect to a CDN that counts.</summary>
    private sealed class Upstream : IAsyncDisposable
    {
        private WebApplication app = null!;

        public byte[] Blob { get; } = RandomBytes(200_000);

        public string Sha => Convert.ToHexStringLower(SHA256.HashData(Blob));

        public string Url { get; private set; } = null!;

        public bool Corrupt { get; set; }

        public ConcurrentQueue<string> FirstHops { get; } = new();

        public ConcurrentQueue<(string Range, string Authorization)> CdnRequests { get; } = new();

        public static async Task<Upstream> StartAsync()
        {
            var fake = new Upstream();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            fake.app = builder.Build();

            fake.app.MapGet("/org/Tiny-GGUF/resolve/{commit}/tiny.gguf", (HttpContext context) =>
            {
                var authorization = context.Request.Headers.Authorization.ToString();
                fake.FirstHops.Enqueue(authorization);

                if (authorization.Length == 0)
                {
                    return Results.StatusCode(401);
                }

                context.Response.Headers["X-Linked-Etag"] = $"\"{fake.Sha}\"";
                context.Response.Headers["X-Linked-Size"] = fake.Blob.Length.ToString();
                // Another host name for the same listener: a CDN is never the first hop's host.
                return Results.Redirect(fake.Url.Replace("127.0.0.1", "localhost") + $"/cdn/{fake.Sha}?X-Amz-Signature=secret");
            });

            fake.app.MapGet("/v2/library/tiny/blobs/{digest}", (string digest)
                => Results.Redirect($"{fake.Url}/cdn/{digest["sha256:".Length..]}?sig=secret", permanent: false, preserveMethod: true));

            fake.app.MapGet("/cdn/{sha}", async (HttpContext context) =>
            {
                var range = context.Request.Headers.Range.ToString();
                fake.CdnRequests.Enqueue((range, context.Request.Headers.Authorization.ToString()));
                var body = fake.Corrupt ? fake.Blob.Select((b, i) => i == 10 ? (byte)(b ^ 0xFF) : b).ToArray() : fake.Blob;
                int from = 0, to = body.Length - 1;

                if (range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    var parts = range["bytes=".Length..].Split('-');
                    from = int.Parse(parts[0]);
                    to = parts[1].Length > 0 ? int.Parse(parts[1]) : to;
                    context.Response.StatusCode = 206;
                    context.Response.Headers.ContentRange = $"bytes {from}-{to}/{body.Length}";
                }

                context.Response.ContentLength = to - from + 1;
                await context.Response.Body.WriteAsync(body.AsMemory(from, to - from + 1));
            });

            await fake.app.StartAsync();
            fake.Url = fake.app.Urls.First();
            return fake;
        }

        private static byte[] RandomBytes(int length)
        {
            var bytes = new byte[length];
            new Random(123).NextBytes(bytes);
            return bytes;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
