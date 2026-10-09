using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using InferHub.Node.Backends.HuggingFace;
using InferHub.Shared.Contracts;
using InferHub.Shared.HuggingFace;
using InferHub.Shared.Strata;

namespace InferHub.Node.Backends.Strata;

/// <summary>Runs one <c>setup.py</c> and hands back what it printed, line by line, then its exit code.</summary>
public interface IStrataSetupRunner
{
    /// <summary>Every output line (a <c>\r</c> ends one too); the last item has <see cref="SetupOutput.ExitCode"/> set.</summary>
    IAsyncEnumerable<SetupOutput> RunAsync(ProcessStartInfo info, CancellationToken cancellationToken);
}

public sealed record SetupOutput(string? Line, int? ExitCode = null);

/// <summary>
/// Installs a Strata model on this node from a Hugging Face link the hub sent (phase 99, D4): the link
/// names a family and a size, and Strata's own <c>setup.py --setup --yes --no-start</c> downloads it,
/// checks it, prepares it for this box and writes its config — which the catalogue then lists.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strata's installer, not ours</b> (load-bearing). Its setup pins every repo to a commit, checks
/// SHA-256, resumes a cut download, and then does what no GGUF download can: builds the expert pack and
/// the profile for this machine's RAM and GPU, and fetches the engine and the draft layer. Phase 98's
/// store downloading the shards would leave a directory nothing can serve. <i>Rejected:</i> the node
/// downloading the GGUFs itself and passing <c>--gguf-dir</c> — the same bytes, a second resume and
/// checksum implementation, and still setup's pack step after it.
/// </para>
/// <para>
/// <b>One at a time</b>: two installs are two 70 GB downloads and two pack builds on one disk. A second
/// one waits and says so. <c>HF_TOKEN</c> and <c>HF_ENDPOINT</c> come from <c>HuggingFace:</c>, which
/// Strata's setup reads exactly as <c>huggingface_hub</c> does.
/// </para>
/// <para>
/// <b>Pictures are the install's choice</b> (phase 100): the hub's <c>vision</c> is setup's
/// <c>--vision</c>, and the node's <c>Strata:Install:Vision</c> when the hub says nothing. Asking for
/// pictures on a size installed without them runs setup again for that size — "install another model
/// or change settings", in its own words — which skips everything already there and adds the image
/// encoder to the config. Not while that size is loaded: setup rewrites the config a running server
/// was started from, and the server would go on without the encoder anyway.
/// </para>
/// <para>
/// <b>Adding pictures changes nothing else.</b> Setup run for a size answers every question again with
/// its own recommendation — it reuses an earlier config's answers only when it adopts a new copy of
/// Strata — so the Coder set up at 32K would come back at this card's 128K. The node passes the
/// config's own context and KV precision back (<see cref="KeptChoices"/>), unless
/// <c>Strata:Install</c> names them.
/// </para>
/// </remarks>
public sealed partial class StrataInstaller(
    StrataOptions options,
    HuggingFaceOptions huggingFace,
    StrataCatalog catalog,
    IStrataSetupRunner runner,
    ILogger logger)
{
    private const int TailLines = 6;

    private readonly SemaphoreSlim one = new(1, 1);

    /// <summary>Whether <paramref name="model"/> is in the Strata catalogue — so a delete through the store is refused in Strata's words.</summary>
    public bool Has(string model) => catalog.CatalogNames.Contains(model?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>Qwen3…-00001-of-00002.gguf:   7.93 / 29.61 GB (27%)</c> — setup's download line; the image
    /// encoder's is labelled <c>vision encoder:</c>, with a space (found on the first real run of 100).
    /// </summary>
    [GeneratedRegex(@"^(?<file>\S+(?: \S+)*?):\s+(?<done>[\d.]+)\s*/\s*(?<total>[\d.]+)\s*GB\s*\((?<pct>\d+)%\)")]
    private static partial Regex DownloadLine();

    /// <summary><c>=== Step 5: downloading Qwen3.8-Flash-Next Coder IQ1_M ===</c></summary>
    [GeneratedRegex(@"^=+\s*Step\s+(?<n>\d+):\s*(?<title>.+?)\s*=+$")]
    private static partial Regex StepLine();

    public IAsyncEnumerable<ModelPullProgress> InstallAsync(HfReference reference, CancellationToken cancellationToken)
        => InstallAsync(reference, vision: null, cancellationToken);

    /// <param name="vision">setup's <c>--vision</c> for this install (100), or null for <c>Strata:Install:Vision</c>.</param>
    public async IAsyncEnumerable<ModelPullProgress> InstallAsync(
        HfReference reference,
        string? vision,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!StrataModels.TryMatch(reference, out var family, out var size, out var error))
        {
            throw new ArgumentException(error);
        }

        vision = string.IsNullOrWhiteSpace(vision) ? null : vision.Trim().ToLowerInvariant();

        if (!ModelCommand.IsKnownVision(vision))
        {
            throw new ArgumentException($"'{vision}' is not setup.py's --vision: yes, cpu or no");
        }

        var name = StrataModels.CatalogName(family!, size!);

        if (Settled(name, vision) is { } settled)
        {
            yield return new ModelPullProgress(settled, null, null);
            yield break;
        }

        if (AddingPictures(name, vision) && Loaded(name))
        {
            throw new InvalidOperationException(
                $"'{name}' is loaded; unload it (and unpin it) in the hub's Strata panel first — setup rewrites the config its server was started from, and pictures need a fresh start of it");
        }

        if (!await one.WaitAsync(0, cancellationToken))
        {
            yield return new ModelPullProgress("waiting: another Strata install is running on this node", null, null);
            await one.WaitAsync(cancellationToken);
        }

        try
        {
            // Installed while this one waited for the other.
            if (Settled(name, vision) is { } settledMeanwhile)
            {
                yield return new ModelPullProgress(settledMeanwhile, null, null);
                yield break;
            }

            var effective = vision ?? options.Install.Vision;
            var adding = AddingPictures(name, vision);

            logger.LogInformation(
                "{What} Strata {Family} {Size} as '{Name}' with its setup.py (from {Repo}, --vision {Vision}).",
                adding ? "Adding the image encoder to" : "Installing", family!.Name, size!.Name, name, family.Repo, effective);

            yield return new ModelPullProgress(
                adding
                    ? $"adding pictures to {family.Title} {size.Name} with Strata's setup"
                    : $"installing {family.Title} {size.Name}{(ModelCommand.WantsImages(effective) ? " with pictures" : "")} with Strata's setup",
                null,
                null);

            var tail = new Queue<string>();
            int? exit = null;
            string? lastStatus = null;
            var lastPercent = -1;

            await foreach (var output in runner.RunAsync(StartInfo(options, huggingFace, family.Name, size.Name, effective, adding ? KeptChoices(options, ConfigOf(name)) : null), cancellationToken))
            {
                if (output.ExitCode is { } code)
                {
                    exit = code;
                    break;
                }

                var line = output.Line;

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (DownloadLine().Match(line) is { Success: true } download)
                {
                    var percent = int.Parse(download.Groups["pct"].Value, CultureInfo.InvariantCulture);
                    var status = $"downloading {download.Groups["file"].Value}";

                    // Setup redraws the line a few times a second; the hub's frame is one per percent.
                    if (status == lastStatus && percent == lastPercent)
                    {
                        continue;
                    }

                    if (status != lastStatus || percent % 10 == 0)
                    {
                        logger.LogInformation("Strata setup: {Line}", line);
                    }

                    lastStatus = status;
                    lastPercent = percent;
                    yield return new ModelPullProgress(status, Bytes(download.Groups["total"].Value), Bytes(download.Groups["done"].Value));
                    continue;
                }

                logger.LogInformation("Strata setup: {Line}", line);
                Remember(tail, line);

                if (StepLine().Match(line) is { Success: true } step)
                {
                    lastStatus = $"step {step.Groups["n"].Value}: {step.Groups["title"].Value}";
                    lastPercent = -1;
                    yield return new ModelPullProgress(lastStatus, null, null);
                }
            }

            if (exit is not 0)
            {
                throw new InvalidOperationException(
                    $"Strata's setup.py exited with code {exit?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}: {string.Join(" | ", tail)}");
            }

            if (!catalog.CatalogNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Strata's setup.py finished, and there is no {name}.json in {options.ResolvedConfigDir()} (Strata:ConfigDir): {string.Join(" | ", tail)}");
            }

            var images = ImagesOf(name) == true;

            if (ModelCommand.WantsImages(effective) && !images)
            {
                throw new InvalidOperationException(
                    $"Strata's setup.py finished, and {name}.json has no image encoder (--vision {effective} was asked for): {string.Join(" | ", tail)}");
            }

            logger.LogInformation("Strata '{Name}' is installed and listed (pictures: {Images}).", name, images ? "yes" : "no");
            catalog.Installed();
            yield return new ModelPullProgress($"installed as '{name}'{(images ? ", with pictures" : "")}", null, null);
        }
        finally
        {
            one.Release();
        }
    }

    /// <summary>
    /// The sentence that ends an install before setup runs: the size is there, and nothing asks for
    /// pictures it lacks. Null: setup has work to do.
    /// </summary>
    private string? Settled(string name, string? vision)
    {
        if (!catalog.CatalogNames.Contains(name, StringComparer.OrdinalIgnoreCase) || AddingPictures(name, vision))
        {
            return null;
        }

        return ModelCommand.WantsImages(vision)
            ? $"'{name}' is already installed, with pictures"
            : $"'{name}' is already installed";
    }

    /// <summary>
    /// An explicit ask for pictures on a size installed without them. Only an explicit one: a node whose
    /// <c>Strata:Install:Vision</c> is <c>yes</c> does not re-run setup for every plain install request.
    /// </summary>
    private bool AddingPictures(string name, string? vision) =>
        ModelCommand.WantsImages(vision)
        && catalog.CatalogNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        && ImagesOf(name) == false;

    private bool? ImagesOf(string name) =>
        ConfigOf(name) is { } path ? catalog.ImagesOf(path) : null;

    private string? ConfigOf(string name) => catalog.Scan().TryGetValue(name, out var path) ? path : null;

    /// <summary>
    /// The installed config's own <c>--context</c> and <c>--kv</c>, as setup flags — what setup itself
    /// reads back from a config when it adopts one (its <c>choices_from_config</c>) — minus whatever
    /// <c>Strata:Install</c> sets, which wins.
    /// </summary>
    internal static IReadOnlyList<string> KeptChoices(StrataOptions options, string? config)
    {
        if (config is null)
        {
            return [];
        }

        List<string> args;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(config));

            if (!document.RootElement.TryGetProperty("args", out var list) || list.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return [];
            }

            args = list.EnumerateArray().Select(a => a.ValueKind == System.Text.Json.JsonValueKind.String ? a.GetString() ?? "" : "").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }

        string? Value(string flag) => args.IndexOf(flag) is var i and >= 0 && i + 1 < args.Count ? args[i + 1] : null;
        bool Named(string flag) => options.Install.Arguments.Any(a => a.Trim().Split('=')[0].Equals(flag, StringComparison.OrdinalIgnoreCase));

        var kept = new List<string>();

        if (options.Install.Context is null && !Named("--context")
            && int.TryParse(Value("--max-context"), NumberStyles.None, CultureInfo.InvariantCulture, out var context) && context > 0)
        {
            kept.AddRange(["--context", context.ToString(CultureInfo.InvariantCulture)]);
        }

        if (!Named("--kv") && Value("--kv") is { } kv && kv is "int8" or "q4_0" or "k8v4")
        {
            kept.AddRange(["--kv", kv]);
        }

        return kept;
    }

    private bool Loaded(string name) =>
        catalog.State(string.Empty).Models.Any(m =>
            string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)
            && m.State is NodeCatalogModel.Loaded or NodeCatalogModel.Loading);

    /// <summary>The whole <c>setup.py</c> command line, pure, so the tests can read it.</summary>
    /// <param name="vision">setup's <c>--vision</c> for this install; null is <c>Strata:Install:Vision</c>.</param>
    /// <param name="kept">An installed config's own answers (<see cref="KeptChoices"/>) when setup runs again for it.</param>
    public static ProcessStartInfo StartInfo(StrataOptions options, HuggingFaceOptions huggingFace, string family, string size, string? vision = null, IReadOnlyList<string>? kept = null)
    {
        var root = options.Root!.Trim();

        var info = new ProcessStartInfo(options.ResolvedPython())
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var arguments = new List<string>
        {
            Path.Combine(root, "setup.py"),
            "--setup", "--yes", "--no-start", "--no-browser",
            "--family", family,
            "--model", size,
            "--host", "127.0.0.1",
            "--vision", vision ?? options.Install.Vision
        };

        if (!string.IsNullOrWhiteSpace(options.DataDir))
        {
            arguments.AddRange(["--data-dir", options.DataDir.Trim()]);
        }

        if (options.Install.Context is { } context)
        {
            arguments.AddRange(["--context", context.ToString(CultureInfo.InvariantCulture)]);
        }

        arguments.AddRange(kept ?? []);
        arguments.AddRange(options.Install.Arguments.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()));

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Remove(StrataServe.ContentTeeVariable);
        info.Environment["PYTHONUNBUFFERED"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";

        if (!string.IsNullOrWhiteSpace(huggingFace.Token))
        {
            info.Environment["HF_TOKEN"] = huggingFace.Token.Trim();
        }

        if (!string.Equals(huggingFace.Endpoint.TrimEnd('/'), HuggingFaceOptions.DefaultEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            info.Environment["HF_ENDPOINT"] = huggingFace.Endpoint.TrimEnd('/');
        }

        return info;
    }

    private static long Bytes(string gigabytes) =>
        (long)(double.Parse(gigabytes, CultureInfo.InvariantCulture) * 1_000_000_000d);

    private static void Remember(Queue<string> tail, string line)
    {
        tail.Enqueue(line);

        while (tail.Count > TailLines)
        {
            tail.Dequeue();
        }
    }
}

