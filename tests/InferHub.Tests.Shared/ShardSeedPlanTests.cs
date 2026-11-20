using System.Text;
using InferHub.Shared.Fit;

namespace InferHub.Tests;

/// <summary>
/// Phase 143 D2: which tensors a member's worker will be sent, with the bytes llama.cpp sends — the exact size by type and
/// shape, never the gap to the next tensor (which carries the file's padding) — and the name its cache gives them.
/// </summary>
public sealed class ShardSeedPlanTests
{
    [Fact]
    public void TheHashIsFnv1a64AsGgmlRpcComputesIt()
    {
        // The published FNV-1a 64 vectors.
        Assert.Equal(0xcbf29ce484222325UL, ShardSeedPlan.Fnv1a([]));
        Assert.Equal(0xaf63dc4c8601ec8cUL, ShardSeedPlan.Fnv1a("a"u8));
        Assert.Equal(0x85944171f73967e8UL, ShardSeedPlan.Fnv1a("foobar"u8));

        // Continued over pieces it is the same as over the whole — the seeder hashes what it copies, buffer by buffer.
        var whole = ShardSeedPlan.Fnv1a("foobar"u8);
        Assert.Equal(whole, ShardSeedPlan.Fnv1a("bar"u8, ShardSeedPlan.Fnv1a("foo"u8)));

        // %016 PRIx64: lower case, zero-padded.
        Assert.Equal("00000000000000ff", ShardSeedPlan.FileName(0xff));
        Assert.Equal("85944171f73967e8", ShardSeedPlan.FileName(whole));
    }

    [Theory]
    [InlineData(12u, new long[] { 4096, 4096 }, 4096L / 256 * 144 * 4096)] // Q4_K
    [InlineData(14u, new long[] { 5120, 13824 }, 5120L / 256 * 210 * 13824)] // Q6_K, a row not a multiple of 32 bytes
    [InlineData(8u, new long[] { 4096, 1024 }, 4096L / 32 * 34 * 1024)] // Q8_0
    [InlineData(0u, new long[] { 10 }, 40L)] // F32
    [InlineData(30u, new long[] { 8, 2, 3 }, 96L)] // BF16, three dimensions
    public void ATensorsBytesAreItsRowsOfBlocks(uint type, long[] dims, long bytes) =>
        Assert.Equal(bytes, GgmlTypes.Bytes(type, dims));

    [Fact]
    public void ATypeTheTableDoesNotKnowOrARowThatSplitsABlockHasNoSize()
    {
        Assert.Null(GgmlTypes.Bytes(4, [256])); // Q4_2, removed from ggml
        Assert.Null(GgmlTypes.Bytes(12, [100])); // Q4_K needs whole blocks of 256
        Assert.Null(GgmlTypes.Bytes(0, []));
    }

    [Theory]
    [InlineData("blk.12.attn_q.weight", 10, 5, false, true)]
    [InlineData("blk.15.ffn_down.weight", 10, 5, false, false)]
    [InlineData("blk.9.ffn_down.weight", 10, 5, false, false)]
    [InlineData("blk.1x.attn_q.weight", 0, 99, false, false)]
    [InlineData("output.weight", 10, 5, true, true)]
    [InlineData("output_norm.weight", 10, 5, true, true)]
    [InlineData("output.weight", 10, 5, false, false)]
    [InlineData("token_embd.weight", 0, 99, true, false)] // stays on the head's CPU, never on a worker
    public void ATensorBelongsToItsLayerByName(string name, int first, int count, bool output, bool holds) =>
        Assert.Equal(holds, ShardSeedPlan.Holds(name, first, count, output));

    [Fact]
    public async Task ThePlanTakesTheLayersTensorsAboveTheThresholdWithWhereTheirBytesStart()
    {
        var data = new Dictionary<string, byte[]>
        {
            ["token_embd.weight"] = Fill(96, 1),
            ["blk.0.attn_q.weight"] = Fill(68, 2), // 68: not a multiple of the alignment, so the gap is 96
            ["blk.1.attn_q.weight"] = Fill(68, 3),
            ["blk.1.attn_norm.weight"] = Fill(8, 4), // under the threshold: always sent
            ["output.weight"] = Fill(64, 5)
        };
        var bytes = GgufWriter.WithData([("general.architecture", "llama"), ("llama.block_count", 2L)], data.Select(d => (d.Key, d.Value)));
        var header = await GgufHeader.ReadAsync(new MemoryStream(bytes));

        var plan = ShardSeedPlan.Tensors(header, first: 1, count: 1, output: true, threshold: 16);

        Assert.Equal(["blk.1.attn_q.weight", "output.weight"], plan.Select(p => p.Name));
        Assert.All(plan, p => Assert.Equal(data[p.Name], bytes.AsSpan((int)p.Start, (int)p.Bytes).ToArray()));
        Assert.Equal(68, plan[0].Bytes);
        Assert.Equal((uint)0, header.Tensors[0].Type);
        Assert.Equal([17L], header.Tensors[1].Dims);
    }

    private static byte[] Fill(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
