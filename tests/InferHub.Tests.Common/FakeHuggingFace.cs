using System.Collections.Concurrent;
using System.Security.Cryptography;
using InferHub.Node.Backends.HuggingFace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace InferHub.Tests;

/// <summary>
/// Phase 98. A socket that answers the three calls the store makes the way huggingface.co does —
/// <c>/api/models/{repo}/revision/{rev}</c>, <c>/api/models/{repo}/tree/{commit}</c> and
/// <c>/{repo}/resolve/{commit}/{path}</c> with <c>Range</c> — so resuming and verifying cross real
/// HTTP. The real Hub is in the release notes.
/// </summary>
internal sealed class FakeHuggingFace : IAsyncDisposable
{
    public const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private readonly ConcurrentDictionary<string, Dictionary<string, byte[]>> repos = new(StringComparer.OrdinalIgnoreCase);
    private WebApplication app = null!;

    public string Url { get; private set; } = null!;

    /// <summary>Repos that answer 401, as a gated one does without a token.</summary>
    public HashSet<string> Gated { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files whose listed sha256 is deliberately wrong.</summary>
    public HashSet<string> Corrupt { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<string> Downloads { get; } = new();

    public ConcurrentQueue<string> Ranges { get; } = new();

    public string? LastAuthorization { get; private set; }

    public void Repo(string repo, params (string Path, byte[] Bytes)[] files)
        => repos[repo] = files.ToDictionary(f => f.Path, f => f.Bytes);

    public static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static async Task<FakeHuggingFace> StartAsync()
    {
        var fake = new FakeHuggingFace();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        fake.app = builder.Build();

        fake.app.MapGet("/api/models/{owner}/{repo}/revision/{rev}", (string owner, string repo, HttpContext context) =>
            fake.Find(owner, repo, context) is null ? fake.Refuse(owner, repo) : Results.Json(new { id = $"{owner}/{repo}", sha = Commit }));

        fake.app.MapGet("/api/models/{owner}/{repo}/tree/{commit}", (string owner, string repo, HttpContext context) =>
        {
            if (fake.Find(owner, repo, context) is not { } files)
            {
                return fake.Refuse(owner, repo);
            }

            // A directory entry first, as the real tree has: the store must take files only.
            var entries = new List<object> { new { type = "directory", path = "sub", oid = "x" } };

            entries.AddRange(files.Select(f => (object)new
            {
                type = "file",
                path = f.Key,
                size = f.Value.Length,
                lfs = f.Key.EndsWith(".gguf") || f.Key.EndsWith(".safetensors")
                    ? new { oid = fake.Corrupt.Contains(f.Key) ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(f.Value)), size = f.Value.Length }
                    : null
            }));

            return Results.Json(entries);
        });

        fake.app.MapGet("/{owner}/{repo}/resolve/{commit}/{**path}", async (string owner, string repo, string path, HttpContext context) =>
        {
            if (fake.Find(owner, repo, context) is not { } files || !files.TryGetValue(Uri.UnescapeDataString(path), out var bytes))
            {
                context.Response.StatusCode = 404;
                return;
            }

            fake.Downloads.Enqueue(path);
            var from = 0L;

            if (context.Request.Headers.Range.ToString() is { Length: > 0 } range && range.StartsWith("bytes="))
            {
                fake.Ranges.Enqueue(range);
                from = long.Parse(range["bytes=".Length..].Split('-')[0]);
                context.Response.StatusCode = 206;
            }

            context.Response.ContentLength = bytes.Length - from;
            await context.Response.Body.WriteAsync(bytes.AsMemory((int)from));
        });

        await fake.app.StartAsync();
        fake.Url = fake.app.Urls.First();
        return fake;
    }

    public HuggingFaceClient Client(string? token = null)
        => new(new HttpClient(), new Uri(Url + "/"), token);

    private Dictionary<string, byte[]>? Find(string owner, string repo, HttpContext context)
    {
        LastAuthorization = context.Request.Headers.Authorization.ToString();
        var id = $"{owner}/{repo}";

        if (Gated.Contains(id) && string.IsNullOrEmpty(LastAuthorization))
        {
            return null;
        }

        return repos.GetValueOrDefault(id);
    }

    private IResult Refuse(string owner, string repo)
        => Gated.Contains($"{owner}/{repo}") ? Results.StatusCode(401) : Results.NotFound();

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

/// <summary>A converter that writes what <c>coli convert</c> writes, or fails as it does.</summary>
internal sealed class FakeConverter : IColibriConverter
{
    public ConcurrentQueue<(string Repo, string Directory)> Calls { get; } = new();

    public bool Fail { get; set; }

    /// <summary>When set, the conversion waits on it after its first line — a long convert, held open.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public async IAsyncEnumerable<string> ConvertAsync(string repoId, string outputDirectory, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Calls.Enqueue((repoId, outputDirectory));
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "model-00000.safetensors"), "half", cancellationToken);
        yield return "checkpoint: OLMoE -> tools/convert_olmoe_merged.py";

        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        if (Fail)
        {
            throw new InvalidOperationException("coli convert exited with code 1: unsupported checkpoint family");
        }

        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "config.json"), "{}", cancellationToken);
        yield return "done";
    }
}
