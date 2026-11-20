using System.Globalization;
using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Shared.Fit;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends.Shards;

/// <summary>
/// Phase 143: a member fills its own worker's cache from the model file on its own disk — the file its coordinator delivered
/// (98's download, through the relay and supply of 122–124 on a sealed node) — so a head loading the group sends it hashes, not
/// tensors (D1). One assignment at a time; each tensor is hashed while it is copied, into a <c>.part</c> file renamed only when
/// whole, because <c>rpc-server</c> answers a hash from any file of that name, whole or not (D3). Inert unless the worker runs
/// with its cache (<c>Node:Shards:Cache</c>, on by default): with no cache there is nothing to fill, and the state says null.
/// </summary>
public sealed class ShardSeeder : IHostedService, IAsyncDisposable
{
    private const int Buffer = 4 * 1024 * 1024;
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(5);

    private readonly ShardOptions options;
    private readonly string? modelsDir;
    private readonly string? cacheRoot;
    private readonly ILogger logger;
    private readonly TimeProvider time;
    private readonly long threshold;
    private readonly Func<string, long?> freeBytes;
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim wake = new(0);
    private CancellationTokenSource? life;
    private Task? loop;
    private DateTimeOffset lastReport;

    public ShardSeeder(IOptions<NodeOptions> node, IOptions<BackendOptions> backend, ShardWorker worker, ILogger<ShardSeeder> logger)
        : this(node.Value.Shards, ShardGroupHost.ModelsDirectory(node.Value.Shards, backend.Value), ShardWorker.CacheRoot(node.Value.Shards), logger, null)
    {
        worker.AttachSeeds(this);
    }

    internal ShardSeeder(ShardOptions options, string? modelsDir, string? cacheRoot, ILogger logger, TimeProvider? time,
        long threshold = ShardSeedPlan.HashThreshold, Func<string, long?>? freeBytes = null)
    {
        this.options = options;
        this.modelsDir = modelsDir;
        this.cacheRoot = cacheRoot;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
        this.threshold = threshold;
        this.freeBytes = freeBytes ?? FreeBytes;
    }

    /// <summary>Its state changed: an assignment started, moved on, or ended.</summary>
    public event Action? Changed;

    /// <summary>Whether this node can fill a cache at all: an offer, a worker with <c>-c</c>, a directory llama.cpp will read.</summary>
    public bool Enabled => options.Enabled && options.Cache && cacheRoot is not null;

    /// <summary>Where <c>rpc-server -c</c> keeps tensors: <c>&lt;LLAMA_CACHE&gt;/rpc</c>.</summary>
    public string? RpcDirectory => cacheRoot is null ? null : Path.Combine(cacheRoot, "rpc");

