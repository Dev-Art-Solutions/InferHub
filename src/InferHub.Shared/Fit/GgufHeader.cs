using System.Buffers.Binary;
using System.Text;

namespace InferHub.Shared.Fit;

/// <summary>
/// The metadata at the front of a GGUF file (phase 118, D2): its key-value pairs and, after them, where each
/// tensor's data starts — read from a stream that may be only the file's first megabytes (an HTTP Range read), so a
/// model is sized without downloading it.
/// </summary>
/// <remarks>
/// Scalars are kept; strings only when short (a chat template is not a fact we size with); arrays are skipped,
/// except integer arrays of up to 4096 entries — a per-layer <c>head_count_kv</c> is how a hybrid model says which
/// layers have attention at all. Tensor sizes come from the gaps between data offsets, which needs no table of
/// quantization block sizes.
/// </remarks>
public sealed class GgufHeader
{
    private const int MaxKeptString = 256;
    private const int MaxKeptArray = 4096;

    private GgufHeader(uint version, IReadOnlyDictionary<string, object> values, IReadOnlyList<GgufTensor> tensors, long dataStart)
    {
        Version = version;
        Values = values;
        Tensors = tensors;
        DataStart = dataStart;
    }

    public uint Version { get; }

    /// <summary>Scalars as <see cref="long"/>, <see cref="double"/>, <see cref="bool"/> or <see cref="string"/>; kept integer arrays as <c>long[]</c>.</summary>
    public IReadOnlyDictionary<string, object> Values { get; }

    /// <summary>Every tensor's name and data offset (relative to <see cref="DataStart"/>), in file order. Empty when the read ended before them.</summary>
    public IReadOnlyList<GgufTensor> Tensors { get; }

    /// <summary>Where tensor data begins in the file (aligned), or -1 when the tensor list was not reached.</summary>
    public long DataStart { get; }

    public string? Architecture => Text("general.architecture");

    public string? Text(string key) => Values.TryGetValue(key, out var v) ? v as string : null;

    public long? Integer(string key) => Values.TryGetValue(key, out var v) ? v switch
    {
        long l => l,
        double d when d == Math.Floor(d) => (long)d,
        _ => null
    } : null;

    public long[]? Integers(string key) => Values.TryGetValue(key, out var v) ? v as long[] : null;

    /// <summary>
    /// Bytes per tensor from the gaps between offsets; the last one runs to <paramref name="fileBytes"/> (the data of
    /// this file only — a split model's first shard holds some of its tensors).
    /// </summary>
    public IReadOnlyList<(string Name, long Bytes)> TensorBytes(long fileBytes)
    {
        if (DataStart < 0 || Tensors.Count == 0)
        {
            return [];
        }

        var ordered = Tensors.OrderBy(t => t.Offset).ToArray();
        var sizes = new List<(string, long)>(ordered.Length);

        for (var i = 0; i < ordered.Length; i++)
        {
            var end = i + 1 < ordered.Length ? ordered[i + 1].Offset : fileBytes - DataStart;
            sizes.Add((ordered[i].Name, Math.Max(0, end - ordered[i].Offset)));
        }

        return sizes;
    }

    /// <summary>
    /// Reads a header. A stream that ends inside the key-value pairs throws <see cref="EndOfStreamException"/> (read
    /// more); one that ends inside the tensor list returns the pairs with no tensors.
    /// </summary>
    public static async Task<GgufHeader> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var reader = new Reader(stream);

        if (await reader.U32Async(cancellationToken) != 0x46554747) // "GGUF", little-endian
        {
            throw new InvalidDataException("not a GGUF file (no GGUF magic)");
        }

        var version = await reader.U32Async(cancellationToken);

        if (version is < 2 or > 3)
        {
            throw new InvalidDataException($"GGUF version {version} is not one this reader knows (2 or 3)");
        }

        var tensorCount = await reader.U64Async(cancellationToken);
        var kvCount = await reader.U64Async(cancellationToken);

        if (tensorCount > 1_000_000 || kvCount > 100_000)
        {
            throw new InvalidDataException("GGUF header counts are implausible");
        }

        var values = new Dictionary<string, object>(StringComparer.Ordinal);

        for (ulong i = 0; i < kvCount; i++)
        {
            var key = await reader.StringAsync(MaxKeptString * 4, cancellationToken) ?? string.Empty;
            var type = await reader.U32Async(cancellationToken);
            var value = await reader.ValueAsync(type, cancellationToken);

            if (value is not null)
            {
                values[key] = value;
            }
        }

        var tensors = new List<GgufTensor>();
        long dataStart = -1;

