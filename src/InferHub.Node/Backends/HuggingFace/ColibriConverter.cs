using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using InferHub.Node.Backends.Colibri;

namespace InferHub.Node.Backends.HuggingFace;

/// <summary>Runs a conversion and streams what it says; a seam so the store is tested without torch.</summary>
public interface IColibriConverter
{
    /// <summary>Lines of progress; throws with the tail of the output when the converter fails.</summary>
    IAsyncEnumerable<string> ConvertAsync(string repoId, string outputDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// <c>coli convert --repo &lt;owner/repo&gt; --model &lt;dir&gt;</c> (98 D3). colibri picks the converter
/// for the checkpoint's family, downloads the shards itself through <c>huggingface_hub</c> (which
/// reads <c>HF_TOKEN</c> and <c>HF_ENDPOINT</c>) and writes its own format; nothing else here
/// knows that format.
/// </summary>
public sealed class ColibriProcessConverter(ColibriOptions colibri, HuggingFaceOptions options, string cacheDirectory, ILogger logger)
    : IColibriConverter
{
    private const int TailLines = 15;

    /// <summary>
    /// colibri's own converter environment when it is there (<c>&lt;launcher dir&gt;/mio_env</c>, where
    /// <c>coli</c> itself looks), else <c>Serve:Python</c>. Found live: run under the image's bare
    /// <c>python3</c>, <c>coli convert</c> could not import <c>huggingface_hub</c> to read the
    /// checkpoint's family, fell back to GLM-5.2's converter, and refused an OLMoE.
    /// </summary>
    public static string Interpreter(ColibriOptions colibri)
    {
        var launcher = colibri.Serve.Launcher;
        var directory = Path.IsPathRooted(launcher) ? Path.GetDirectoryName(launcher) : null;

        if (directory is not null)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(directory, "mio_env", "bin", "python3"),
                         Path.Combine(directory, "mio_env", "Scripts", "python.exe")
                     })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return colibri.Serve.Python;
    }

    public static ProcessStartInfo StartInfo(ColibriOptions colibri, HuggingFaceOptions options, string cacheDirectory, string repoId, string outputDirectory)
    {
        var info = new ProcessStartInfo(Interpreter(colibri))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in new[] { colibri.Serve.Launcher, "convert", "--repo", repoId, "--model", outputDirectory })
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Remove(ColibriServe.ContentTeeVariable);
        info.Environment["HF_HOME"] = cacheDirectory;
        info.Environment["PYTHONUNBUFFERED"] = "1";
        info.Environment["HF_HUB_DISABLE_PROGRESS_BARS"] = "1";

        if (!string.IsNullOrWhiteSpace(options.Token))
        {
            info.Environment["HF_TOKEN"] = options.Token.Trim();
        }

        if (!string.Equals(options.Endpoint.TrimEnd('/'), HuggingFaceOptions.DefaultEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            info.Environment["HF_ENDPOINT"] = options.Endpoint.TrimEnd('/');
        }

        return info;
    }

    public async IAsyncEnumerable<string> ConvertAsync(
        string repoId,
        string outputDirectory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheDirectory);

        var info = StartInfo(colibri, options, cacheDirectory, repoId, outputDirectory);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned no process.");

        try
        {
            ChildProcessJob.Assign(process);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "The converter is not tied to this node's lifetime.");
        }

        logger.LogInformation("Converting '{Repo}' for colibri into {Directory} (pid {Pid}).", repoId, outputDirectory, process.Id);

        var lines = Channel.CreateUnbounded<string>();
        var tail = new Queue<string>();

        void Line(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            // ANSI colours and carriage-return progress bars are the terminal's, not ours.
            var clean = System.Text.RegularExpressions.Regex.Replace(text, @"\x1B\[[0-9;]*[A-Za-z]", string.Empty).Split('\r')[^1].Trim();

            if (clean.Length == 0)
            {
                return;
            }

            lock (tail)
            {
                tail.Enqueue(clean);

                while (tail.Count > TailLines)
                {
                    tail.Dequeue();
                }
            }

            logger.LogInformation("coli convert: {Line}", clean);
            lines.Writer.TryWrite(clean);
        }

        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _ = process.WaitForExitAsync(CancellationToken.None).ContinueWith(_ => lines.Writer.TryComplete(), TaskScheduler.Default);

        try
        {
            await foreach (var line in lines.Reader.ReadAllAsync(cancellationToken))
            {
                yield return line;
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

        if (process.ExitCode != 0)
        {
            string said;

            lock (tail)
            {
                said = string.Join(" | ", tail.TakeLast(4));
            }

            throw new InvalidOperationException($"coli convert exited with code {process.ExitCode}: {said}");
        }
    }
}
