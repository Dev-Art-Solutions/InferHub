using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Backends;

/// <summary>
/// A prefix of a rendered prompt, known by its length and two hashes — never by its text. <see cref="Opening"/> (the
/// first <see cref="OpeningChars"/> characters) is a cheap first test; <see cref="Hash"/> is the whole prefix's.
/// </summary>
public sealed record ContextPrefix(int Chars, string Opening, string Hash)
{
    public const int OpeningChars = 1024;

    public static ContextPrefix Of(string text, int chars)
    {
        chars = Math.Clamp(chars, 0, text.Length);
        return new ContextPrefix(chars, Sha(text.AsSpan(0, Math.Min(OpeningChars, chars))), Sha(text.AsSpan(0, chars)));
    }

    /// <summary>Whether <paramref name="text"/> starts with this prefix.</summary>
    public bool IsPrefixOf(string text) =>
        text.Length >= Chars
        && Sha(text.AsSpan(0, Math.Min(OpeningChars, Chars))) == Opening
        && Sha(text.AsSpan(0, Chars)) == Hash;

    private static string Sha(ReadOnlySpan<char> text)
    {
        var bytes = Encoding.UTF8.GetBytes(text.ToArray());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

/// <summary>One slot file on disk: which prefix it holds, how big it is, and how it has been used.</summary>
public sealed class ContextTierEntry
{
    public string Id { get; set; } = "";

    public int Chars { get; set; }

    public string Opening { get; set; } = "";

    public string Hash { get; set; } = "";

    public long Tokens { get; set; }

    public long Bytes { get; set; }

    /// <summary>Times it was restored. A file written and never read back is the first to go.</summary>
    public long Uses { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset LastUsedUtc { get; set; }

    public bool Pinned { get; set; }

    [JsonIgnore]
    public string File => $"ctx-{Id}.bin";

    [JsonIgnore]
    public ContextPrefix Prefix => new(Chars, Opening, Hash);

    /// <summary>
    /// 153 D3: what to keep when the disk is full — how often a prefix came back, discounted by how long ago. A pinned
    /// file is never scored.
    /// </summary>
    public double Score(DateTimeOffset now) => (Uses + 1) / (1 + Math.Max(0, (now - LastUsedUtc).TotalHours));

    public ContextTierEntryInfo Info() => new(Id, Tokens, Bytes, Uses, CreatedUtc, LastUsedUtc, Pinned);
}

/// <summary>
/// The disk tier's book (phase 153): <c>index.json</c> beside the slot files llama-server writes into
/// <c>--slot-save-path</c>. The files are the truth: an entry whose file is gone is dropped on load, and a
/// <c>ctx-*.bin</c> no entry names is deleted (a crash between the save and the index write).
/// </summary>
public sealed class ContextTierIndex
{
    private const string IndexFile = "index.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string directory;
    private readonly List<ContextTierEntry> entries = [];
    private readonly object sync = new();

    public ContextTierIndex(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
        Load();
    }

    public string Location => directory;

    public long HeldBytes
    {
        get
        {
            lock (sync)
            {
                return entries.Sum(e => e.Bytes);
            }
        }
    }

    public IReadOnlyList<ContextTierEntry> All()
    {
        lock (sync)
        {
            return entries.OrderByDescending(e => e.LastUsedUtc).ToArray();
        }
    }

    /// <summary>The longest entry whose prefix <paramref name="text"/> starts with, or null.</summary>
    public ContextTierEntry? Find(string text)
    {
        ContextTierEntry[] candidates;

        lock (sync)
        {
            candidates = entries.Where(e => e.Chars <= text.Length).OrderByDescending(e => e.Chars).ToArray();
        }

        return candidates.FirstOrDefault(e => e.Prefix.IsPrefixOf(text));
    }

    public ContextTierEntry? Get(string id)
    {
        lock (sync)
        {
            return entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Add(ContextTierEntry entry)
    {
        lock (sync)
        {
            entries.Add(entry);
            Save();
        }
    }

    public void Touch(ContextTierEntry entry, DateTimeOffset now)
    {
        lock (sync)
        {
            entry.Uses++;
            entry.LastUsedUtc = now;
            Save();
        }
    }

    public bool SetPinned(string id, bool pinned)
    {
        lock (sync)
        {
            var entry = entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                return false;
            }

            entry.Pinned = pinned;
            Save();
            return true;
        }
    }

    public bool Remove(string id)
    {
        lock (sync)
        {
            var entry = entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                return false;
            }

            Drop(entry);
            Save();
            return true;
        }
    }

    /// <summary>
    /// 153 D3: a shorter prefix of the same conversation that was never read back is replaced by the longer one —
    /// it would only ever be the second choice. One that was restored stays: another conversation may share it.
    /// </summary>
    /// <param name="text">The longer entry's prefix text, held by the caller for this call only.</param>
    public int Supersede(ContextTierEntry longer, string text)
    {
        lock (sync)
        {
            var gone = entries
                .Where(e => e != longer && !e.Pinned && e.Uses == 0 && e.Chars < longer.Chars && e.Opening == longer.Opening)
                .Where(e => e.Prefix.IsPrefixOf(text))
                .ToArray();

            foreach (var entry in gone)
            {
                Drop(entry);
            }

            if (gone.Length > 0)
            {
                Save();
            }

            return gone.Length;
        }
    }

    /// <summary>Deletes the lowest-scored unpinned files until <paramref name="room"/> more bytes fit under <paramref name="budget"/>.</summary>
    public int MakeRoom(long budget, long room, DateTimeOffset now)
    {
        lock (sync)
        {
            var evicted = 0;

            while (entries.Sum(e => e.Bytes) + room > budget)
            {
                var victim = entries.Where(e => !e.Pinned).OrderBy(e => e.Score(now)).FirstOrDefault();

                if (victim is null)
                {
                    break;
                }

                Drop(victim);
                evicted++;
            }

            if (evicted > 0)
            {
                Save();
            }

            return evicted;
        }
    }

    /// <summary>Deletes a file that never became an entry (a save that did not fit).</summary>
    public void Discard(string file)
    {
        try
        {
            File.Delete(Path.Combine(directory, file));
        }
        catch (IOException)
        {
        }
    }

    private void Drop(ContextTierEntry entry)
    {
        entries.Remove(entry);

        try
        {
            File.Delete(Path.Combine(directory, entry.File));
        }
        catch (IOException)
        {
            // Mapped by a restore in flight: the next load deletes it as unnamed.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Load()
    {
        var path = Path.Combine(directory, IndexFile);

        if (File.Exists(path))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<List<ContextTierEntry>>(File.ReadAllText(path), Json) ?? [];
                entries.AddRange(stored.Where(e => !string.IsNullOrWhiteSpace(e.Id) && File.Exists(Path.Combine(directory, e.File))));
            }
            catch (JsonException)
            {
                // An index that does not parse loses the bookkeeping, not the answers: the files below are swept and the
                // conversations are computed again. The node owns this directory.
            }
        }

        var named = entries.Select(e => e.File).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var stray in System.IO.Directory.EnumerateFiles(directory, "ctx-*.bin").Where(f => !named.Contains(Path.GetFileName(f))))
        {
            try
            {
                File.Delete(stray);
            }
            catch (IOException)
            {
            }
        }

        Save();
    }

    private void Save()
    {
        var path = Path.Combine(directory, IndexFile);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(entries, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
