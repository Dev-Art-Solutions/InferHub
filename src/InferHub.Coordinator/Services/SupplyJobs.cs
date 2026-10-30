using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>One fetch or import, as <c>GET /api/admin/supply</c> shows it. In memory: a restart forgets jobs, never models.</summary>
public sealed class SupplyJob
{
    private long done;

    public required string Id { get; init; }

    /// <summary><c>fetch</c> or <c>import</c>.</summary>
    public required string Action { get; init; }

    public required string Name { get; init; }

    public string State { get; set; } = "running";

    public long TotalBytes { get; set; }

    public long DoneBytes => Interlocked.Read(ref done);

    public string? Error { get; set; }

    public List<string> Supplied { get; } = [];

    public List<string> Notes { get; } = [];

    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? FinishedUtc { get; set; }

    internal void Add(long bytes) => Interlocked.Add(ref done, bytes);
}

/// <summary>What <c>POST /api/admin/supply</c> asks for: one Ollama model, or one Hugging Face repo at a revision.</summary>
public sealed record SupplyFetchRequest(string? Ollama, string? HuggingFace, string? Revision, string[]? Include);

/// <summary>What <c>POST /api/admin/supply/import</c> asks for: a path under <c>Relay:Supply:ImportDirectory</c>.</summary>
public sealed record SupplyImportRequest(string? Path, string? Name, string? Revision);

/// <summary>
/// Phase 124. Fills the supply (D2, D5): a fetch from the relay's upstreams — the mirror they name, if any — or an import
/// from a directory a disk carried in. Every byte is hashed on its way to <c>partial/</c>; a blob is moved in only when
/// it is what the upstream or the source named, and a record is written only when every blob is there.
/// </summary>
public sealed class SupplyJobs : IDisposable
{
    private const int KeptJobs = 50;

    private static readonly string[] OllamaRegistryHosts = ["registry.ollama.ai", "ollama.com"];

    private static readonly string[] ManifestTypes =
    [
        RelaySupply.OllamaManifestType,
        "application/vnd.oci.image.manifest.v1+json"
    ];

    private readonly RelaySupply supply;
    private readonly IOptionsMonitor<RelayOptions> options;
    private readonly IHttpClientFactory factory;
    private readonly ILogger<SupplyJobs> logger;
    private readonly ConcurrentDictionary<string, SupplyJob> jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> busy = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource stopping = new();

    public SupplyJobs(RelaySupply supply, IOptionsMonitor<RelayOptions> options, IHttpClientFactory factory, ILogger<SupplyJobs> logger)
    {
        this.supply = supply;
        this.options = options;
        this.factory = factory;
        this.logger = logger;
    }

    public IReadOnlyList<SupplyJob> Jobs() => jobs.Values.OrderByDescending(job => job.StartedUtc).ToList();

    public int Running => jobs.Values.Count(job => job.State == "running");

    public SupplyJob? Get(string id) => jobs.GetValueOrDefault(id);

