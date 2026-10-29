using System.Net;
using System.Security.Cryptography;
using System.Text;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using InferHub.Node.Backends;
using InferHub.Node.Backends.HuggingFace;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using InferHub.Shared.HuggingFace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 122 across real sockets: a sealed node's Hugging Face download and its Ollama pull both go to the hub's
/// relay, which fetches from upstreams the node never addresses — and the loopback registry answers the way Ollama
/// needs (a cross-host redirect for a blob, then ranges).
/// </summary>
public class SealedRelayMeshTests
{
    private const string Secret = "sealed-mesh-secret";

    [Fact]
    public async Task AHuggingFaceDownloadGoesThroughTheHubAndArrivesWhole()
    {
        await using var hf = await FakeHuggingFace.StartAsync();
        var bytes = FakeHuggingFace.Bytes(300_000, 7);
        hf.Repo("bartowski/Tiny-GGUF", ("Tiny-Q4_K_M.gguf", bytes));
        hf.Gated.Add("bartowski/Tiny-GGUF");

        await using var hub = await RelayHub.StartAsync(hf.Url, "http://127.0.0.1:9/");
        var egress = hub.Egress();
        var client = new HuggingFaceClient(Sealed(egress), egress.RelayBase(NodeRelay.HuggingFace)!, "hf_token");

        var reference = new HfReference("bartowski", "Tiny-GGUF");
        var commit = await client.CommitAsync(reference, CancellationToken.None);
        var file = Assert.Single(await client.TreeAsync(reference, commit, CancellationToken.None));

        var destination = Path.Combine(Path.GetTempPath(), "inferhub-sealed-" + Guid.NewGuid().ToString("N"), file.Path);
        await client.DownloadAsync(reference, commit, file, destination, _ => { }, CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        // The node's own token reached Hugging Face through the hub; the hub logged the download.
        Assert.Equal("Bearer hf_token", hf.LastAuthorization);
        Assert.Contains(hub.Relayed, line => line.Contains("hf/bartowski/Tiny-GGUF/resolve/"));
        Directory.Delete(Path.GetDirectoryName(destination)!, recursive: true);
    }

    [Fact]
    public async Task TheRelayRefusesWithoutTheSecretAndWhenOff()
    {
        await using var hf = await FakeHuggingFace.StartAsync();
        await using var hub = await RelayHub.StartAsync(hf.Url, "http://127.0.0.1:9/");
        using var plain = new HttpClient();

        var anonymous = await plain.GetAsync(hub.Url + "/node/relay/hf/api/models/a/b");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        hub.Options.Enabled = false;
        using var request = new HttpRequestMessage(HttpMethod.Get, hub.Url + "/node/relay/hf/api/models/a/b");
        request.Headers.Add(NodeRelay.SecretHeader, Secret);
        var off = await plain.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        Assert.Contains("Relay:Enabled=false", await off.Content.ReadAsStringAsync());

        hub.Options.Enabled = true;
        using var unknown = new HttpRequestMessage(HttpMethod.Get, hub.Url + "/node/relay/github/repos/x");
        unknown.Headers.Add(NodeRelay.SecretHeader, Secret);
        Assert.Equal(HttpStatusCode.NotFound, (await plain.SendAsync(unknown)).StatusCode);
    }

    [Fact]
    public async Task TheLoopbackRegistryAnswersAsOllamaNeeds()
    {
        await using var registry = await FakeRegistry.StartAsync();
        await using var hub = await RelayHub.StartAsync("http://127.0.0.1:9/", registry.Url);
        var egress = hub.Egress();

        await using var shim = LoopbackRegistry.Start(() => Sealed(egress), egress.RelayBase(NodeRelay.OllamaRegistry)!, NullLogger.Instance);
        using var ollama = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var root = $"http://127.0.0.1:{shim.Port}";

        using var manifest = new HttpRequestMessage(HttpMethod.Get, $"{root}/v2/library/tiny/manifests/1b");
        manifest.Headers.Add("Accept", "application/vnd.docker.distribution.manifest.v2+json");
        var answer = await ollama.SendAsync(manifest);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains(registry.Digest, await answer.Content.ReadAsStringAsync());
        Assert.Equal("application/vnd.docker.distribution.manifest.v2+json", registry.LastAccept);

        using var head = new HttpRequestMessage(HttpMethod.Head, $"{root}/v2/library/tiny/blobs/{registry.Digest}");
        var size = await ollama.SendAsync(head);
        Assert.Equal(registry.Blob.Length, size.Content.Headers.ContentLength);

        // A blob is a redirect to another host name for the same listener — Ollama refuses one to the same host.
        var blob = await ollama.GetAsync($"{root}/v2/library/tiny/blobs/{registry.Digest}");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, blob.StatusCode);
        var location = blob.Headers.Location!;
        Assert.Equal("localhost", location.Host);
        Assert.Equal(shim.Port, location.Port);

        using var part = new HttpRequestMessage(HttpMethod.Get, location);
        part.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, 1999);
        var ranged = await ollama.SendAsync(part);
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal(registry.Blob.AsSpan(1000, 1000).ToArray(), await ranged.Content.ReadAsByteArrayAsync());

