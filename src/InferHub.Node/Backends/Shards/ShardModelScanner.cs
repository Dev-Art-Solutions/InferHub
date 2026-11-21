using System.Text.RegularExpressions;
using InferHub.Shared.Fit;

namespace InferHub.Node.Backends.Shards;

/// <summary>
/// Phase 144 D2: the GGUF files in this node's group models directory, as the hub needs them to place a group by itself —
/// their names, their sizes, their layer counts and KV per token, read from their headers. The same files
/// <see cref="ShardGroupHost.ModelFile"/> would find: in the directory, then one level down (143), the first of a name wins.
/// A split model is listed once, by its first part, with every part's bytes. Read again at most every <c>every</c>, in the
/// background; a header is read only when its file's size or time changed.
/// </summary>
public sealed partial class ShardModelScanner(string? directory, ILogger logger, TimeProvider? time = null, TimeSpan? every = null)
{
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly TimeSpan every = every ?? TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, (long Length, DateTime Written, ShardModelFile? File)> headers = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private IReadOnlyList<ShardModelFile>? current;
    private DateTimeOffset scanned = DateTimeOffset.MinValue;
    private Task? scanning;

    /// <summary>The list changed after a scan.</summary>
    public event Action? Changed;

    /// <summary>The last list read — null until the first scan ended, or without a directory — and a new scan when it is old.</summary>
    public IReadOnlyList<ShardModelFile>? Current()
    {
        lock (sync)
        {
            if (directory is not null && scanning is null && time.GetUtcNow() - scanned >= every)
            {
                scanning = Task.Run(RefreshAsync);
            }

            return current;
        }
    }

    /// <summary>Reads the directory now; what <see cref="Current"/> returns afterwards.</summary>
    public async Task<IReadOnlyList<ShardModelFile>?> RefreshAsync()
    {
        IReadOnlyList<ShardModelFile>? found = null;

        try
        {
            found = directory is null ? null : await ScanAsync(directory);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not list the group models directory {Directory}", directory);
        }

        bool changed;

        lock (sync)
        {
            changed = !Same(current, found);
            current = found ?? current;
            scanned = time.GetUtcNow();
            scanning = null;
        }

        if (changed && found is not null)
        {
            Changed?.Invoke();
        }

        return found;
    }

    private async Task<IReadOnlyList<ShardModelFile>> ScanAsync(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var files = new List<ShardModelFile>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var directories = new[] { root }.Concat(SubDirectories(root));

        foreach (var dir in directories)
        {
            string[] entries;

            try
            {
                entries = Directory.GetFiles(dir, "*.gguf").Order(StringComparer.Ordinal).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var path in entries)
            {
                var name = Path.GetFileName(path);
                var split = SplitPart().Match(name);

                if ((split.Success && split.Groups[2].Value != "00001") || !names.Add(name))
                {
                    continue;
                }

                var bytes = new FileInfo(path).Length;

                if (split.Success)
                {
                    bytes = entries
                        .Where(e => SplitPart().Match(Path.GetFileName(e)) is { Success: true } m && m.Groups[1].Value == split.Groups[1].Value && m.Groups[3].Value == split.Groups[3].Value)
                        .Sum(e => new FileInfo(e).Length);
                }

                if (await ReadAsync(path, name, bytes) is { } file)
                {
                    files.Add(file);
                }
            }
        }

        return files;
    }

    private async Task<ShardModelFile?> ReadAsync(string path, string name, long bytes)
    {
        var info = new FileInfo(path);

        lock (sync)
        {
            if (headers.TryGetValue(path, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc)
            {
                return known.File is null ? null : known.File with { MiB = bytes / (1024 * 1024) };
            }
        }

        ShardModelFile? file;

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
            var header = await GgufHeader.ReadAsync(stream);
            var facts = ModelFacts.FromGguf(ModelFacts.SourceGguf, name, header, info.Length, []);
            file = new ShardModelFile(name, bytes / (1024 * 1024), facts.Layers, facts.KvBytesPerToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or FormatException)
        {
            // Not a GGUF it can read (a partial download, another format): not offered for a group.
            logger.LogDebug(ex, "Could not read the GGUF header of {Path}", path);
            file = null;
        }

        lock (sync)
        {
            headers[path] = (info.Length, info.LastWriteTimeUtc, file);
        }

        return file;
    }

    private static IEnumerable<string> SubDirectories(string root)
    {
        try
        {
            return Directory.GetDirectories(root).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool Same(IReadOnlyList<ShardModelFile>? a, IReadOnlyList<ShardModelFile>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);

    [GeneratedRegex(@"^(.*)-(\d{5})-of-(\d{5})\.gguf$", RegexOptions.CultureInvariant)]
    private static partial Regex SplitPart();
}