    /// <summary>Starts a fetch; throws <see cref="ArgumentException"/> for a request that names nothing fetchable.</summary>
    public SupplyJob Fetch(SupplyFetchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Ollama) == !string.IsNullOrWhiteSpace(request.HuggingFace))
        {
            throw new ArgumentException("name one model: \"ollama\": \"qwen3:8b\", or \"huggingFace\": \"owner/repo\" (with \"revision\" and \"include\")");
        }

        if (request.Ollama is { } ollama)
        {
            var (ns, repo, tag) = ParseOllama(ollama);
            var name = $"{ns}/{repo}:{tag}";
            return Start("fetch", name, (job, token) => FetchOllamaAsync(ns, repo, tag, job, token));
        }

        var repoId = (request.HuggingFace ?? string.Empty).Trim().Trim('/');

        if (repoId.StartsWith("https://huggingface.co/", StringComparison.OrdinalIgnoreCase))
        {
            repoId = repoId["https://huggingface.co/".Length..];
        }
        else if (repoId.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase))
        {
            repoId = repoId["hf.co/".Length..];
        }

        if (repoId.Split('/') is not [{ Length: > 0 }, { Length: > 0 }])
        {
            throw new ArgumentException($"'{request.HuggingFace}' is not a Hugging Face repo; name it owner/repo");
        }

        var revision = string.IsNullOrWhiteSpace(request.Revision) ? "main" : request.Revision.Trim();
        var include = request.Include?.Where(pattern => !string.IsNullOrWhiteSpace(pattern)).ToArray() ?? [];
        return Start("fetch", $"{repoId}@{revision}", (job, token) => FetchHuggingFaceAsync(repoId, revision, include, job, token));
    }

    /// <summary>Starts an import from under <c>ImportDirectory</c>; a path that leaves it is an <see cref="ArgumentException"/>.</summary>
    public SupplyJob Import(SupplyImportRequest request)
    {
        var source = ImportPath(supply.Options.ImportDirectory, request.Path);

        if (!Directory.Exists(source))
        {
            throw new ArgumentException($"'{request.Path}' is not a directory under Relay:Supply:ImportDirectory ({Path.GetFullPath(supply.Options.ImportDirectory)})");
        }

        return Start("import", request.Path ?? ".", (job, token) => ImportAsync(source, request.Name, request.Revision, job, token));
    }

    public void Dispose()
    {
        stopping.Cancel();
        stopping.Dispose();
    }

    /// <summary>
    /// The path the request named, under the import root and nowhere else (D5): an admin key is not a shell on the hub
    /// host, so <c>..</c> or an absolute path lands on null.
    /// </summary>
    internal static string? ImportPath(string importDirectory, string? relative)
    {
        var root = Path.GetFullPath(importDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var asked = (relative ?? string.Empty).Trim();

        if (Path.IsPathRooted(asked) || asked.Contains(':'))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(root, asked)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison) ? full : null;
    }

    /// <summary><c>qwen3:8b</c> → (<c>library</c>, <c>qwen3</c>, <c>8b</c>); <c>owner/model</c> keeps its owner; another registry is refused.</summary>
    internal static (string Namespace, string Model, string Tag) ParseOllama(string model)
    {
        var parts = (model ?? string.Empty).Trim().Split('/');

        if (parts.Length == 3)
        {
            if (!parts[0].Equals("registry.ollama.ai", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"'{model}' is pulled from {parts[0]}; the supply holds models from the Ollama registry (Relay:OllamaRegistry)");
            }

            parts = parts[1..];
        }

        if (parts.Length is < 1 or > 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"'{model}' is not an Ollama model name");
        }

        var (repo, tag) = parts[^1].Split(':', 2) is [var r, var t] ? (r, t) : (parts[^1], "latest");

        if (tag.EndsWith("cloud", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{model}' is an Ollama cloud model; it runs on ollama.com and there are no weights to supply");
        }

        return ((parts.Length == 2 ? parts[0] : "library").ToLowerInvariant(), repo.ToLowerInvariant(), tag);
    }

    /// <summary>A glob over a repo path: <c>*</c> within a segment, <c>**</c> across them, <c>?</c> one character.</summary>
    internal static bool Matches(string path, IReadOnlyCollection<string> include)
    {
        if (include.Count == 0)
        {
            return true;
        }

        foreach (var pattern in include)
        {
            var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern.Trim())
                .Replace(@"\*\*", "\u0001", StringComparison.Ordinal)
                .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
                .Replace(@"\?", "[^/]", StringComparison.Ordinal)
                .Replace("\u0001", ".*", StringComparison.Ordinal) + "$";

            // A pattern without a slash matches the file name anywhere, as people write "*.gguf".
            var subject = pattern.Contains('/') ? path : path[(path.LastIndexOf('/') + 1)..];

            if (System.Text.RegularExpressions.Regex.IsMatch(subject, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private SupplyJob Start(string action, string name, Func<SupplyJob, CancellationToken, Task> work)
    {
        if (!busy.TryAdd(name, name))
        {
            throw new InvalidOperationException($"'{name}' is already being supplied; see GET /api/admin/supply");
        }

        var job = new SupplyJob { Id = Guid.NewGuid().ToString("N")[..12], Action = action, Name = name };
        jobs[job.Id] = job;
        var token = stopping.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await work(job, token);
                job.State = "done";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                job.State = "failed";
                job.Error = ex is OperationCanceledException ? "the hub stopped before it finished" : ex.Message;
                logger.LogWarning("Relay supply: {Action} '{Name}' failed: {Reason}", action, name, job.Error);
            }
            finally
            {
                job.FinishedUtc = DateTimeOffset.UtcNow;
                busy.TryRemove(name, out _);

                foreach (var old in jobs.Values.Where(j => j.State != "running").OrderByDescending(j => j.StartedUtc).Skip(KeptJobs).ToList())
                {
                    jobs.TryRemove(old.Id, out _);
                }
            }
        }, CancellationToken.None);

        return job;
    }

    private async Task FetchOllamaAsync(string ns, string repo, string tag, SupplyJob job, CancellationToken cancellationToken)
    {
        var relay = options.CurrentValue;
        var client = factory.CreateClient(NodeRelayEndpoints.HttpClientName);
        var manifestUrl = Upstream(relay.OllamaRegistry, $"v2/{ns}/{repo}/manifests/{tag}");
        var accept = ManifestTypes.Select(type => ("Accept", new[] { type })).ToArray();

        using var response = await GetAsync(client, manifestUrl, accept, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"{relay.OllamaRegistry} has no '{ns}/{repo}:{tag}'");
        }

        await EnsureAsync(response, relay.OllamaRegistry);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var manifestSha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        // D2: the manifest is checked too, where the registry named it.
        if (response.Headers.TryGetValues("Docker-Content-Digest", out var named)
            && RelayCache.Sha256Of(named.FirstOrDefault()) is { } digest && digest != manifestSha)
        {
            throw new InvalidOperationException($"the manifest of '{ns}/{repo}:{tag}' hashes to {manifestSha[..12]}…, the registry named {digest[..12]}…");
        }

        var (mediaType, layers) = ReadManifest(bytes);
        job.TotalBytes = layers.Sum(layer => layer.Size);
        supply.EnsureRoomFor(layers.Where(layer => !supply.HasBlob(layer.Sha256)).Sum(layer => layer.Size));

        foreach (var layer in layers)
        {
            if (supply.HasBlob(layer.Sha256))
            {
                job.Add(layer.Size);
                continue;
            }

            var url = Upstream(relay.OllamaRegistry, $"v2/{ns}/{repo}/blobs/sha256:{layer.Sha256}");
            using var blob = await GetAsync(client, url, [], cancellationToken);
            await EnsureAsync(blob, relay.OllamaRegistry);
            await KeepCheckedAsync(blob.Content, layer.Path, layer.Sha256, null, layer.Size, job, cancellationToken);
        }

        var manifest = supply.KeepBytes(bytes);
        var name = $"{ns}/{repo}:{tag}";
        supply.Save(new SuppliedModel(
            RelaySupply.IdFor(SuppliedModel.Ollama, name), SuppliedModel.Ollama, name, tag, null,
            manifest, response.Content.Headers.ContentType?.MediaType ?? mediaType, null,
            layers, layers.Sum(layer => layer.Size), "fetched", relay.OllamaRegistry, DateTimeOffset.UtcNow));
        job.Supplied.Add(name);
    }

    private async Task FetchHuggingFaceAsync(string repoId, string revision, string[] include, SupplyJob job, CancellationToken cancellationToken)
    {
        var relay = options.CurrentValue;
        var client = factory.CreateClient(NodeRelayEndpoints.HttpClientName);
        var auth = string.IsNullOrWhiteSpace(supply.Options.HuggingFaceToken)
            ? Array.Empty<(string, string[])>()
            : [("Authorization", ["Bearer " + supply.Options.HuggingFaceToken!.Trim()])];

        string commit;

        using (var response = await GetAsync(client, Upstream(relay.HuggingFace, $"api/models/{repoId}/revision/{Uri.EscapeDataString(revision)}"), auth, cancellationToken))
        {
            await EnsureAsync(response, relay.HuggingFace, repoId);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            commit = document.RootElement.TryGetProperty("sha", out var sha) && sha.GetString() is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"{relay.HuggingFace} did not say which commit '{repoId}@{revision}' is");
        }

        // (path, size, sha256 of an LFS file, git oid of any file)
        var tree = new List<(string Path, long Size, string? Sha256, string? Oid)>();
        string? next = $"api/models/{repoId}/tree/{commit}?recursive=true";

        while (next is not null)
        {
            var url = next.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? Upstream(relay.HuggingFace, new Uri(next).PathAndQuery.TrimStart('/'))
                : Upstream(relay.HuggingFace, next);

            using var response = await GetAsync(client, url, auth, cancellationToken);
            await EnsureAsync(response, relay.HuggingFace, repoId);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("type", out var type) && type.GetString() == "file"
                    && entry.TryGetProperty("path", out var path) && path.GetString() is { } value)
                {
                    var size = entry.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                    var oid = entry.TryGetProperty("oid", out var o) ? o.GetString() : null;
                    string? sha = null;

                    if (entry.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object)
                    {
                        sha = lfs.TryGetProperty("oid", out var lfsOid) ? RelayCache.Sha256Of(lfsOid.GetString()) : null;
                        size = lfs.TryGetProperty("size", out var big) && big.TryGetInt64(out var b) ? b : size;
                    }

                    tree.Add((value, size, sha, oid));
                }
            }

            next = NextPage(response);
        }

        var wanted = tree.Where(file => Matches(file.Path, include)).ToList();

        if (wanted.Count == 0)
        {
            throw new InvalidOperationException(include.Length == 0
                ? $"'{repoId}@{revision}' has no files"
                : $"no file of '{repoId}@{revision}' matches {string.Join(", ", include)}; it has {string.Join(", ", tree.Take(8).Select(f => f.Path))}{(tree.Count > 8 ? ", …" : string.Empty)}");
        }

        job.TotalBytes = wanted.Sum(file => file.Size);
        supply.EnsureRoomFor(wanted.Where(file => file.Sha256 is null || !supply.HasBlob(file.Sha256)).Sum(file => file.Size));

        // The model info 118's estimate reads, replayed as it was (D3). Best effort: a mirror may not serve it.
        string? info = null;

        using (var response = await GetAsync(client, Upstream(relay.HuggingFace,
                   $"api/models/{repoId}/revision/{commit}?expand[]=pipeline_tag&expand[]=safetensors&expand[]=gguf"), auth, cancellationToken))
        {
            if (response.IsSuccessStatusCode)
            {
                info = supply.KeepBytes(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            }
            else
            {
                job.Notes.Add($"no model info ({(int)response.StatusCode}); a node's estimate of it reads the GGUF header alone");
            }
        }

        var files = new List<SuppliedFile>();

        foreach (var file in wanted)
        {
            if (file.Sha256 is { } known && supply.HasBlob(known))
            {
                files.Add(new SuppliedFile(file.Path, file.Size, known));
                job.Add(file.Size);
                continue;
            }

            var path = string.Join('/', file.Path.Split('/').Select(Uri.EscapeDataString));
            using var response = await GetAsync(client, Upstream(relay.HuggingFace, $"{repoId}/resolve/{commit}/{path}"), auth, cancellationToken);
            await EnsureAsync(response, relay.HuggingFace, repoId);
            var sha256 = await KeepCheckedAsync(response.Content, file.Path, file.Sha256, file.Sha256 is null ? file.Oid : null, file.Size, job, cancellationToken);
            files.Add(new SuppliedFile(file.Path, file.Size, sha256));
        }

        supply.Save(new SuppliedModel(
            RelaySupply.IdFor(SuppliedModel.HuggingFace, repoId, revision), SuppliedModel.HuggingFace, repoId, revision, commit,
            null, null, info, files, files.Sum(file => file.Size), "fetched", relay.HuggingFace, DateTimeOffset.UtcNow));
        job.Supplied.Add($"{repoId}@{revision}");
    }

    private async Task ImportAsync(string source, string? name, string? revision, SupplyJob job, CancellationToken cancellationToken)
    {
        var from = Path.GetRelativePath(Path.GetFullPath(supply.Options.ImportDirectory), source).Replace('\\', '/');

        // ~/.ollama itself, as people copy it
        if (Directory.Exists(Path.Combine(source, "models", "manifests")) || Directory.Exists(Path.Combine(source, "models", "manifests-v2")))
        {
            source = Path.Combine(source, "models");
        }

        if (Directory.Exists(Path.Combine(source, "models")) && Directory.Exists(Path.Combine(source, "blobs"))
            && Directory.EnumerateFiles(Path.Combine(source, "models"), "*.json").Any())
        {
            await ImportSupplyAsync(source, from, name, job, cancellationToken);
        }
        else if ((Directory.Exists(Path.Combine(source, "manifests")) || Directory.Exists(Path.Combine(source, "manifests-v2")))
                 && Directory.Exists(Path.Combine(source, "blobs")))
        {
            await ImportOllamaAsync(source, from, name, job, cancellationToken);
        }
        else if (HuggingFaceRepos(source) is { Count: > 0 } repos)
        {
            await ImportHuggingFaceAsync(repos, from, name, revision, job, cancellationToken);
        }
        else
        {
            throw new InvalidOperationException(
                $"found nothing to import in '{from}': it holds no Ollama models directory (manifests/ or manifests-v2/, and blobs/), no Hugging Face cache (models--owner--repo/) and no supply directory (models/*.json and blobs/)");
        }

        if (job.Supplied.Count == 0)
        {
            throw new InvalidOperationException(name is null
                ? $"'{from}' holds no model that could be imported" + (job.Notes.Count > 0 ? ": " + string.Join("; ", job.Notes) : string.Empty)
                : $"'{from}' holds no '{name}'");
        }
    }

    /// <summary>
    /// An Ollama models directory: <c>manifests/&lt;host&gt;/&lt;namespace&gt;/&lt;model&gt;/&lt;tag&gt;</c> and <c>blobs/sha256-&lt;hex&gt;</c>.
    /// Ollama 0.40 writes a new pull under <c>manifests-v2/ollama.com/…</c> (found on this box, 2026-10-31), the same
    /// manifest under another name for the same registry; both are read, and where both hold a tag the first wins.
    /// </summary>
    private async Task ImportOllamaAsync(string source, string from, string? filter, SupplyJob job, CancellationToken cancellationToken)
    {
        var wanted = filter is null ? ((string, string, string)?)null : ParseOllama(filter);
        var found = new List<(string Name, string Tag, string File)>();

        var hosts = new[] { "manifests", "manifests-v2" }
            .Select(tree => Path.Combine(source, tree))
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories);

        foreach (var host in hosts)
        {
            foreach (var file in Directory.EnumerateFiles(host, "*", SearchOption.AllDirectories))
            {
                var parts = Path.GetRelativePath(host, file).Replace('\\', '/').Split('/');

                if (parts is not [var ns, var model, var tag])
                {
                    continue;
                }

                if (!OllamaRegistryHosts.Contains(Path.GetFileName(host), StringComparer.OrdinalIgnoreCase))
                {
                    job.Notes.Add($"skipped {Path.GetFileName(host)}/{ns}/{model}:{tag} — not from the Ollama registry");
                    continue;
                }

                if (wanted is { } w && !(w.Item1 == ns && w.Item2 == model && w.Item3 == tag))
                {
                    continue;
                }

                if (found.All(f => f.Name != $"{ns}/{model}:{tag}"))
                {
                    found.Add(($"{ns}/{model}:{tag}", tag, file));
                }
            }
        }

        var plans = new List<(string Name, string Tag, byte[] Bytes, string MediaType, List<SuppliedFile> Layers)>();

        foreach (var (name, tag, file) in found)
        {
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            var (mediaType, layers) = ReadManifest(bytes);
            plans.Add((name, tag, bytes, mediaType, layers));
        }

        job.TotalBytes = plans.SelectMany(plan => plan.Layers).DistinctBy(layer => layer.Sha256).Sum(layer => layer.Size);
        supply.EnsureRoomFor(plans.SelectMany(plan => plan.Layers).DistinctBy(layer => layer.Sha256).Where(layer => !supply.HasBlob(layer.Sha256)).Sum(layer => layer.Size));
        var counted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, tag, bytes, mediaType, layers) in plans)
        {
            var missing = layers.FirstOrDefault(layer => !supply.HasBlob(layer.Sha256) && !File.Exists(Path.Combine(source, "blobs", "sha256-" + layer.Sha256)));

            if (missing is not null)
            {
                job.Notes.Add($"skipped {name} — its blob {missing.Sha256[..12]}… is not in blobs/");
                continue;
            }

            foreach (var layer in layers)
            {
                if (supply.HasBlob(layer.Sha256))
                {
                    if (counted.Add(layer.Sha256))
                    {
                        job.Add(layer.Size);
                    }

                    continue;
                }

                await using var stream = File.OpenRead(Path.Combine(source, "blobs", "sha256-" + layer.Sha256));
                await KeepCheckedAsync(stream, layer.Path, layer.Sha256, null, layer.Size, job, cancellationToken);
                counted.Add(layer.Sha256);
            }

            supply.Save(new SuppliedModel(
                RelaySupply.IdFor(SuppliedModel.Ollama, name), SuppliedModel.Ollama, name, tag, null,
                supply.KeepBytes(bytes), mediaType, null, layers, layers.Sum(layer => layer.Size), "imported", from, DateTimeOffset.UtcNow));
            job.Supplied.Add(name);
        }
    }

    /// <summary>
    /// A Hugging Face cache, as <c>huggingface-cli download</c> leaves it: <c>models--owner--repo/refs/&lt;revision&gt;</c>
    /// names a commit, <c>snapshots/&lt;commit&gt;/</c> holds the files. Each file is hashed as it is copied; one whose
    /// cache blob is named by a sha256 (an LFS file) must match it.
    /// </summary>
    private async Task ImportHuggingFaceAsync(List<string> repos, string from, string? filter, string? revision, SupplyJob job, CancellationToken cancellationToken)
    {
        var plans = new List<(string Repo, string Revision, string Commit, string Snapshot, List<string> Files)>();

        foreach (var directory in repos)
        {
            var parts = Path.GetFileName(directory)["models--".Length..].Split("--", 2);

            if (parts is not [{ Length: > 0 } owner, { Length: > 0 } name])
            {
                job.Notes.Add($"skipped {Path.GetFileName(directory)} — a node pulls owner/repo, and this repo has no owner");
                continue;
            }

            var repoId = $"{owner}/{name}";

            if (filter is not null && !filter.Trim().Equals(repoId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var refs = Path.Combine(directory, "refs");
            var pinned = Directory.Exists(refs)
                ? Directory.EnumerateFiles(refs, "*", SearchOption.AllDirectories)
                    .Select(file => (Revision: Path.GetRelativePath(refs, file).Replace('\\', '/'), Commit: File.ReadAllText(file).Trim()))
                    .ToList()
                : [];

            // No refs (a copy that lost them): each snapshot is its own commit, asked for by that commit.
            if (pinned.Count == 0 && Directory.Exists(Path.Combine(directory, "snapshots")))
            {
                pinned = Directory.EnumerateDirectories(Path.Combine(directory, "snapshots"))
                    .Select(snapshot => (Path.GetFileName(snapshot), Path.GetFileName(snapshot)))
                    .ToList();
            }

            foreach (var (rev, commit) in pinned.Where(p => revision is null || p.Revision == revision || p.Commit == revision))
            {
                var snapshot = Path.Combine(directory, "snapshots", commit);

                if (!Directory.Exists(snapshot))
                {
                    job.Notes.Add($"skipped {repoId}@{rev} — snapshots/{commit} is not there");
                    continue;
                }

                var files = Directory.EnumerateFiles(snapshot, "*", SearchOption.AllDirectories).ToList();
                plans.Add((repoId, rev, commit, snapshot, files));
            }
        }

        job.TotalBytes = plans.SelectMany(plan => plan.Files).Sum(Length);
        supply.EnsureRoomFor(job.TotalBytes);

        foreach (var (repoId, rev, commit, snapshot, paths) in plans)
        {
            var files = new List<SuppliedFile>();

            foreach (var path in paths)
            {
                var relative = Path.GetRelativePath(snapshot, path).Replace('\\', '/');
                var size = Length(path);

                // The cache names an LFS file's blob by its sha256 and a small file's by its git oid; either is checked.
                var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.Name;
                var expected = RelayCache.Sha256Of(target);
                var oid = expected is null && target is { Length: 40 } && target.All(Uri.IsHexDigit) ? target : null;

                await using var stream = File.OpenRead(path);
                var sha256 = await KeepCheckedAsync(stream, relative, expected, oid, size, job, cancellationToken);
                files.Add(new SuppliedFile(relative, size, sha256));
            }

            supply.Save(new SuppliedModel(
                RelaySupply.IdFor(SuppliedModel.HuggingFace, repoId, rev), SuppliedModel.HuggingFace, repoId, rev, commit,
                null, null, null, files, files.Sum(file => file.Size), "imported", from, DateTimeOffset.UtcNow));
            job.Supplied.Add($"{repoId}@{rev}");
        }
    }

    /// <summary>Another hub's supply directory, carried across: its records, each blob re-hashed on the way in.</summary>
    private async Task ImportSupplyAsync(string source, string from, string? filter, SupplyJob job, CancellationToken cancellationToken)
    {
        var records = new List<SuppliedModel>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(source, "models"), "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<SuppliedModel>(await File.ReadAllTextAsync(file, cancellationToken), RelaySupply.Json) is { } model
                    && (filter is null || model.Name.Equals(filter.Trim(), StringComparison.OrdinalIgnoreCase) || model.Id == filter.Trim()))
                {
                    records.Add(model);
                }
            }
            catch (JsonException)
            {
                job.Notes.Add($"skipped {Path.GetFileName(file)} — not a supply record");
            }
        }

        job.TotalBytes = records.SelectMany(model => model.Files).DistinctBy(file => file.Sha256).Sum(file => file.Size);
        supply.EnsureRoomFor(job.TotalBytes);

        foreach (var model in records)
        {
            var blobs = model.Blobs().ToList();
            var missing = blobs.FirstOrDefault(sha => !supply.HasBlob(sha) && !File.Exists(Path.Combine(source, "blobs", sha)));

            if (missing is not null)
            {
                job.Notes.Add($"skipped {model.Name} — its blob {missing[..12]}… is not in blobs/");
                continue;
            }

            foreach (var sha in blobs.Where(sha => !supply.HasBlob(sha)))
            {
                var path = Path.Combine(source, "blobs", sha);
                await using var stream = File.OpenRead(path);
                await KeepCheckedAsync(stream, sha, sha, null, Length(path), job, cancellationToken);
            }

            supply.Save(model with { Source = "imported", From = from, SuppliedUtc = DateTimeOffset.UtcNow });
            job.Supplied.Add(model.Name);
        }
    }

    private static List<string> HuggingFaceRepos(string source)
    {
        var hub = Directory.Exists(Path.Combine(source, "hub")) ? Path.Combine(source, "hub") : source;

        return Path.GetFileName(source).StartsWith("models--", StringComparison.Ordinal)
            ? [source]
            : Directory.EnumerateDirectories(hub, "models--*").ToList();
    }

    /// <summary>
    /// Copies <paramref name="content"/> to <c>partial/</c>, hashing every byte, and moves it in only when its size and
    /// its sha256 (or, for a small Hugging Face file, its git oid) are what the upstream named (D2).
    /// </summary>
    private async Task<string> KeepCheckedAsync(object content, string label, string? sha256, string? gitOid, long size, SupplyJob job, CancellationToken cancellationToken)
    {
        var partial = supply.NewPartial();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var git = gitOid is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        git?.AppendData(Encoding.ASCII.GetBytes($"blob {size}\0"));
        long written = 0;

        try
        {
            await using (var source = content is HttpContent http ? await http.ReadAsStreamAsync(cancellationToken) : (Stream)content)
            await using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                int read;

                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                    git?.AppendData(buffer, 0, read);
                    written += read;
                    job.Add(read);
                }
            }

            if (written != size)
            {
                throw new InvalidOperationException($"'{label}' arrived with {written} bytes, {size} were named; nothing was supplied");
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());

            if (sha256 is not null && actual != sha256)
            {
                throw new InvalidOperationException($"'{label}' hashes to {actual[..12]}…, not the {sha256[..12]}… it was named by; nothing was supplied");
            }

            if (git is not null && Convert.ToHexStringLower(git.GetHashAndReset()) is var oid && !oid.Equals(gitOid, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"'{label}' has git oid {oid[..12]}…, not the {gitOid} it was named by; nothing was supplied");
            }

            supply.Keep(partial, actual);
            return actual;
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    private static (string MediaType, List<SuppliedFile> Layers) ReadManifest(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var layers = new List<SuppliedFile>();

        void Add(JsonElement element)
        {
            if (element.TryGetProperty("digest", out var d) && RelayCache.Sha256Of(d.GetString()) is { } sha
                && element.TryGetProperty("size", out var s) && s.TryGetInt64(out var size))
            {
                var type = element.TryGetProperty("mediaType", out var m) ? m.GetString() : null;
                layers.Add(new SuppliedFile(type ?? "blob", size, sha, type));
            }
            else
            {
                throw new InvalidOperationException("the manifest names a layer without a sha256 digest and a size");
            }
        }

        if (root.TryGetProperty("config", out var config))
        {
            Add(config);
        }

        if (root.TryGetProperty("layers", out var all))
        {
            foreach (var layer in all.EnumerateArray())
            {
                Add(layer);
            }
        }

        if (layers.Count == 0)
        {
            throw new InvalidOperationException("the manifest names no layers");
        }

        return (root.TryGetProperty("mediaType", out var mediaType) && mediaType.GetString() is { } value ? value : RelaySupply.OllamaManifestType,
            layers.DistinctBy(layer => layer.Sha256).ToList());
    }

    private static Uri Upstream(string configured, string relative)
        => new(new Uri(configured.TrimEnd('/') + "/"), relative);

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, Uri url, (string Name, string[] Values)[] headers, CancellationToken cancellationToken)
    {
        var first = await client.SendAsync(NodeRelayEndpoints.Request(HttpMethod.Get, url, headers, url), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return await NodeRelayEndpoints.FollowAsync(client, first, HttpMethod.Get, url, headers, cancellationToken);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, string upstream, string? repo = null)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        body = body.Length > 200 ? body[..200] + "…" : body;

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when repo is not null => new InvalidOperationException(
                $"{upstream} refused '{repo}' ({(int)response.StatusCode}): it is gated or private; give the hub a token in Relay:Supply:HuggingFaceToken"),
            HttpStatusCode.NotFound when repo is not null => new InvalidOperationException($"{upstream} has no '{repo}' at that revision (404)"),
            _ => new InvalidOperationException($"{upstream} answered {(int)response.StatusCode}: {body}")
        };
    }

    private static string? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var links))
        {
            return null;
        }

        foreach (var link in links.SelectMany(l => l.Split(',')))
        {
            if (link.Contains("rel=\"next\"", StringComparison.Ordinal))
            {
                var start = link.IndexOf('<');
                var end = link.IndexOf('>');
                return start >= 0 && end > start ? link[(start + 1)..end] : null;
            }
        }

        return null;
    }

    private static long Length(string path)
    {
        var info = new FileInfo(path);
        return info.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target ? target.Length : info.Length;
    }
}
