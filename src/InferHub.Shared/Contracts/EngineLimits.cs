using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// Hard limits on what one runtime on a node may use (phase 102): every process of that runtime
/// together — all of a node's <c>llama-server</c>s, every <c>coli serve</c>, every Strata
/// <c>server.py</c>, the <c>ollama serve</c> the node started and its runners, the tool workers.
/// Null on any field is "no limit on that".
/// </summary>
/// <remarks>
/// <para>
/// <b>The node's own <c>Node:ResourceLimits:Engines</c> is the ceiling; a profile may only go
/// lower</b> (43 D1). A coordinator sends the same record in <see cref="NodeProfile.Limits"/> and the
/// node takes, per field, the smaller of the two — a value above the node's is refused by name and
/// the node's stays. A field the node leaves unset the coordinator may set to anything, because
/// limiting a runtime the operator did not limit is narrowing.
/// </para>
/// </remarks>
public sealed record EngineLimits(
    /// <summary>Share of the whole machine's CPU, 1–100.</summary>
    [property: JsonPropertyName("cpuPercent")] int? CpuPercent = null,
    /// <summary>How many logical processors the runtime may run on (the first N).</summary>
    [property: JsonPropertyName("cores")] int? Cores = null,
    /// <summary>Memory, in MiB, summed over every process of the runtime.</summary>
    [property: JsonPropertyName("memoryMiB")] int? MemoryMiB = null,
    /// <summary>Dedicated GPU memory, in MiB, summed over every process of the runtime.</summary>
    [property: JsonPropertyName("vramMiB")] int? VramMiB = null,
    /// <summary>The GPU indexes the runtime may see (<c>CUDA_VISIBLE_DEVICES</c>). Empty is "no GPU at all".</summary>
    [property: JsonPropertyName("gpus")] IReadOnlyList<int>? Gpus = null)
{
    public static readonly EngineLimits None = new();

    [JsonIgnore]
    public bool IsEmpty => CpuPercent is null && Cores is null && MemoryMiB is null && VramMiB is null && Gpus is null;
}

/// <summary>The runtimes a limit can name. One group each: every process of that runtime shares the budget.</summary>
public static class EngineLimitGroups
{
    public const string Ollama = "ollama";
    public const string LlamaCpp = "llamacpp";
    public const string Colibri = "colibri";
    public const string Strata = "strata";
    public const string Tools = "tools";

    public static readonly IReadOnlyList<string> All = [Ollama, LlamaCpp, Colibri, Strata, Tools];

    /// <summary>The canonical spelling of <paramref name="name"/>, or null when it names no runtime.</summary>
    public static string? Normalize(string? name)
    {
        var trimmed = name?.Trim();

        if (string.Equals(trimmed, "llama.cpp", StringComparison.OrdinalIgnoreCase))
        {
            return LlamaCpp;
        }

        return All.FirstOrDefault(group => string.Equals(group, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// What a node says about its runtime limits (phase 102): per runtime, its own ceiling, what the
/// coordinator asked for, what is in force, how each one is enforced on this box, and what the
/// runtime is using now. The phase-44 D6 mailbox — the node reports, the hub records and shows it,
/// and the hub can see the node's ceiling but has no way to change it.
/// </summary>
public sealed record NodeLimitsState(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("groups")] IReadOnlyList<EngineLimitsGroupState> Groups,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc);

/// <summary>One runtime's row in <see cref="NodeLimitsState"/>.</summary>
public sealed record EngineLimitsGroupState(
    /// <summary>One of <see cref="EngineLimitGroups"/>.</summary>
    [property: JsonPropertyName("group")] string Group,
    /// <summary>The node's own ceiling (<c>Node:ResourceLimits:Engines:{group}</c>). Read-only to a coordinator.</summary>
    [property: JsonPropertyName("node")] EngineLimits? Node,
    /// <summary>What the coordinator's profile asked for, after the node refused anything above its ceiling.</summary>
    [property: JsonPropertyName("hub")] EngineLimits? Hub,
    /// <summary>The smaller of the two, per field — what this node enforces.</summary>
    [property: JsonPropertyName("effective")] EngineLimits Effective,
    /// <summary>Resource → how it is enforced here (<c>os</c>, <c>watchdog</c>, <c>launch</c>) or <c>none: why</c>.</summary>
    [property: JsonPropertyName("enforcement")] IReadOnlyDictionary<string, string> Enforcement,
    /// <summary>How many processes of this runtime the node is holding to the limits right now.</summary>
    [property: JsonPropertyName("processes")] int Processes,
    [property: JsonPropertyName("cpuPercent")] double? CpuPercent,
    [property: JsonPropertyName("memoryMiB")] long? MemoryMiB,
    [property: JsonPropertyName("vramMiB")] long? VramMiB,
    /// <summary>Times the watchdog stopped this runtime for going over a limit, since the node started.</summary>
    [property: JsonPropertyName("breaches")] int Breaches,
    /// <summary>The last breach, as a sentence naming the limit and the numbers.</summary>
    [property: JsonPropertyName("lastBreach")] string? LastBreach);