/// <summary>
/// The shipped runner: <c>setup.py</c> as a child in the node's Job Object, killed with its tree on
/// cancel. It reads characters rather than lines, because setup draws its download bar with
/// <c>\r</c> alone — a line reader sees a 30 GB file as one line when it finishes.
/// </summary>
public sealed partial class StrataSetupRunner(ILogger logger) : IStrataSetupRunner
{
    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex Ansi();

    public async IAsyncEnumerable<SetupOutput> RunAsync(ProcessStartInfo info, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned no process.");

        try
        {
            ChildProcessJob.Assign(process);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Strata's setup is not tied to this node's lifetime.");
        }

        var lines = Channel.CreateUnbounded<string>();
        var pumps = Task.WhenAll(PumpAsync(process.StandardOutput, lines.Writer), PumpAsync(process.StandardError, lines.Writer));
        _ = pumps.ContinueWith(_ => lines.Writer.TryComplete(), TaskScheduler.Default);

        try
        {
            await foreach (var line in lines.Reader.ReadAllAsync(cancellationToken))
            {
                yield return new SetupOutput(line);
            }

            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        yield return new SetupOutput(null, process.ExitCode);
    }

    private static async Task PumpAsync(StreamReader reader, ChannelWriter<string> lines)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();

        while (true)
        {
            var read = await reader.ReadAsync(buffer);

            if (read == 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                if (buffer[i] is '\r' or '\n')
                {
                    Flush(line, lines);
                }
                else
                {
                    line.Append(buffer[i]);
                }
            }
        }

        Flush(line, lines);
    }

    private static void Flush(StringBuilder line, ChannelWriter<string> lines)
    {
        var text = Ansi().Replace(line.ToString(), string.Empty).Trim();
        line.Clear();

        if (text.Length > 0)
        {
            lines.TryWrite(text);
        }
    }
}
