using System.Diagnostics;
using System.Globalization;

namespace InferHub.Node.Backends;

/// <summary>
/// The <c>llama-server</c> command line a <c>llamacpp</c> engine is launched with (phase 95, D3).
/// Pure, so the tests read it without a binary.
/// </summary>
public static class LlamaCppServe
{
    /// <summary>
    /// Flags that make <c>llama-server</c> log prompts or generated text. Rule 7: no prompt reaches
    /// a log on any host at any level, and the node logs this process's output — so they are refused
    /// at startup by name rather than filtered line by line afterwards.
    /// </summary>
    public static readonly string[] ContentLoggingFlags = ["-v", "--verbose", "--log-verbose", "-lv", "--verbosity", "--log-verbosity"];

    public static ProcessStartInfo StartInfo(EngineOptions engine)
    {
        var serve = engine.Serve;

        var info = new ProcessStartInfo(serve.Executable.Trim())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        info.ArgumentList.Add("-m");
        info.ArgumentList.Add(serve.Model!.Trim());
        info.ArgumentList.Add("--alias");
        info.ArgumentList.Add(serve.ResolvedAlias());

        // Loopback only: the node is the thing in front of it, and a launched engine bound to every
        // interface would be a second, unauthenticated API on the box (93 D5's choice for colibri).
        info.ArgumentList.Add("--host");
        info.ArgumentList.Add("127.0.0.1");
        info.ArgumentList.Add("--port");
        info.ArgumentList.Add(serve.Port.ToString(CultureInfo.InvariantCulture));

        if (engine.Embeddings)
        {
            info.ArgumentList.Add("--embeddings");
        }

        foreach (var argument in serve.Arguments.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            info.ArgumentList.Add(argument.Trim());
        }

        return info;
    }

    /// <summary>Null when it can be launched; otherwise the sentence the engine reports as <c>failed</c>.</summary>
    public static string? Precondition(EngineOptions engine)
        => File.Exists(engine.Serve.Model!.Trim())
            ? null
            : $"Serve:Model '{engine.Serve.Model}' is not a file this node can see; mount the GGUF there.";
}
