using System.Globalization;

namespace InferHub.Node.Backends;

/// <summary>
/// <c>Backend:Engines:{name}:Serve:Context</c> (phase 153): a long context kept on three tiers — the engine's one slot
/// (VRAM, or RAM with <c>--no-kv-offload</c>), llama.cpp's own prompt cache in RAM (<c>--cache-ram</c>), and slot files
/// on disk (<c>--slot-save-path</c>) that the node writes, restores and evicts by use.
/// </summary>
/// <remarks>
/// Off unless <see cref="Enabled"/>; only for one model behind <c>Serve:Model</c> (a router spreads a model's slots over
/// children the node cannot address). The context size, the KV type and where the experts sit stay the operator's
/// <c>Serve:Arguments</c> (<c>-c</c>, <c>-ctk</c>, <c>--n-cpu-moe</c>, YaRN) — the node adds only the three flags it owns.
/// </remarks>
public sealed class ContextTierOptions
{
    /// <summary>Flags the node writes itself when tiers are on, so they may not also be in <c>Serve:Arguments</c>.</summary>
    public static readonly string[] OwnedFlags = ["-np", "--parallel", "--slot-save-path", "-cram", "--cache-ram", "-kvu", "--kv-unified", "-no-kvu", "--no-kv-unified"];

    public bool Enabled { get; set; }

    /// <summary>The disk tier. Unset: <c>&lt;Node:DataDirectory&gt;/context/&lt;engine&gt;</c>.</summary>
    public string? Directory { get; set; }

    /// <summary>How much the disk tier may hold. A 620k-token ornith state is 6.8 GB.</summary>
    public double DiskMaxGiB { get; set; } = 64;

    /// <summary>llama.cpp's prompt cache in RAM (<c>--cache-ram</c>, MiB). llama.cpp's own default is 8192.</summary>
    public int RamMiB { get; set; } = 8192;

    /// <summary>A prefix shorter than this is cheaper to compute again than to keep on disk.</summary>
    public int MinPrefixTokens { get; set; } = 8192;

    /// <summary>A conversation's prefix is written again only once it has grown by this many tokens since its last file.</summary>
    public int SaveEveryTokens { get; set; } = 8192;

    public long DiskMaxBytes => (long)(DiskMaxGiB * 1024 * 1024 * 1024);

    public string ResolvedDirectory(string engineName, string nodeDataDirectory) =>
        string.IsNullOrWhiteSpace(Directory)
            ? Path.Combine(string.IsNullOrWhiteSpace(nodeDataDirectory) ? "." : nodeDataDirectory, "context", engineName)
            : Directory.Trim();

    /// <summary>The flags tiers add to <c>llama-server</c>.</summary>
    public IEnumerable<string> LaunchArguments(string directory) =>
    [
        // One slot: the node restores into slot 0 and nothing else may be routed there in the meantime.
        "-np", "1",
        "--slot-save-path", directory,
        "--cache-ram", RamMiB.ToString(CultureInfo.InvariantCulture)
    ];

    /// <summary>The configuration's mistakes, each naming its key; empty when it is usable.</summary>
    public IEnumerable<string> Problems(string key, EngineServeOptions serve)
    {
        if (!Enabled)
        {
            yield break;
        }

        if (!serve.IsSingle)
        {
            yield return $"{key}:Serve:Context needs Serve:Model: tiers keep one model's slot, and a router spreads it over children the node cannot address.";
        }

        foreach (var flag in serve.Arguments.Select(a => a.Trim()).Where(a => OwnedFlags.Contains(a, StringComparer.Ordinal)))
        {
            yield return $"{key}:Serve:Arguments has {flag}, which Serve:Context sets itself; remove it.";
        }

        if (DiskMaxGiB <= 0)
        {
            yield return $"{key}:Serve:Context:DiskMaxGiB must be above 0.";
        }

        if (RamMiB < 0)
        {
            yield return $"{key}:Serve:Context:RamMiB must be 0 (no RAM tier) or more.";
        }

        if (MinPrefixTokens < 256 || SaveEveryTokens < 256)
        {
            yield return $"{key}:Serve:Context:MinPrefixTokens and SaveEveryTokens must be at least 256.";
        }
    }
}
