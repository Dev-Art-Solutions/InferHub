using System.Text.Json.Serialization;

namespace InferHub.Shared.Fit;

/// <summary>
/// Phase 143 D1: a member of a group fills its own worker's cache from the model file on its own disk, so the head sends it
/// hashes instead of tensors. <see cref="Weights"/> is the group's <c>--tensor-split</c> in the head's order (the head's own
/// worker first) and <see cref="Index"/> this member's place in it — what <see cref="ShardLayers.Split"/> needs to say which
/// layers it will hold. <see cref="Key"/> is the hub's name for this exact assignment; the member answers under it.
/// </summary>
public sealed record ShardSeedSpec(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("weights")] IReadOnlyList<long> Weights,
    [property: JsonPropertyName("index")] int Index);

/// <summary>The hub's list for one member. Replaces the previous one whole; an assignment not in it is abandoned.</summary>
public sealed record ShardSeedList(
    [property: JsonPropertyName("seeds")] IReadOnlyList<ShardSeedSpec> Seeds);

/// <summary>
/// What a member says about one assignment: <see cref="Seeding"/> (reading its file into the cache — the hub holds the group
/// back), <see cref="Ready"/> (its layers are in the cache), <see cref="Absent"/> (it does not hold the file — the head sends
/// them, as before 143) or <see cref="Failed"/> (the sentence says why — the head sends them).
/// </summary>
public sealed record ShardSeedState(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("doneMiB")] long DoneMiB,
    [property: JsonPropertyName("totalMiB")] long TotalMiB,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null)
{
    public const string Seeding = "seeding";
    public const string Ready = "ready";
    public const string Absent = "absent";
    public const string Failed = "failed";
}

/// <summary>
/// Phase 143 D2: the tensors a member's worker will be sent, and the name its cache gives each. llama.cpp's RPC client asks
/// the worker for a weight tensor above 10 MiB by the FNV-1a 64 of its bytes before sending them (<c>SET_TENSOR_HASH</c>,
/// <c>HASH_THRESHOLD</c> in <c>ggml-rpc.cpp</c>, b12182); <c>rpc-server -c</c> answers from <c>&lt;cache&gt;/&lt;hash as 16
/// hex&gt;</c>, a file of exactly those bytes. The bytes are the GGUF's own: the loader sets a whole tensor at once on an RPC
/// device (it reports no host buffer, so there are no chunked uploads).
/// </summary>
public static class ShardSeedPlan
{
    /// <summary><c>HASH_THRESHOLD</c>: a tensor at or under it is always sent, cache or not.</summary>
    public const long HashThreshold = 10 * 1024 * 1024;

    /// <summary>
    /// The tensors of <paramref name="first"/>…<paramref name="first"/>+<paramref name="count"/>−1 (<c>blk.N.*</c>) and, with
    /// <paramref name="output"/>, the output layer's (<c>output.*</c>, <c>output_norm.*</c>) — those above the threshold, with
    /// where their bytes start in the file. A tensor of a type the size table does not know is left out (it is sent, as today).
    /// </summary>
    public static IReadOnlyList<(string Name, long Start, long Bytes)> Tensors(GgufHeader header, int first, int count, bool output, long threshold = HashThreshold)
    {
        if (header.DataStart < 0)
        {
            return [];
        }

        var picked = new List<(string, long, long)>();

        foreach (var tensor in header.Tensors)
        {
            if (tensor.Bytes is not { } bytes || bytes <= threshold || !Holds(tensor.Name, first, count, output))
            {
                continue;
            }

            picked.Add((tensor.Name, header.DataStart + tensor.Offset, bytes));
        }

        return picked;
    }

    /// <summary>Whether a tensor belongs to the layers given — by llama.cpp's names: <c>blk.&lt;il&gt;.</c>, and the output's.</summary>
    public static bool Holds(string name, int first, int count, bool output)
    {
        if (name.StartsWith("blk.", StringComparison.Ordinal))
        {
            var dot = name.IndexOf('.', 4);
            return dot > 4 && int.TryParse(name.AsSpan(4, dot - 4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var il)
                && il >= first && il < first + count;
        }

        return output && (name.StartsWith("output.", StringComparison.Ordinal) || name.StartsWith("output_norm.", StringComparison.Ordinal));
    }

    /// <summary>FNV-1a 64 continued over <paramref name="data"/> — <c>fnv_hash</c> in <c>ggml-rpc.cpp</c>.</summary>
    public static ulong Fnv1a(ReadOnlySpan<byte> data, ulong hash = Fnv1aStart)
    {
        const ulong prime = 0x100000001b3UL;

        foreach (var b in data)
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }

    public const ulong Fnv1aStart = 0xcbf29ce484222325UL;

    /// <summary>The cache's file name for a hash: <c>%016</c><c>PRIx64</c>.</summary>
    public static string FileName(ulong hash) => hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
}