    /// <summary>The assignments as the hub should see them, or null when this node cannot fill a cache (the hub never waits).</summary>
    public IReadOnlyList<ShardSeedState>? State()
    {
        if (!Enabled)
        {
            return null;
        }

        lock (sync)
        {
            return entries.Values.Select(e => e.State).OrderBy(s => s.Group, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>The hub's list (D1): new assignments are queued, ones it dropped are abandoned — a half-copied tensor deleted.</summary>
    public void SetSeeds(ShardSeedList list)
    {
        if (!Enabled)
        {
            return;
        }

        var changed = false;

        lock (sync)
        {
            var wanted = list.Seeds.ToDictionary(s => s.Key, StringComparer.Ordinal);

            foreach (var key in entries.Keys.Where(k => !wanted.ContainsKey(k)).ToArray())
            {
                entries[key].Cancel.Cancel();
                entries.Remove(key);
                changed = true;
            }

            foreach (var spec in list.Seeds.Where(s => !entries.ContainsKey(s.Key)))
            {
                var (path, problem) = ShardGroupHost.ModelFile(modelsDir, spec.Model);
                var state = path is null
                    ? new ShardSeedState(spec.Key, spec.Group, spec.Model, ShardSeedState.Absent, 0, 0, problem)
                    : new ShardSeedState(spec.Key, spec.Group, spec.Model, ShardSeedState.Seeding, 0, 0);
                entries[spec.Key] = new Entry(spec, path, state);
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
            wake.Release();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Enabled)
        {
            life = new CancellationTokenSource();
            loop = Task.Run(() => LoopAsync(life.Token));
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (life is null)
        {
            return;
        }

        await life.CancelAsync();

        lock (sync)
        {
            foreach (var entry in entries.Values)
            {
                entry.Cancel.Cancel();
            }
        }

        try
        {
            await (loop ?? Task.CompletedTask).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        life?.Dispose();
    }

    /// <summary>Runs every queued assignment, one at a time — two at once would only share one disk.</summary>
    internal async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Entry? next;

            lock (sync)
            {
                next = entries.Values.FirstOrDefault(e => e.State.State == ShardSeedState.Seeding && !e.Started);

                if (next is not null)
                {
                    next.Started = true;
                }
            }

            if (next is null)
            {
                try
                {
                    await wake.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, next.Cancel.Token);
            await SeedAsync(next, linked.Token);
        }
    }

    /// <summary>One assignment, to its end: ready, failed with the sentence, or abandoned.</summary>
    internal async Task SeedAsync(Entry entry, CancellationToken token)
    {
        try
        {
            var final = await FillAsync(entry, token);
            Set(entry, final, force: true);
        }
        catch (OperationCanceledException)
        {
            // Abandoned: the hub dropped it, or the node stops. Whatever is whole stays in the cache; the part was deleted.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
        {
            logger.LogWarning(ex, "Could not fill the shard cache for group {Group} from {Model}; the head will send those layers", entry.Spec.Group, entry.Spec.Model);
            Set(entry, entry.State with { State = ShardSeedState.Failed, Error = $"could not fill the cache from {entry.Spec.Model}: {ex.Message}" }, force: true);
        }
    }

    private async Task<ShardSeedState> FillAsync(Entry entry, CancellationToken token)
    {
        var spec = entry.Spec;
        var rpc = RpcDirectory!;
        Directory.CreateDirectory(rpc);

        await using var file = new FileStream(entry.Path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Buffer, FileOptions.SequentialScan);
        var header = await GgufHeader.ReadAsync(file, token);
        var blocks = header.Values.FirstOrDefault(v => v.Key.EndsWith(".block_count", StringComparison.Ordinal)).Value;

        if (blocks is null || !int.TryParse(Convert.ToString(blocks, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var layers) || layers < 1)
        {
            return entry.State with { State = ShardSeedState.Failed, Error = $"{spec.Model} names no block count: its layers cannot be told apart" };
        }

        var split = ShardLayers.Split(layers, spec.Weights);

        if (spec.Index < 1 || spec.Index >= split.Count)
        {
            return entry.State with { State = ShardSeedState.Failed, Error = $"place {spec.Index} is not a member's in a split of {split.Count}" };
        }

        var (first, count, output) = split[spec.Index];
        var tensors = ShardSeedPlan.Tensors(header, first, count, output, threshold);
        var total = tensors.Sum(t => t.Bytes);
        var index = IndexPath(spec.Model, first, count, output);

        // D4: what was filled before for this file and these layers is not read again — the record names each hash, and a hash
        // whose file is still there with its size is trusted (the file is named by its content).
        if (Recorded(index, file.Length, File.GetLastWriteTimeUtc(entry.Path!)) is { } known
            && known.Count == tensors.Count
            && known.Zip(tensors).All(p => new FileInfo(Path.Combine(rpc, p.First)) is { Exists: true } f && f.Length == p.Second.Bytes))
        {
            return entry.State with { State = ShardSeedState.Ready, DoneMiB = Mib(total), TotalMiB = Mib(total), Error = null };
        }

        if (freeBytes(rpc) is { } free && free < total + (1L << 30))
        {
            return entry.State with
            {
                State = ShardSeedState.Failed,
                TotalMiB = Mib(total),
                Error = $"{Mib(total)} MiB of layers need the disk of {rpc}, which has {Mib(free)} MiB free (1 GiB kept)"
            };
        }

        Set(entry, entry.State with { TotalMiB = Mib(total) }, force: true);
        var names = new List<string>(tensors.Count);
        var done = 0L;
        var buffer = new byte[Buffer];

        foreach (var (name, start, bytes) in tensors)
        {
            token.ThrowIfCancellationRequested();
            var part = Path.Combine(rpc, $".inferhub-{Guid.NewGuid():N}.part");
            var hash = ShardSeedPlan.Fnv1aStart;

            try
            {
                await using (var target = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, Buffer))
                {
                    file.Position = start;
                    var left = bytes;

                    while (left > 0)
                    {
                        var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), token);

                        if (read == 0)
                        {
                            throw new EndOfStreamException($"{spec.Model} ends inside tensor '{name}'");
                        }

                        hash = ShardSeedPlan.Fnv1a(buffer.AsSpan(0, read), hash);
                        await target.WriteAsync(buffer.AsMemory(0, read), token);
                        left -= read;
                        done += read;
                        Set(entry, entry.State with { DoneMiB = Mib(done) }, force: false);
                    }
                }

                var final = Path.Combine(rpc, ShardSeedPlan.FileName(hash));

                if (new FileInfo(final) is { Exists: true } existing && existing.Length == bytes)
                {
                    File.Delete(part);
                }
                else
                {
                    // D3: a reader sees the whole tensor or no file — never one being written.
                    File.Move(part, final, overwrite: true);
                }

                names.Add(ShardSeedPlan.FileName(hash));
            }
            finally
            {
                if (File.Exists(part))
                {
                    File.Delete(part);
                }
            }
        }

        Record(index, file.Length, File.GetLastWriteTimeUtc(entry.Path!), names);
        logger.LogInformation("Filled the shard cache for group {Group}: {Count} tensors of layers {First}–{Last}{Output} from {Model}, {MiB} MiB",
            spec.Group, names.Count, first, first + count - 1, output ? " and the output" : "", spec.Model, Mib(total));
        return entry.State with { State = ShardSeedState.Ready, DoneMiB = Mib(total), TotalMiB = Mib(total), Error = null };
    }

    private void Set(Entry entry, ShardSeedState state, bool force)
    {
        bool tell;

        lock (sync)
        {
            if (!entries.TryGetValue(entry.Spec.Key, out var current) || !ReferenceEquals(current, entry))
            {
                return;
            }

            entry.State = state;
            var now = time.GetUtcNow();
            tell = force || now - lastReport >= ReportEvery;

            if (tell)
            {
                lastReport = now;
            }
        }

        if (tell)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>D4's record: beside llama.cpp's cache, never inside the directory it reads.</summary>
    private string IndexPath(string model, int first, int count, bool output) =>
        Path.Combine(cacheRoot!, "inferhub-seeds", $"{model}.{first}-{count}{(output ? "-out" : "")}.json");

    private static IReadOnlyList<string>? Recorded(string path, long length, DateTime written)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var record = JsonSerializer.Deserialize<SeedRecord>(File.ReadAllText(path));
            return record is not null && record.Length == length && record.WrittenUtc == written ? record.Hashes : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Record(string path, long length, DateTime written, IReadOnlyList<string> hashes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new SeedRecord(length, written, hashes)));
    }

    private static long? FreeBytes(string directory)
    {
        try
        {
            // The deepest mount that holds it: in a container the cache is a volume, not the root file system.
            var full = Path.GetFullPath(directory);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && full.StartsWith(d.Name, comparison))
                .OrderByDescending(d => d.Name.Length)
                .FirstOrDefault()?.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long Mib(long bytes) => bytes / (1024 * 1024);

    private sealed record SeedRecord(long Length, DateTime WrittenUtc, IReadOnlyList<string> Hashes);

    internal sealed class Entry(ShardSeedSpec spec, string? path, ShardSeedState state)
    {
        public ShardSeedSpec Spec { get; } = spec;
        public string? Path { get; } = path;
        public ShardSeedState State { get; set; } = state;
        public bool Started { get; set; }
        public CancellationTokenSource Cancel { get; } = new();
    }
}
