using System.Security.Cryptography;
using System.Text;

namespace InferHub.Coordinator.Vector.Qdrant;

/// <summary>
/// Maps an arbitrary InferHub record id (a SHA-256 chunk id, a filename, a GUID, a caller-supplied
/// string) onto a Qdrant point id.
/// <para>
/// Qdrant accepts <b>only</b> an unsigned integer or a UUID as a point id — InferHub ids are
/// neither. So the point id is a deterministic <c>UUIDv5</c> of the real id, and the real id itself
/// rides along in the point payload (<c>__id</c>), which is what every read returns. Determinism is
/// the load-bearing property: re-upserting the same real id addresses the same point and therefore
/// <i>replaces</i> rather than duplicating — the idempotent re-ingest promise (phase 23 D5) survives
/// the mapping. Nothing downstream ever sees the UUID.
/// </para>
/// </summary>
internal static class QdrantIdMap
{
    // A fixed namespace GUID, in RFC-4122 network byte order (most-significant byte first). Constant
    // forever: change it and every existing point id shifts, orphaning every stored collection.
    private static readonly byte[] Namespace =
    [
        0x9e, 0x3f, 0x8c, 0x1a, 0x6b, 0x2d, 0x4e, 0x7f,
        0xa1, 0xc5, 0x0d, 0x2b, 0x4e, 0x6a, 0x8f, 0x10
    ];

    /// <summary>
    /// The Qdrant point id (a canonical UUIDv5 string) for a real InferHub id. Same input → same
    /// output, always; the SHA-1 collision surface over distinct ids is astronomically small.
    /// </summary>
    public static string ToPointId(string realId)
    {
        var name = Encoding.UTF8.GetBytes(realId);
        var input = new byte[Namespace.Length + name.Length];
        Buffer.BlockCopy(Namespace, 0, input, 0, Namespace.Length);
        Buffer.BlockCopy(name, 0, input, Namespace.Length, name.Length);

        var hash = SHA1.HashData(input);

        // Take the first 16 bytes and stamp version (5) and the RFC-4122 variant.
        Span<byte> uuid = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(uuid);
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50); // version 5
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80); // variant 10xx

        return Format(uuid);
    }

    private static string Format(ReadOnlySpan<byte> uuid)
    {
        Span<char> chars = stackalloc char[36];
        var pos = 0;
        for (var i = 0; i < 16; i++)
        {
            if (i is 4 or 6 or 8 or 10)
            {
                chars[pos++] = '-';
            }
            var b = uuid[i];
            chars[pos++] = HexLower(b >> 4);
            chars[pos++] = HexLower(b & 0x0F);
        }
        return new string(chars);
    }

    private static char HexLower(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'a' + (nibble - 10));
}
