using System.Buffers.Binary;

namespace InferHub.Shared.CloudMedia;

/// <summary>
/// The few facts about a file this library may read from its header — and no more (46 D6: nothing
/// here decodes a pixel or a sample). A PNG's dimensions, a WAV's length, and which container a
/// file is by its magic bytes.
/// </summary>
internal static class MediaBytes
{
    /// <summary>Width and height from a PNG's IHDR chunk, or null for anything else.</summary>
    public static (int Width, int Height)? PngSize(ReadOnlySpan<byte> bytes)
    {
        // 8-byte signature, 4-byte length, "IHDR", then width and height big-endian.
        if (bytes.Length < 24
            || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47
            || bytes[12] != (byte)'I' || bytes[13] != (byte)'H' || bytes[14] != (byte)'D' || bytes[15] != (byte)'R')
        {
            return null;
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]);
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]);
        return width > 0 && height > 0 ? (width, height) : null;
    }

    /// <summary>
    /// A clip's length from its MP4 <c>moov/mvhd</c> box — the container's own header, read the way
    /// <see cref="PngSize"/> reads a picture's (105 D3). No frame is decoded. Null when the bytes are
    /// not an MP4 this can walk.
    /// </summary>
    public static double? Mp4Seconds(ReadOnlySpan<byte> bytes)
    {
        var position = 0;

        while (position + 8 <= bytes.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(bytes[position..]);
            var header = 8;

            if (size == 1 && position + 16 <= bytes.Length)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(bytes[(position + 8)..]);
                header = 16;
            }
            else if (size == 0)
            {
                size = bytes.Length - position;
            }

            if (size < header || position + size > bytes.Length)
            {
                return null;
            }

            if (IsAscii(bytes, position + 4, "moov"))
            {
                // Descend: mvhd is a direct child of moov.
                var child = position + header;
                var end = position + (int)size;

                while (child + 8 <= end)
                {
                    var childSize = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes[child..]);

                    if (childSize < 8 || child + childSize > end)
                    {
                        return null;
                    }

                    if (IsAscii(bytes, child + 4, "mvhd") && child + 32 <= end)
                    {
                        var version = bytes[child + 8];
                        uint timescale;
                        ulong duration;

                        if (version == 1 && child + 44 <= end)
                        {
                            timescale = BinaryPrimitives.ReadUInt32BigEndian(bytes[(child + 28)..]);
                            duration = BinaryPrimitives.ReadUInt64BigEndian(bytes[(child + 32)..]);
                        }
                        else
                        {
                            timescale = BinaryPrimitives.ReadUInt32BigEndian(bytes[(child + 20)..]);
                            duration = BinaryPrimitives.ReadUInt32BigEndian(bytes[(child + 24)..]);
                        }

                        return timescale > 0 && duration > 0 ? Math.Round((double)duration / timescale, 3) : null;
                    }

                    child += childSize;
                }

                return null;
            }

            position += (int)size;
        }

        return null;
    }

    /// <summary>
    /// Seconds of audio in a canonical PCM WAV, read from its <c>fmt </c> and <c>data</c> chunks.
    /// Null for anything else — a transcript's units are never guessed from a byte count (104 D5).
    /// </summary>
    public static double? WavSeconds(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !IsAscii(bytes, 0, "RIFF") || !IsAscii(bytes, 8, "WAVE"))
        {
            return null;
        }

        var position = 12;
        var byteRate = 0;

        while (position + 8 <= bytes.Length)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes[(position + 4)..(position + 8)]);

            if (size < 0)
            {
                return null;
            }

            if (IsAscii(bytes, position, "fmt ") && position + 20 <= bytes.Length)
            {
                byteRate = BinaryPrimitives.ReadInt32LittleEndian(bytes[(position + 16)..(position + 20)]);
            }
            else if (IsAscii(bytes, position, "data"))
            {
                // A streamed WAV writes 0 or 0xFFFFFFFF here; the bytes that are actually present are the honest length.
                var available = Math.Min((long)(uint)size, bytes.Length - position - 8L);
                return byteRate > 0 ? available / (double)byteRate : null;
            }

            position += 8 + size + (size & 1);
        }

        return null;
    }

    /// <summary>A 44-byte WAV header in front of 16-bit little-endian PCM — the one container written here.</summary>
    public static byte[] WrapPcm16(byte[] pcm, int sampleRate, int channels = 1)
    {
        var wav = new byte[44 + pcm.Length];
        var span = wav.AsSpan();
        var byteRate = sampleRate * channels * 2;

        WriteAscii(span, 0, "RIFF");
        BinaryPrimitives.WriteInt32LittleEndian(span[4..8], 36 + pcm.Length);
        WriteAscii(span, 8, "WAVE");
        WriteAscii(span, 12, "fmt ");
        BinaryPrimitives.WriteInt32LittleEndian(span[16..20], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..22], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..24], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..28], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..32], byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..34], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..36], 16);
        WriteAscii(span, 36, "data");
        BinaryPrimitives.WriteInt32LittleEndian(span[40..44], pcm.Length);
        pcm.CopyTo(span[44..]);
        return wav;
    }

    /// <summary>
    /// The audio container by its magic bytes, as a media type, or the one the caller declared when
    /// the bytes say nothing. Null when neither is an audio type.
    /// </summary>
    public static string? AudioMediaType(ReadOnlySpan<byte> bytes, string? declared)
    {
        if (bytes.Length >= 12 && IsAscii(bytes, 0, "RIFF") && IsAscii(bytes, 8, "WAVE"))
        {
            return "audio/wav";
        }

        if (bytes.Length >= 4 && IsAscii(bytes, 0, "OggS"))
        {
            return "audio/ogg";
        }

        if (bytes.Length >= 4 && IsAscii(bytes, 0, "fLaC"))
        {
            return "audio/flac";
        }

        if (bytes.Length >= 12 && IsAscii(bytes, 0, "FORM") && (IsAscii(bytes, 8, "AIFF") || IsAscii(bytes, 8, "AIFC")))
        {
            return "audio/aiff";
        }

        if (bytes.Length >= 3 && IsAscii(bytes, 0, "ID3"))
        {
            return "audio/mp3";
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF)
        {
            // 0xFFF1/0xFFF9 is ADTS AAC; any other 0xFFEx–0xFFFx sync word is an MPEG audio frame.
            return (bytes[1] & 0xF6) == 0xF0 ? "audio/aac" : (bytes[1] & 0xE0) == 0xE0 ? "audio/mp3" : null;
        }

        var type = declared?.Split(';')[0].Trim().ToLowerInvariant();
        return type is not null && type.StartsWith("audio/", StringComparison.Ordinal) ? type : null;
    }

    /// <summary>A file extension for a media type, for the multipart filename a vendor sniffs.</summary>
    public static string ExtensionFor(string? mediaType) => mediaType?.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
        "audio/mp3" or "audio/mpeg" => "mp3",
        "audio/ogg" => "ogg",
        "audio/flac" => "flac",
        "audio/aac" => "aac",
        "audio/aiff" => "aiff",
        "audio/webm" => "webm",
        "audio/mp4" or "audio/m4a" or "audio/x-m4a" => "m4a",
        "image/jpeg" => "jpg",
        "image/webp" => "webp",
        "image/png" => "png",
        _ => "bin"
    };

    /// <summary>The image container by magic bytes, defaulting to the declared type and then PNG.</summary>
    public static string ImageMediaType(ReadOnlySpan<byte> bytes, string? declared)
    {
        if (PngSize(bytes) is not null)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 12 && IsAscii(bytes, 0, "RIFF") && IsAscii(bytes, 8, "WEBP"))
        {
            return "image/webp";
        }

        return string.IsNullOrWhiteSpace(declared) ? "image/png" : declared.Split(';')[0].Trim();
    }

    private static bool IsAscii(ReadOnlySpan<byte> bytes, int offset, string text)
    {
        if (offset + text.Length > bytes.Length)
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (bytes[offset + i] != (byte)text[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteAscii(Span<byte> span, int offset, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            span[offset + i] = (byte)text[i];
        }
    }
}
