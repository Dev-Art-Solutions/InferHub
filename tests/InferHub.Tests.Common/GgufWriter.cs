using System.Text;

namespace InferHub.Tests;

/// <summary>
/// Phase 118. Writes a GGUF file the way llama.cpp's writer lays one out — header, key-value pairs, tensor infos,
/// aligned data — with tensors of chosen byte sizes (zeros), so the header reader and the estimate are tested on
/// the format rather than on a mock of it.
/// </summary>
internal static class GgufWriter
{
    /// <param name="values">string, long, int, bool, double, long[] (as int32s), string[] (a vocabulary).</param>
    public static byte[] Build(IEnumerable<(string Key, object Value)> values, IEnumerable<(string Name, long Bytes)>? tensors = null, int alignment = 32)
    {
        var kv = values.ToArray();
        var list = (tensors ?? []).ToArray();
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        w.Write(0x46554747u);
        w.Write(3u);
        w.Write((ulong)list.Length);
        w.Write((ulong)kv.Length);

        foreach (var (key, value) in kv)
        {
            String(w, key);

            switch (value)
            {
                case string s: w.Write(8u); String(w, s); break;
                case bool b: w.Write(7u); w.Write((byte)(b ? 1 : 0)); break;
                case double d: w.Write(6u); w.Write((float)d); break;
                case int i: w.Write(4u); w.Write((uint)i); break;
                case long l when l is >= 0 and <= uint.MaxValue: w.Write(4u); w.Write((uint)l); break;
                case long l: w.Write(10u); w.Write((ulong)l); break;
                case long[] a:
                    w.Write(9u); w.Write(5u); w.Write((ulong)a.Length);
                    foreach (var x in a) { w.Write((int)x); }
                    break;
                case string[] a:
                    w.Write(9u); w.Write(8u); w.Write((ulong)a.Length);
                    foreach (var x in a) { String(w, x); }
                    break;
                default: throw new ArgumentException($"no GGUF type for {value.GetType()}");
            }
        }

        long offset = 0;

        foreach (var (name, bytes) in list)
        {
            String(w, name);
            w.Write(1u);
            w.Write((ulong)Math.Max(1, bytes));
            w.Write(0u); // F32 — the reader never looks at the type
            w.Write((ulong)offset);
            offset += (bytes + alignment - 1) / alignment * alignment;
        }

        w.Flush();
        var pad = (alignment - stream.Length % alignment) % alignment;
        w.Write(new byte[pad]);
        w.Write(new byte[offset]);
        w.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Phase 143: a GGUF whose tensors carry real bytes and their true shape — F32, <c>ne[0]</c> = bytes / 4 — so a test can
    /// check what was read out of the file, byte for byte. <paramref name="alignment"/> pads between tensors as llama.cpp does.
    /// </summary>
    public static byte[] WithData(IEnumerable<(string Key, object Value)> values, IEnumerable<(string Name, byte[] Data)> tensors, int alignment = 32)
    {
        var list = tensors.ToArray();
        // Build() with no tensors and no padding is magic, version, a tensor count of 0, then the pairs: take it with the count.
        var pairs = Build(values, alignment: 1);
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(pairs.AsSpan(0, 8));
        w.Write((ulong)list.Length);
        w.Write(pairs.AsSpan(16, pairs.Length - 16));

        long offset = 0;

        foreach (var (name, data) in list)
        {
            if (data.Length % 4 != 0)
            {
                throw new ArgumentException("F32 data is a multiple of 4 bytes", nameof(tensors));
            }

            String(w, name);
            w.Write(1u);
            w.Write((ulong)(data.Length / 4));
            w.Write(0u);
            w.Write((ulong)offset);
            offset += (data.Length + alignment - 1) / alignment * alignment;
        }

        w.Flush();
        w.Write(new byte[(alignment - stream.Length % alignment) % alignment]);

        foreach (var (_, data) in list)
        {
            w.Write(data);
            w.Write(new byte[(alignment - data.Length % alignment) % alignment]);
        }

        w.Flush();
        return stream.ToArray();
    }

    private static void String(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        w.Write((ulong)bytes.Length);
        w.Write(bytes);
    }

    /// <summary>A small llama-shaped model: <paramref name="layers"/> blocks, GQA, optionally a MoE with experts.</summary>
    public static byte[] Llama(string arch = "llama", int layers = 4, int heads = 8, int kvHeads = 2, int keyLength = 64, int context = 4096, int experts = 0, long denseBytes = 4096, long expertBytes = 0, string[]? vocabulary = null)
    {
        var values = new List<(string, object)>
        {
            ("general.architecture", arch),
            ("general.alignment", 32),
            ($"{arch}.block_count", layers),
            ($"{arch}.context_length", context),
            ($"{arch}.embedding_length", heads * keyLength),
            ($"{arch}.attention.head_count", heads),
            ($"{arch}.attention.head_count_kv", kvHeads),
            ($"{arch}.attention.key_length", keyLength),
            ($"{arch}.attention.value_length", keyLength)
        };

        if (vocabulary is not null)
        {
            values.Add(("tokenizer.ggml.tokens", vocabulary));
        }

        if (experts > 0)
        {
            values.Add(($"{arch}.expert_count", experts));
            values.Add(($"{arch}.expert_used_count", Math.Max(1, experts / 8)));
        }

        var tensors = new List<(string, long)> { ("token_embd.weight", denseBytes) };

        for (var i = 0; i < layers; i++)
        {
            tensors.Add(($"blk.{i}.attn_norm.weight", 32));
            tensors.Add(($"blk.{i}.attn_k.weight", 64));

            if (experts > 0)
            {
                tensors.Add(($"blk.{i}.ffn_up_exps.weight", expertBytes));
            }
        }

        return Build(values, tensors);
    }
}
