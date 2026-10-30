using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Phase 124. <c>Relay:Supply:</c> — models this hub fetched and checked once, or imported from a disk, that the relay
/// answers a sealed node from without asking the upstream (124 D1). Off by default; rule 4's seventh exception (D6).
/// </summary>
public sealed class RelaySupplyOptions
{
    public bool Enabled { get; set; }

    /// <summary>Relative by default so bare metal and Windows work; the image points it at <c>/data/supply</c>.</summary>
    public string DataDirectory { get; set; } = "./data/supply";

    /// <summary>The relay answers only from the supply: anything else is a 404 naming this key, never a fetch (D4).</summary>
    public bool Only { get; set; }

    /// <summary>The only place an import reads from (D5); the request names a path under it. The image says <c>/import</c>.</summary>
    public string ImportDirectory { get; set; } = "./data/import";

    /// <summary>The hub's own Hugging Face token, for a gated or private repo it supplies. Env or user-secrets, never the file.</summary>
    public string? HuggingFaceToken { get; set; }

    /// <summary>A fetch or an import that would leave less than this free is refused before its first byte.</summary>
    public double KeepFreeGiB { get; set; } = 20;
}

/// <summary>One file of a supplied model, by the sha256 it was checked against (D2).</summary>
public sealed record SuppliedFile(string Path, long Size, string Sha256, string? MediaType = null);

/// <summary>
/// A supplied model as the upstream answered on the day it was fetched (D3): an Ollama manifest and its blobs, or a
/// Hugging Face repo at one commit with its tree and its model info. Written as <c>models/&lt;id&gt;.json</c>.
/// </summary>
public sealed record SuppliedModel(
    string Id,
    string Kind,
    string Name,
    string? Revision,
    string? Commit,
    string? Manifest,
    string? ManifestMediaType,
    string? Info,
    IReadOnlyList<SuppliedFile> Files,
    long Bytes,
    string Source,
    string From,
    DateTimeOffset SuppliedUtc)
{
    public const string Ollama = "ollama";

    public const string HuggingFace = "hf";

    /// <summary>Every blob this record needs: its files, and the manifest and the model info it replays.</summary>
    public IEnumerable<string> Blobs()
        => Files.Select(file => file.Sha256)
            .Concat(new[] { Manifest, Info }.OfType<string>())
            .Distinct(StringComparer.Ordinal);
}

/// <summary>What the supply holds and has done since the hub started. Counts, never names (rule 7's habit).</summary>
public sealed record RelaySupplySnapshot(
    int Models,
    long Bytes,
    bool Only,
    long Served,
    long ServedBytes,
    long Refused,
    int Running);

