using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Shared.Images;

/// <summary>
/// A file-backed <see cref="IImageJobArchive"/> (<c>Images:Jobs:Persistence=file</c>): one
/// <c>{id}.json</c> record and one <c>{id}.{index}.bin</c> per image, under
/// <c>Images:Jobs:DataDirectory</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A file per job rather than <c>FileProfileStore</c>'s append-log-and-snapshot</b>, and the
/// difference is what is being stored: a profile is a few hundred bytes that is rewritten in place
/// and read as a whole, while a job carries megabytes that are deleted individually the instant
/// somebody fetches them. An ops log of image bytes would have to be compacted to reclaim a delivered
/// picture — which means "read once" would leave the pixels on disk until a compaction ran, and that
/// is the one thing this format may not do (D4). <c>File.Delete</c> is the whole mechanism instead.
/// </para>
/// <para>
/// <b>Nothing here throws into a caller.</b> The caller is the job store, which is called from a
/// dispatch loop; a full disk must cost the archive, never the render. Failures go to the
/// <c>onError</c> callback the host passes — one line of ASP.NET-free plumbing, the same shape as
/// <c>IVectorLog</c> (38 D3), so <c>InferHub.Shared.csproj</c> is still empty.
/// </para>
/// <para>
/// Writes are temp-file-then-move, which is <c>FileProfileStore</c>'s and <c>RawCollection</c>'s
/// discipline: a record torn by a crash mid-write would be a job whose state nobody can read, and the
/// state is the only reason the record exists.
/// </para>
/// </remarks>
public sealed class FileImageJobArchive : IImageJobArchive
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object gate = new();
    private readonly string directory;
    private readonly Action<string, Exception>? onError;

    public FileImageJobArchive(string directory, Action<string, Exception>? onError = null)
    {
        this.directory = string.IsNullOrWhiteSpace(directory) ? ImageJobOptions.DefaultDataDirectory : directory;
        this.onError = onError;

        Directory.CreateDirectory(this.directory);
    }

    public bool Enabled => true;

    public IReadOnlyList<ArchivedImageJob> Load()
    {
        lock (gate)
        {
            var jobs = new List<ArchivedImageJob>();

            foreach (var path in SafeEnumerate("*.json"))
            {
                try
                {
                    if (JsonSerializer.Deserialize<ArchivedImageJob>(File.ReadAllText(path), JsonOptions) is { } job)
                    {
                        jobs.Add(job);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    // A record we cannot read is a job that did not survive, which is a state the
                    // whole design already has a name for. Never a failed startup.
                    Report($"could not read the archived image job at '{path}'", ex);
                }
            }

            return jobs;
        }
    }

    public IReadOnlyList<byte[]> ReadImages(Guid id, int count)
    {
        lock (gate)
        {
            var images = new List<byte[]>(Math.Max(0, count));

            for (var index = 0; index < count; index++)
            {
                try
                {
                    var path = ImagePath(id, index);

                    if (!File.Exists(path))
                    {
                        break;
                    }

                    images.Add(File.ReadAllBytes(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Report($"could not read image {index} of archived job {id}", ex);
                    break;
                }
            }

            return images;
        }
    }

    public void Save(ArchivedImageJob job, IReadOnlyList<byte[]> images)
    {
        lock (gate)
        {
            try
            {
                for (var index = 0; index < images.Count; index++)
                {
                    var path = ImagePath(job.Id, index);

                    if (!File.Exists(path))
                    {
                        WriteAtomic(path, images[index]);
                    }
                }

                // Anything past what the record now claims is gone from the API and must be gone
                // from the disk in the same breath (D4) — a delivered picture that survives as a
                // file is the read-once rule quietly switched off.
                for (var index = images.Count; ; index++)
                {
                    var path = ImagePath(job.Id, index);

                    if (!File.Exists(path))
                    {
                        break;
                    }

                    File.Delete(path);
                }

                WriteAtomic(RecordPath(job.Id), JsonSerializer.SerializeToUtf8Bytes(job, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report($"could not archive image job {job.Id}", ex);
            }
        }
    }

    public void Delete(Guid id)
    {
        lock (gate)
        {
            try
            {
                var record = RecordPath(id);

                if (File.Exists(record))
                {
                    File.Delete(record);
                }

                foreach (var path in SafeEnumerate($"{id}.*.bin"))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report($"could not delete archived image job {id}", ex);
            }
        }
    }

    private string RecordPath(Guid id) => Path.Combine(directory, $"{id}.json");

    private string ImagePath(Guid id, int index) => Path.Combine(directory, $"{id}.{index}.bin");

    private IEnumerable<string> SafeEnumerate(string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"could not list '{pattern}' under '{directory}'", ex);
            return [];
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temp = path + ".tmp";

        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private void Report(string message, Exception ex) => onError?.Invoke(message, ex);
}
