using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace InferHub.Node.Backends;

/// <summary>
/// The <c>llama-server</c> command line a <c>llamacpp</c> engine is launched with (phase 95, D3) — one
/// GGUF behind <c>-m</c>, or since phase 96 a router over a directory and a preset INI (96 D1).
/// Pure, so the tests read it without a binary.
/// </summary>
public static partial class LlamaCppServe
{
    /// <summary>
    /// Flags that make <c>llama-server</c> log prompts or generated text. Rule 7: no prompt reaches
    /// a log on any host at any level, and the node logs this process's output — so they are refused
    /// at startup by name rather than filtered line by line afterwards.
    /// </summary>
    public static readonly string[] ContentLoggingFlags = ["-v", "--verbose", "--log-verbose", "-lv", "--verbosity", "--log-verbosity"];

    /// <summary>
    /// Preset keys the node owns or rule 7 forbids. <c>host</c>/<c>port</c>/<c>alias</c> are the
    /// router's to give each child, and the section name is the alias.
    /// </summary>
    public static readonly string[] RefusedPresetKeys =
        ["verbose", "log-verbose", "verbosity", "log-verbosity", "host", "port", "alias", "model", "hf-repo", "mmproj", "embeddings", "reranking", "api-key", "api-key-file"];

    /// <summary>A preset name is a model name and an INI section: a plain token.</summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")]
    public static partial Regex PresetName();

    /// <summary>A llama.cpp long option without its dashes.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    public static partial Regex SettingKey();

    /// <summary><c>owner/repo</c> with an optional <c>:quant</c> — what <c>-hf</c> and <c>POST /models</c> take.</summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*(:[A-Za-z0-9_.-]+)?$")]
    public static partial Regex HfRepo();

    /// <param name="presetPath">The INI <see cref="WritePresets"/> wrote; null when there are no presets.</param>
    public static ProcessStartInfo StartInfo(EngineOptions engine, string? presetPath = null)
    {
        var serve = engine.Serve;

        var info = new ProcessStartInfo(serve.Executable.Trim())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (serve.IsSingle)
        {
            info.ArgumentList.Add("-m");
            info.ArgumentList.Add(serve.Model!.Trim());
            info.ArgumentList.Add("--alias");
            info.ArgumentList.Add(serve.ResolvedAlias());
        }
        else
        {
            // 96 D1: no -m is what makes llama-server a router.
            if (!string.IsNullOrWhiteSpace(serve.ModelsDir))
            {
                info.ArgumentList.Add("--models-dir");
                info.ArgumentList.Add(serve.ModelsDir.Trim());
            }

            if (presetPath is not null)
            {
                info.ArgumentList.Add("--models-preset");
                info.ArgumentList.Add(presetPath);
            }

            if (serve.MaxLoaded is { } max)
            {
                info.ArgumentList.Add("--models-max");
                info.ArgumentList.Add(max.ToString(CultureInfo.InvariantCulture));
            }
        }

        // Loopback only: the node is the thing in front of it, and a launched engine bound to every
        // interface would be a second, unauthenticated API on the box (93 D5's choice for colibri).
        info.ArgumentList.Add("--host");
        info.ArgumentList.Add("127.0.0.1");
        info.ArgumentList.Add("--port");
        info.ArgumentList.Add(serve.Port.ToString(CultureInfo.InvariantCulture));

        // On a router these would reach every child; per model, they are preset keys (96 D2).
        if (serve.IsSingle && engine.Embeddings)
        {
            info.ArgumentList.Add("--embeddings");
        }

        if (serve.IsSingle && engine.Reranking)
        {
            info.ArgumentList.Add("--reranking");
        }

        foreach (var argument in serve.Arguments.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            info.ArgumentList.Add(argument.Trim());
        }

        return info;
    }

    /// <summary>The INI <c>--models-preset</c> reads, one section per preset, names sorted.</summary>
    public static string PresetIni(EngineOptions engine)
    {
        var ini = new StringBuilder();

        foreach (var (name, preset) in engine.Serve.Presets.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            ini.Append('[').Append(name.Trim()).Append("]\n");
            Line(ini, "model", preset.Model);
            Line(ini, "hf-repo", preset.HfRepo);
            Line(ini, "mmproj", preset.Mmproj);

            if (preset.Embeddings)
            {
                Line(ini, "embeddings", "true");
            }

            if (preset.Reranking)
            {
                Line(ini, "reranking", "true");
            }

            foreach (var (key, value) in preset.Settings.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                Line(ini, key.Trim().ToLowerInvariant(), value);
            }

            ini.Append('\n');
        }

        return ini.ToString();
    }

    /// <summary>
    /// Writes <see cref="PresetIni"/> where the node keeps its own scratch, fresh on every launch so an
    /// edited configuration is what the next process reads. Null when there are no presets.
    /// </summary>
    public static string? WritePresets(string engineName, EngineOptions engine)
    {
        if (engine.Serve.Presets.Count == 0)
        {
            return null;
        }

        var directory = Path.Combine(Path.GetTempPath(), "inferhub-llamacpp");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{engineName}-{Environment.ProcessId}.ini");
        File.WriteAllText(path, PresetIni(engine), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>Null when it can be launched; otherwise the sentence the engine reports as <c>failed</c>.</summary>
    public static string? Precondition(EngineOptions engine)
    {
        var serve = engine.Serve;

        if (serve.IsSingle)
        {
            return File.Exists(serve.Model!.Trim())
                ? null
                : $"Serve:Model '{serve.Model}' is not a file this node can see; mount the GGUF there.";
        }

        if (!string.IsNullOrWhiteSpace(serve.ModelsDir) && !Directory.Exists(serve.ModelsDir.Trim()))
        {
            return $"Serve:ModelsDir '{serve.ModelsDir}' is not a directory this node can see; mount the GGUFs there.";
        }

        foreach (var (name, preset) in serve.Presets)
        {
            foreach (var (key, path) in new[] { ("Model", preset.Model), ("Mmproj", preset.Mmproj) })
            {
                if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path.Trim()))
                {
                    return $"Serve:Presets:{name}:{key} '{path}' is not a file this node can see.";
                }
            }
        }

        return null;
    }

    private static void Line(StringBuilder ini, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            ini.Append(key).Append(" = ").Append(value.Trim()).Append('\n');
        }
    }
}