        // The hub followed the registry's own redirect to its CDN; the node never addressed either.
        Assert.Contains("bytes=1000-1999", registry.CdnRanges);
    }

    [Fact]
    public async Task ASealedNodesClientsReachOnlyTheHubAndThisNetwork()
    {
        await using var hub = await RelayHub.StartAsync("http://127.0.0.1:9/", "http://127.0.0.1:9/");
        using var client = Sealed(hub.Egress());

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://huggingface.co/api/models"));
        Assert.Contains("Node:Sealed", refused.Message);
        Assert.Contains("huggingface.co", refused.Message);

        // The hub itself, health and all, is reachable.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(hub.Url + "/health")).StatusCode);
    }

    private static HttpClient Sealed(SealedEgress egress)
        => new(new SealedEgressHandler(egress) { InnerHandler = new SocketsHttpHandler() });

    private sealed class RelayHub : IAsyncDisposable
    {
        private WebApplication app = null!;

        public string Url { get; private set; } = null!;

        public RelayOptions Options { get; } = new() { Enabled = true };

        public System.Collections.Concurrent.ConcurrentQueue<string> Relayed { get; } = new();

        public static async Task<RelayHub> StartAsync(string huggingFace, string registry)
        {
            var hub = new RelayHub();
            hub.Options.HuggingFace = huggingFace;
            hub.Options.OllamaRegistry = registry;

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new LineCollector(hub.Relayed));
            builder.Services.Configure<ApiKeyOptions>(o => o.NodeEnrollmentSecret = Secret);
            builder.Services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<RelayOptions>>(new FixedMonitor<RelayOptions>(hub.Options));
            builder.Services.AddSingleton<NodeAuthFilter>();
            builder.Services.AddHttpClient(NodeRelayEndpoints.HttpClientName);

            hub.app = builder.Build();
            hub.app.MapGet("/health", () => "ok");
            hub.app.MapNodeRelay();
            await hub.app.StartAsync();
            hub.Url = hub.app.Urls.First();
            return hub;
        }

        public SealedEgress Egress() => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coordinator:Url"] = Url,
                ["Coordinator:EnrollmentSecret"] = Secret
            })
            .Build());

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class FixedMonitor<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class LineCollector(System.Collections.Concurrent.ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Collector(lines);

        public void Dispose()
        {
        }

        private sealed class Collector(System.Collections.Concurrent.ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => lines.Enqueue(formatter(state, exception));
        }
    }

    /// <summary>An Ollama registry as registry.ollama.ai answers: a blob GET redirects to its CDN, which serves ranges.</summary>
    private sealed class FakeRegistry : IAsyncDisposable
    {
        private WebApplication app = null!;

        public byte[] Blob { get; } = FakeHuggingFace.Bytes(50_000, 3);

        public string Digest => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Blob));

        public string Url { get; private set; } = null!;

        public string? LastAccept { get; private set; }

        public System.Collections.Concurrent.ConcurrentQueue<string> CdnRanges { get; } = new();

        public static async Task<FakeRegistry> StartAsync()
        {
            var fake = new FakeRegistry();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            fake.app = builder.Build();

            fake.app.MapGet("/v2/library/tiny/manifests/1b", (HttpContext context) =>
            {
                fake.LastAccept = context.Request.Headers.Accept.ToString();
                var manifest = $$"""{"schemaVersion":2,"layers":[{"mediaType":"application/vnd.ollama.image.model","digest":"{{fake.Digest}}","size":{{fake.Blob.Length}}}]}""";
                return Results.Text(manifest, "application/vnd.docker.distribution.manifest.v2+json", Encoding.UTF8);
            });

            fake.app.MapMethods("/v2/library/tiny/blobs/{digest}", [HttpMethods.Get, HttpMethods.Head], (string digest, HttpContext context) =>
            {
                if (HttpMethods.IsHead(context.Request.Method))
                {
                    context.Response.ContentLength = fake.Blob.Length;
                    return Results.Empty;
                }

                return Results.Redirect($"{fake.Url}/cdn/{digest}?X-Amz-Signature=secret", permanent: false, preserveMethod: true);
            });

            fake.app.MapGet("/cdn/{digest}", async (string digest, HttpContext context) =>
            {
                var range = context.Request.Headers.Range.ToString();
                CdnRange(fake, range, out var from, out var to);
                context.Response.StatusCode = range.Length > 0 ? 206 : 200;
                context.Response.ContentLength = to - from + 1;
                await context.Response.Body.WriteAsync(fake.Blob.AsMemory(from, to - from + 1));
            });

            await fake.app.StartAsync();
            fake.Url = fake.app.Urls.First();
            return fake;
        }

        private static void CdnRange(FakeRegistry fake, string range, out int from, out int to)
        {
            from = 0;
            to = fake.Blob.Length - 1;

            if (range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                fake.CdnRanges.Enqueue(range);
                var parts = range["bytes=".Length..].Split('-');
                from = int.Parse(parts[0]);
                to = parts[1].Length > 0 ? int.Parse(parts[1]) : to;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