        try
        {
            for (ulong i = 0; i < tensorCount; i++)
            {
                var name = await reader.StringAsync(4096, cancellationToken) ?? string.Empty;
                var dims = await reader.U32Async(cancellationToken);

                for (var d = 0; d < dims; d++)
                {
                    await reader.U64Async(cancellationToken);
                }

                await reader.U32Async(cancellationToken); // type
                var offset = (long)await reader.U64Async(cancellationToken);
                tensors.Add(new GgufTensor(name, offset));
            }

            var alignment = values.TryGetValue("general.alignment", out var a) && a is long al && al > 0 ? al : 32;
            dataStart = (reader.Position + alignment - 1) / alignment * alignment;
        }
        catch (EndOfStreamException)
        {
            tensors.Clear();
        }

        return new GgufHeader(version, values, tensors, dataStart);
    }

    private sealed class Reader(Stream stream)
    {
        private readonly byte[] scratch = new byte[8];

        public long Position { get; private set; }

        public async Task<uint> U32Async(CancellationToken ct)
        {
            await FillAsync(4, ct);
            return BinaryPrimitives.ReadUInt32LittleEndian(scratch);
        }

        public async Task<ulong> U64Async(CancellationToken ct)
        {
            await FillAsync(8, ct);
            return BinaryPrimitives.ReadUInt64LittleEndian(scratch);
        }

        /// <summary>A string, or null (skipped) when longer than <paramref name="keep"/> bytes.</summary>
        public async Task<string?> StringAsync(int keep, CancellationToken ct)
        {
            var length = await U64Async(ct);

            if (length > int.MaxValue)
            {
                throw new InvalidDataException("GGUF string length is implausible");
            }

            if ((int)length > keep)
            {
                await SkipAsync((long)length, ct);
                return null;
            }

            var bytes = new byte[(int)length];
            await ExactAsync(bytes, ct);
            return Encoding.UTF8.GetString(bytes);
        }

        public async Task<object?> ValueAsync(uint type, CancellationToken ct)
        {
            switch (type)
            {
                case 0: await FillAsync(1, ct); return (long)scratch[0];
                case 1: await FillAsync(1, ct); return (long)(sbyte)scratch[0];
                case 2: await FillAsync(2, ct); return (long)BinaryPrimitives.ReadUInt16LittleEndian(scratch);
                case 3: await FillAsync(2, ct); return (long)BinaryPrimitives.ReadInt16LittleEndian(scratch);
                case 4: return (long)await U32Async(ct);
                case 5: await FillAsync(4, ct); return (long)BinaryPrimitives.ReadInt32LittleEndian(scratch);
                case 6: await FillAsync(4, ct); return (double)BinaryPrimitives.ReadSingleLittleEndian(scratch);
                case 7: await FillAsync(1, ct); return scratch[0] != 0;
                case 8: return await StringAsync(MaxKeptString, ct);
                case 9: return await ArrayAsync(ct);
                case 10: return (long)await U64Async(ct);
                case 11: await FillAsync(8, ct); return BinaryPrimitives.ReadInt64LittleEndian(scratch);
                case 12: await FillAsync(8, ct); return BinaryPrimitives.ReadDoubleLittleEndian(scratch);
                default: throw new InvalidDataException($"GGUF value type {type} is unknown");
            }
        }

        private async Task<object?> ArrayAsync(CancellationToken ct)
        {
            var type = await U32Async(ct);
            var count = await U64Async(ct);
            var width = type switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => 0 };
            var integer = type is 0 or 1 or 2 or 3 or 4 or 5 or 10 or 11;

            if (integer && count <= MaxKeptArray)
            {
                var kept = new long[(int)count];

                for (var i = 0; i < kept.Length; i++)
                {
                    kept[i] = (long)(await ValueAsync(type, ct))!;
                }

                return kept;
            }

            if (width > 0)
            {
                await SkipAsync((long)(count * (ulong)width), ct);
                return null;
            }

            // Strings (a vocabulary) and nested arrays: walked one at a time.
            for (ulong i = 0; i < count; i++)
            {
                if (type == 8)
                {
                    await SkipAsync((long)await U64Async(ct), ct);
                }
                else
                {
                    await ValueAsync(type, ct);
                }
            }

            return null;
        }

        private Task FillAsync(int count, CancellationToken ct) => ExactAsync(scratch.AsMemory(0, count), ct);

        private async Task ExactAsync(Memory<byte> buffer, CancellationToken ct)
        {
            await stream.ReadExactlyAsync(buffer, ct);
            Position += buffer.Length;
        }

        private async Task SkipAsync(long count, CancellationToken ct)
        {
            var buffer = new byte[(int)Math.Min(count, 1 << 16)];

            while (count > 0)
            {
                var chunk = (int)Math.Min(count, buffer.Length);
                await ExactAsync(buffer.AsMemory(0, chunk), ct);
                count -= chunk;
            }
        }
    }
}

public sealed record GgufTensor(string Name, long Offset);
