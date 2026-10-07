using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using InferHub.Node.Backends.Colibri;
using InferHub.Shared.HuggingFace;

namespace InferHub.Node.Backends.HuggingFace;

/// <summary>
/// The node's Hugging Face downloads (phase 98): the hub hands it a link, it fetches the model once,
/// and an engine serves it from then on — a GGUF in the llama.cpp router's directory (D2), a
/// safetensors checkpoint converted into the colibri catalogue (D3).
/// </summary>
/// <remarks>
/// <b>A model the store downloaded carries <see cref="Marker"/></b>: the repo, the commit and the
/// files. A second pull of the same thing is "already here", and a delete removes only a directory
/// with the marker — never one the operator put there by hand.
/// </remarks>
public sealed partial class HuggingFaceStore(
    HuggingFaceOptions options,
    HuggingFaceTargets targets,
    Func<HuggingFaceClient> client,
    IColibriConverter? converter,
    Func<string, Func<Task>?, CancellationToken, Task>? restartEngine,
    IColibriControl? colibri,
    ILogger logger)
{
    public const string Marker = ".inferhub-source.json";

    public const string HttpClientName = "huggingface";

    private static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim downloads = new(Math.Max(1, options.MaxConcurrentDownloads));
    private readonly SemaphoreSlim conversions = new(1, 1);

    // Found live: on a fresh volume the router's directory did not exist, so the llama.cpp engine
    // failed its precondition and sat "failed" until the first download created it.
    private readonly bool prepared = Prepare(targets, logger);

    private static bool Prepare(HuggingFaceTargets targets, ILogger logger)
    {
        foreach (var directory in new[] { targets.GgufDirectory, targets.ColibriDirectory })
        {
            if (directory is null)
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not create {Directory}; downloads into it will fail until it exists and is writable.", directory);
            }
        }

        return true;
    }

    [GeneratedRegex(@"-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitSuffix();

    [GeneratedRegex(@"[^A-Za-z0-9_.-]+")]
    private static partial Regex NotNameChars();

    public HuggingFaceTargets Targets => targets;

    public async IAsyncEnumerable<ModelPullProgress> PullAsync(string link, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!HfReference.TryParse(link, null, out var reference, out var error))
        {
            throw new ArgumentException(error);
        }

        var hf = client();
        yield return new ModelPullProgress($"asking Hugging Face about {reference}", null, null);

        var commit = await hf.CommitAsync(reference!, cancellationToken);
        var tree = await hf.TreeAsync(reference!, commit, cancellationToken);
        var ggufs = tree.Where(f => f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (reference!.File is { } named && !named.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{named}' is not a .gguf; a single file can be pulled only as a GGUF. Give the repository link to convert a checkpoint for colibri.");
        }

        IAsyncEnumerable<ModelPullProgress> steps = ggufs.Length > 0
            ? GgufAsync(hf, reference, commit, SelectGguf(reference, ggufs), cancellationToken)
            : IsCheckpoint(tree)
                ? CheckpointAsync(reference, commit, cancellationToken)
                : throw new ArgumentException(
                    $"'{reference.RepoId}' has no .gguf and is not a safetensors checkpoint (no config.json beside *.safetensors); there is nothing here llama.cpp or colibri can serve.");

        await foreach (var step in steps)
        {
            yield return step;
        }
    }

    /// <summary>Removes a model the store downloaded (98 D4): the router is stopped around a GGUF's files, a colibri model is unloaded first.</summary>
    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        name = (name ?? string.Empty).Trim();

        if (Owned(targets.GgufDirectory, name) is { } gguf)
        {
            async Task Remove()
            {
                Directory.Delete(gguf, recursive: true);
                await Task.CompletedTask;
            }

            if (restartEngine is not null && targets.GgufEngine is not null)
            {
                // On Windows a loaded model's file cannot be deleted under the process that maps it.
                await restartEngine(targets.GgufEngine, Remove, cancellationToken);
            }
            else
            {
                await Remove();
            }

            logger.LogInformation("Deleted GGUF model '{Name}' ({Directory}).", name, gguf);
            return;
        }

        if (Owned(targets.ColibriDirectory, name) is { } converted)
        {
            if (colibri is IInferenceBackend catalogue)
            {
                await catalogue.UnloadAsync(name, cancellationToken);
            }

            Directory.Delete(converted, recursive: true);
            logger.LogInformation("Deleted colibri model '{Name}' ({Directory}).", name, converted);
            return;
        }

        throw new InvalidOperationException(
            $"this node has no model '{name}' that it downloaded from Hugging Face; a model put on the box by hand is the operator's to remove");
    }

    // ------------------------------------------------------------------ D2: GGUF

    /// <summary>
    /// Which files a GGUF pull takes: a named file (and its split siblings); else the files that carry
    /// the quant; else the repository's only model. An <c>mmproj</c> comes along — the directory is
    /// then one multimodal model in llama.cpp's convention.
    /// </summary>
    internal static IReadOnlyList<HfFile> SelectGguf(HfReference reference, IReadOnlyList<HfFile> ggufs)
    {
        static bool IsProjector(HfFile f) => Path.GetFileName(f.Path).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase);

        static string Model(HfFile f) => SplitSuffix().Replace(f.Path, ".gguf");

        var models = ggufs.Where(f => !IsProjector(f)).ToArray();
        HfFile[] chosen;

        if (reference.File is { } file)
        {
            var hit = ggufs.FirstOrDefault(f => string.Equals(f.Path, file, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"'{reference.RepoId}' has no file '{file}'");

            chosen = models.Where(f => string.Equals(Model(f), Model(hit), StringComparison.OrdinalIgnoreCase)).ToArray();

            if (chosen.Length == 0)
            {
                chosen = [hit];
            }
        }
        else
        {
            var groups = models.GroupBy(Model, StringComparer.OrdinalIgnoreCase).ToArray();

            if (reference.Quant is { } quant)
            {
                var pattern = new Regex($@"(^|[-_.]){Regex.Escape(quant)}([-_.]|$)", RegexOptions.IgnoreCase);
                var matching = groups.Where(g => pattern.IsMatch(Path.GetFileNameWithoutExtension(Path.GetFileName(g.Key)))).ToArray();

                chosen = matching.Length switch
                {
                    1 => matching[0].ToArray(),
                    0 => throw new ArgumentException($"'{reference.RepoId}' has no {quant} GGUF; it has {Quants(groups)}"),
                    _ => throw new ArgumentException($"'{quant}' matches {matching.Length} files in '{reference.RepoId}' ({string.Join(", ", matching.Select(g => Path.GetFileName(g.Key)))}); link the file itself")
                };
            }
            else
            {
                chosen = groups.Length switch
                {
                    1 => groups[0].ToArray(),
                    0 => throw new ArgumentException($"'{reference.RepoId}' has only a projector (mmproj) GGUF, no model"),
                    _ => throw new ArgumentException($"'{reference.RepoId}' has {groups.Length} GGUF models; choose a quantization ({Quants(groups)}) or link one file")
                };
            }
        }

        var projector = ggufs.Where(IsProjector)
            .OrderBy(f => f.Path.Contains("f16", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .FirstOrDefault();

        return projector is null ? chosen : [.. chosen, projector];
    }

    private static string Quants(IEnumerable<IGrouping<string, HfFile>> groups)
        => string.Join(", ", groups.Select(g => Path.GetFileNameWithoutExtension(Path.GetFileName(g.Key))));

    private async IAsyncEnumerable<ModelPullProgress> GgufAsync(
        HuggingFaceClient hf,
        HfReference reference,
        string commit,
        IReadOnlyList<HfFile> files,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (targets.GgufDirectory is not { } root)
        {
            throw new ArgumentException(
                $"'{reference.RepoId}' is GGUF, and this node has no llama.cpp router to serve it (a llamacpp engine with Serve:ModelsDir under Backend:Engines)");
        }

        var main = files.First(f => !Path.GetFileName(f.Path).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase));
        var name = Name(Path.GetFileNameWithoutExtension(SplitSuffix().Replace(Path.GetFileName(main.Path), ".gguf")));
        var directory = Path.Combine(root, name);

        if (AlreadyHere(directory, reference, commit, files) is { } done)
        {
            yield return new ModelPullProgress(done, null, null);
            yield break;
        }

        if (Directory.Exists(directory) && !File.Exists(Path.Combine(directory, Marker)) && Directory.EnumerateFileSystemEntries(directory).Any(e => !e.EndsWith(".part", StringComparison.Ordinal) && !e.EndsWith(".partial", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"'{directory}' already holds a model this node did not download; remove it or link another file");
        }

        if (downloads.CurrentCount == 0)
        {
            yield return new ModelPullProgress("waiting for another download to finish", null, null);
        }

        await downloads.WaitAsync(cancellationToken);

        try
        {
            var total = files.Sum(f => f.Size);
            long before = 0;

            foreach (var file in files)
            {
                long current = 0;
                var last = DateTimeOffset.MinValue;
                var label = $"downloading {Path.GetFileName(file.Path)}";

                // Partials keep a suffix the router does not list, and are renamed only after every
                // file is whole (below): a directory the router sees is always a complete model.
                var task = hf.DownloadAsync(reference, commit, file, Path.Combine(directory, Path.GetFileName(file.Path)) + ".part", have =>
                {
                    Interlocked.Exchange(ref current, have);
                }, cancellationToken);

                while (!task.IsCompleted)
                {
                    await Task.WhenAny(task, Task.Delay(ProgressEvery, cancellationToken));

                    if (DateTimeOffset.UtcNow - last >= ProgressEvery)
                    {
                        last = DateTimeOffset.UtcNow;
                        yield return new ModelPullProgress(label, total, before + Interlocked.Read(ref current));
                    }
                }

                await task;
                before += file.Size;
                yield return new ModelPullProgress($"verified {Path.GetFileName(file.Path)}", total, before);
            }

            foreach (var file in files)
            {
                var path = Path.Combine(directory, Path.GetFileName(file.Path));
                File.Move(path + ".part", path, overwrite: true);
            }

            await WriteMarkerAsync(directory, reference, commit, files, "gguf", cancellationToken);
        }
        finally
        {
            downloads.Release();
        }

        logger.LogInformation("Downloaded '{Repo}' ({Files} file(s)) as GGUF model '{Name}'.", reference.RepoId, files.Count, name);

        if (restartEngine is not null && targets.GgufEngine is not null)
        {
            // The router reads its directory at launch only (measured, b11417).
            yield return new ModelPullProgress($"restarting llama.cpp engine '{targets.GgufEngine}' so it lists '{name}'", null, null);
            await restartEngine(targets.GgufEngine, null, cancellationToken);
        }

        yield return new ModelPullProgress($"ready as '{name}' (llama.cpp)", null, null);
    }

    // ------------------------------------------------------------------ D3: a checkpoint for colibri

    internal static bool IsCheckpoint(IReadOnlyList<HfFile> tree)
        => tree.Any(f => f.Path == "config.json") && tree.Any(f => f.Path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase));

    private async IAsyncEnumerable<ModelPullProgress> CheckpointAsync(
        HfReference reference,
        string commit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (targets.ColibriDirectory is not { } root || converter is null)
        {
            throw new ArgumentException(
                $"'{reference.RepoId}' is a safetensors checkpoint. llama.cpp needs a GGUF (link a GGUF repository of the same model); colibri can convert it, and this node has no colibri catalogue (Colibri:Serve:ModelsDir) or {HuggingFaceOptions.SectionName}:Convert is off");
        }

        if (reference.Revision != HfReference.DefaultRevision)
        {
            throw new ArgumentException($"coli convert reads a repository's main branch only; '{reference.Revision}' cannot be converted");
        }

        var name = Name(reference.Repo.ToLowerInvariant());
        var directory = Path.Combine(root, name);

        if (AlreadyHere(directory, reference, commit, null) is { } done)
        {
            yield return new ModelPullProgress(done, null, null);
            yield break;
        }

        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        {
            if (!File.Exists(Path.Combine(directory, Marker)) && File.Exists(Path.Combine(directory, "config.json")))
            {
                throw new InvalidOperationException($"'{directory}' already holds a colibri model this node did not convert; remove it first");
            }

            // A conversion that died part way: start it again from nothing.
            Directory.Delete(directory, recursive: true);
        }

        if (conversions.CurrentCount == 0)
        {
            yield return new ModelPullProgress("waiting for another conversion to finish (one at a time)", null, null);
        }

        await conversions.WaitAsync(cancellationToken);

        try
        {
            yield return new ModelPullProgress($"converting '{reference.RepoId}' for colibri; colibri downloads the checkpoint itself — this can take a long time", null, null);

            var completed = false;

            // Found live: the converter writes config.json long before it finishes, and the catalogue lists
            // any directory holding one — the hub routed to a half-converted model. It converts into a
            // dot-directory the catalogue never lists (not a model name) and is renamed only when whole.
            var staging = Path.Combine(root, ".converting-" + name);

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            try
            {
                await foreach (var line in converter.ConvertAsync(reference.RepoId, staging, cancellationToken))
                {
                    yield return new ModelPullProgress($"coli convert: {line}", null, null);
                }

                completed = true;
            }
            finally
            {
                if (!completed && Directory.Exists(staging))
                {
                    try
                    {
                        Directory.Delete(staging, recursive: true);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not remove the unfinished conversion at {Directory}.", staging);
                    }
                }
            }

            if (!File.Exists(Path.Combine(staging, "config.json")))
            {
                Directory.Delete(staging, recursive: true);
                throw new InvalidOperationException($"coli convert finished and wrote no config.json; colibri would not list '{name}'");
            }

            await WriteMarkerAsync(staging, reference, commit, null, "colibri", cancellationToken);
            Directory.Move(staging, directory);
        }
        finally
        {
            conversions.Release();
        }

        logger.LogInformation("Converted '{Repo}' into colibri model '{Name}'.", reference.RepoId, name);
        yield return new ModelPullProgress($"ready as '{name}' (colibri)", null, null);
    }

    // ------------------------------------------------------------------ the marker

    private sealed record Source(string Repo, string Revision, string Commit, string Engine, string[] Files, DateTimeOffset At);

    private static string? AlreadyHere(string directory, HfReference reference, string commit, IReadOnlyList<HfFile>? files)
    {
        var path = Path.Combine(directory, Marker);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var source = JsonSerializer.Deserialize<Source>(File.ReadAllText(path));

            if (source is not null
                && string.Equals(source.Repo, reference.RepoId, StringComparison.OrdinalIgnoreCase)
                && (files is null || files.All(f => source.Files.Contains(Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase))))
            {
                return source.Commit == commit
                    ? $"already downloaded as '{Path.GetFileName(directory)}' (commit {commit[..Math.Min(12, commit.Length)]}); nothing to do"
                    : $"already downloaded as '{Path.GetFileName(directory)}' at commit {source.Commit[..Math.Min(12, source.Commit.Length)]}; delete it to fetch {commit[..Math.Min(12, commit.Length)]}";
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static async Task WriteMarkerAsync(string directory, HfReference reference, string commit, IReadOnlyList<HfFile>? files, string engine, CancellationToken cancellationToken)
    {
        var source = new Source(
            reference.RepoId,
            reference.Revision,
            commit,
            engine,
            files?.Select(f => Path.GetFileName(f.Path)).ToArray() ?? [],
            DateTimeOffset.UtcNow);

        await File.WriteAllTextAsync(Path.Combine(directory, Marker), JsonSerializer.Serialize(source), cancellationToken);
    }

    private static string? Owned(string? root, string name)
    {
        if (root is null || !ColibriOptions.IsModelName(name))
        {
            return null;
        }

        var directory = Path.Combine(root, name);
        return File.Exists(Path.Combine(directory, Marker)) ? directory : null;
    }

    /// <summary>A directory name both engines accept as a model name (97's token).</summary>
    internal static string Name(string raw)
    {
        var name = NotNameChars().Replace(raw, "-").Trim('-', '.', '_');

        if (name.Length > 64)
        {
            name = name[..64].TrimEnd('-', '.', '_');
        }

        return ColibriOptions.IsModelName(name) ? name : "model-" + Math.Abs(raw.GetHashCode()).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>Builds the store with the host, so its directories exist before the engines start.</summary>
public sealed class HuggingFaceStartup(HuggingFaceStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = store.Targets;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