/// <summary>An answer the relay gives from the supply: a blob on disk (ranges honoured), or a small JSON body.</summary>
public sealed record SupplyAnswer(string? Sha256, byte[]? Body, string ContentType, IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>
/// Phase 124. The supply's records and blobs: <c>blobs/&lt;sha256&gt;</c> (written to <c>partial/</c> first, moved in only
/// after the hash matched), <c>models/&lt;id&gt;.json</c>. Read once on first use; a record whose blobs are not all there
/// is not supplied (D2 — a model is whole or it is not).
/// </summary>
public sealed class RelaySupply
{
    private const double GiB = 1024d * 1024 * 1024;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IOptionsMonitor<RelayOptions> options;
    private readonly ILogger<RelaySupply> logger;
    private readonly object gate = new();
    private readonly Dictionary<string, SuppliedModel> models = new(StringComparer.Ordinal);
    private string? root;
    private long served, servedBytes, refused;

    public RelaySupply(IOptionsMonitor<RelayOptions> options, ILogger<RelaySupply> logger)
    {
        this.options = options;
        this.logger = logger;
    }

    public bool Enabled => options.CurrentValue.Supply.Enabled;

    public bool Only => Enabled && options.CurrentValue.Supply.Only;

    public RelaySupplyOptions Options => options.CurrentValue.Supply;

    public IReadOnlyList<SuppliedModel> Models()
    {
        Root();

        lock (gate)
        {
            return models.Values.OrderBy(model => model.Kind).ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public SuppliedModel? Get(string id)
    {
        Root();

        lock (gate)
        {
            return models.GetValueOrDefault(id);
        }
    }

    public void Served(long bytes)
    {
        Interlocked.Increment(ref served);
        Interlocked.Add(ref servedBytes, bytes);
    }

    public void Refused() => Interlocked.Increment(ref refused);

    public RelaySupplySnapshot? Snapshot(int running)
    {
        if (!Enabled)
        {
            return null;
        }

        var all = Models();
        return new RelaySupplySnapshot(
            all.Count,
            BytesOf(all),
            Only,
            Interlocked.Read(ref served),
            Interlocked.Read(ref servedBytes),
            Interlocked.Read(ref refused),
            running);
    }

    public bool HasBlob(string sha256) => File.Exists(BlobPath(sha256));

    public string BlobPath(string sha256) => Path.Combine(Root(), "blobs", sha256);

    public FileStream? OpenBlob(string sha256)
    {
        try
        {
            return new FileStream(BlobPath(sha256), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A fresh file under <c>partial/</c>; it becomes a blob only through <see cref="Keep"/>.</summary>
    public string NewPartial() => Path.Combine(Root(), "partial", Guid.NewGuid().ToString("N"));

    /// <summary>Moves a checked file in under its sha256. A blob already there is the same bytes, so the copy is dropped.</summary>
    public void Keep(string partial, string sha256)
    {
        var path = BlobPath(sha256);

        if (File.Exists(path))
        {
            File.Delete(partial);
            return;
        }

        File.Move(partial, path);
    }

    /// <summary>Small bytes (a manifest, a model info) kept by their own sha256.</summary>
    public string KeepBytes(byte[] bytes)
    {
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

        if (!HasBlob(sha256))
        {
            var partial = NewPartial();
            File.WriteAllBytes(partial, bytes);
            Keep(partial, sha256);
        }

        return sha256;
    }

    /// <summary>Writes the record (replacing one with the same id) and deletes the blobs only the old one needed.</summary>
    public void Save(SuppliedModel model)
    {
        var directory = Root();
        var missing = model.Blobs().Where(sha => !HasBlob(sha)).ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"'{model.Name}' is missing {missing.Count} of its blobs; a model is supplied whole or not at all");
        }

        var path = Path.Combine(directory, "models", model.Id + ".json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(model, Json));
        File.Move(temporary, path, overwrite: true);

        SuppliedModel? previous;

        lock (gate)
        {
            models.TryGetValue(model.Id, out previous);
            models[model.Id] = model;
        }

        if (previous is not null)
        {
            DeleteOrphans(previous);
        }

        logger.LogInformation(
            "Relay supply: {Kind} '{Name}' supplied ({Source}), {Files} files, {Bytes} bytes",
            model.Kind, model.Name, model.Source, model.Files.Count, model.Bytes);
    }

    public bool Delete(string id)
    {
        var directory = Root();
        SuppliedModel? model;

        lock (gate)
        {
            if (!models.Remove(id, out model))
            {
                return false;
            }
        }

        File.Delete(Path.Combine(directory, "models", id + ".json"));
        DeleteOrphans(model);
        logger.LogInformation("Relay supply: {Kind} '{Name}' removed", model.Kind, model.Name);
        return true;
    }

    /// <summary>A free-disk check before the first byte: a fetch that would fill the hub's disk is refused, not half done.</summary>
    public void EnsureRoomFor(long bytes)
    {
        var directory = Root();

        try
        {
            var free = new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace;

            if (free - bytes < Options.KeepFreeGiB * GiB)
            {
                throw new InvalidOperationException(
                    $"{bytes / GiB:0.##} GiB would leave less than Relay:Supply:KeepFreeGiB ({Options.KeepFreeGiB}) free on {Path.GetPathRoot(directory)}");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// What the relay answers from the supply for <c>{upstream}/{path}</c>, or null (D3). Only the paths a node asks:
    /// an Ollama manifest and blob; a Hugging Face revision, model info, tree and resolve.
    /// </summary>
    public SupplyAnswer? Find(string upstream, string? path)
    {
        if (!Enabled)
        {
            return null;
        }

        var segments = (path ?? string.Empty).Trim('/').Split('/');
        var all = Models();

        if (upstream == NodeRelay.OllamaRegistry)
        {
            return FindOllama(all.Where(model => model.Kind == SuppliedModel.Ollama), segments);
        }

        if (upstream == NodeRelay.HuggingFace)
        {
            return FindHuggingFace(all.Where(model => model.Kind == SuppliedModel.HuggingFace), segments);
        }

        return null;
    }

    private SupplyAnswer? FindOllama(IEnumerable<SuppliedModel> candidates, string[] segments)
    {
        // v2/{namespace}/{model}/manifests/{tag|digest}, v2/{namespace}/{model}/blobs/sha256:{hex}
        if (segments is not ["v2", var ns, var name, var kind, var reference])
        {
            return null;
        }

        var repo = $"{ns}/{name}";
        var mine = candidates.Where(model => model.Name.StartsWith(repo + ":", StringComparison.OrdinalIgnoreCase)).ToList();

        if (kind == "manifests")
        {
            var digest = RelayCache.Sha256Of(reference);
            var model = mine.FirstOrDefault(m => m.Name.Equals($"{repo}:{reference}", StringComparison.OrdinalIgnoreCase))
                ?? mine.FirstOrDefault(m => digest is not null && m.Manifest == digest);

            return model?.Manifest is { } manifest
                ? new SupplyAnswer(manifest, null, model.ManifestMediaType ?? OllamaManifestType, [new("Docker-Content-Digest", "sha256:" + manifest)])
                : null;
        }

        if (kind == "blobs" && RelayCache.Sha256Of(reference) is { } sha256
            && mine.Any(model => model.Files.Any(file => file.Sha256 == sha256)))
        {
            return new SupplyAnswer(sha256, null, "application/octet-stream", [new("Docker-Content-Digest", "sha256:" + sha256)]);
        }

        return null;
    }

    private SupplyAnswer? FindHuggingFace(IEnumerable<SuppliedModel> candidates, string[] segments)
    {
        if (segments is ["api", "models", var owner, var name, .. var rest])
        {
            var mine = Repo(candidates, owner, name);

            switch (rest)
            {
                // The model info (118's estimate), and the revision a pull pins to (98 D2): one replayed document.
                case []:
                    return Info(mine.FirstOrDefault(m => m.Revision == "main") ?? mine.FirstOrDefault());
                case ["revision", var revision]:
                    return Info(At(mine, revision));
                case ["tree", var revision, .. var under]:
                    return Tree(At(mine, revision), string.Join('/', under));
                default:
                    return null;
            }
        }

        if (segments is [var o, var n, "resolve", var at, .. var file] && file.Length > 0)
        {
            var model = At(Repo(candidates, o, n), at);
            var wanted = string.Join('/', file.Select(Uri.UnescapeDataString));

            if (model?.Files.FirstOrDefault(f => f.Path == wanted) is not { } found)
            {
                return null;
            }

            return new SupplyAnswer(found.Sha256, null, "application/octet-stream",
            [
                new("X-Repo-Commit", model.Commit ?? string.Empty),
                new("X-Linked-Etag", $"\"{found.Sha256}\""),
                new("X-Linked-Size", found.Size.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ]);
        }

        return null;
    }

    private static List<SuppliedModel> Repo(IEnumerable<SuppliedModel> candidates, string owner, string name)
        => candidates.Where(model => model.Name.Equals($"{owner}/{name}", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(model => model.SuppliedUtc)
            .ToList();

    /// <summary>A revision the record was fetched at, by name or by the commit it pinned (D3 — a branch does not move here).</summary>
    private static SuppliedModel? At(IEnumerable<SuppliedModel> mine, string revision)
    {
        var wanted = Uri.UnescapeDataString(revision);
        return mine.FirstOrDefault(model => string.Equals(model.Revision, wanted, StringComparison.Ordinal)
            || string.Equals(model.Commit, wanted, StringComparison.OrdinalIgnoreCase));
    }

    private SupplyAnswer? Info(SuppliedModel? model)
    {
        if (model is null)
        {
            return null;
        }

        JsonObject info;

        try
        {
            info = model.Info is { } sha && HasBlob(sha) && JsonNode.Parse(File.ReadAllBytes(BlobPath(sha))) is JsonObject stored
                ? stored
                : new JsonObject();
        }
        catch (JsonException)
        {
            info = new JsonObject();
        }

        info["id"] = model.Name;
        info["sha"] = model.Commit;
        return new SupplyAnswer(null, Encoding.UTF8.GetBytes(info.ToJsonString()), "application/json", []);
    }

    /// <summary>
    /// The tree, every file with the sha256 it was checked against as its <c>lfs.oid</c> — so the node's own check (98)
    /// covers a small file too, which the real tree would have named only by its git oid.
    /// </summary>
    private static SupplyAnswer? Tree(SuppliedModel? model, string under)
    {
        if (model is null)
        {
            return null;
        }

        var prefix = under.Length == 0 ? string.Empty : Uri.UnescapeDataString(under).TrimEnd('/') + "/";
        var entries = new JsonArray();

        foreach (var file in model.Files.Where(file => file.Path.StartsWith(prefix, StringComparison.Ordinal)))
        {
            entries.Add(new JsonObject
            {
                ["type"] = "file",
                ["oid"] = file.Sha256,
                ["size"] = file.Size,
                ["path"] = file.Path,
                ["lfs"] = new JsonObject { ["oid"] = file.Sha256, ["size"] = file.Size }
            });
        }

        return new SupplyAnswer(null, Encoding.UTF8.GetBytes(entries.ToJsonString()), "application/json", []);
    }

    public const string OllamaManifestType = "application/vnd.docker.distribution.manifest.v2+json";

    /// <summary>A safe file name and URL segment for a model: <c>ollama-library-qwen3-8b</c>, <c>hf-org-repo-main</c>.</summary>
    public static string IdFor(string kind, string name, string? revision = null)
    {
        var text = $"{kind}-{name}" + (revision is null ? string.Empty : "-" + revision);
        var builder = new StringBuilder(text.Length);

        foreach (var c in text.ToLowerInvariant())
        {
            builder.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' ? c : '-');
        }

        return builder.ToString().Trim('-');
    }

    internal static long BytesOf(IEnumerable<SuppliedModel> all)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0;

        foreach (var file in all.SelectMany(model => model.Files))
        {
            if (seen.Add(file.Sha256))
            {
                bytes += file.Size;
            }
        }

        return bytes;
    }

    private void DeleteOrphans(SuppliedModel gone)
    {
        HashSet<string> needed;

        lock (gate)
        {
            needed = models.Values.SelectMany(model => model.Blobs()).ToHashSet(StringComparer.Ordinal);
        }

        foreach (var sha in gone.Blobs().Where(sha => !needed.Contains(sha)))
        {
            try
            {
                File.Delete(BlobPath(sha));
            }
            catch (IOException ex)
            {
                logger.LogWarning("Relay supply: could not delete blob {Digest}: {Reason}", sha[..12], ex.Message);
            }
        }
    }

    /// <summary>Resolved and read once, on first use: an enabled supply that is never asked creates nothing.</summary>
    private string Root()
    {
        if (root is { } known)
        {
            return known;
        }

        lock (gate)
        {
            if (root is { } raced)
            {
                return raced;
            }

            var directory = Path.GetFullPath(Options.DataDirectory);
            Directory.CreateDirectory(Path.Combine(directory, "blobs"));
            var records = Directory.CreateDirectory(Path.Combine(directory, "models"));
            var partial = Path.Combine(directory, "partial");

            // A fetch or an import the last hub was running when it stopped is not a blob; nothing could finish it.
            if (Directory.Exists(partial))
            {
                Directory.Delete(partial, recursive: true);
            }

            Directory.CreateDirectory(partial);

            foreach (var file in records.EnumerateFiles("*.json"))
            {
                try
                {
                    if (JsonSerializer.Deserialize<SuppliedModel>(File.ReadAllText(file.FullName), Json) is not { } model)
                    {
                        continue;
                    }

                    var missing = model.Blobs().Count(sha => !File.Exists(Path.Combine(directory, "blobs", sha)));

                    if (missing > 0)
                    {
                        // D2: whole or not supplied. The record stays for an admin to see and fetch again.
                        logger.LogWarning("Relay supply: '{Name}' is missing {Missing} blobs and is not supplied; fetch or import it again", model.Name, missing);
                        continue;
                    }

                    models[model.Id] = model;
                }
                catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
                {
                    logger.LogWarning("Relay supply: {File} is not a supply record: {Reason}", file.Name, ex.Message);
                }
            }

            logger.LogInformation("Relay supply: {Count} models in {Directory}", models.Count, directory);
            root = directory;
            return directory;
        }
    }
}
