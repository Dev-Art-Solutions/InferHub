namespace InferHub.Shared.Upstream;

/// <summary>
/// The media type of a bare base64 image, read from its magic bytes. Phase 29 wrote this for the
/// OpenAI dialect's data URLs; phase 63 moved it here because Anthropic's
/// <c>{"type":"image","source":{"type":"base64","media_type":…}}</c> needs the same answer.
/// </summary>
/// <remarks>
/// Ollama's <c>images</c> are base64 with no media type attached, and every upstream wants one. It
/// is sniffed rather than defaulted because an upstream handed <c>image/png</c> for a JPEG produces
/// a failure that looks like a bad model answer — the class of quiet wrongness this codebase spends
/// errors to avoid. Two callers, one copy (52 D2 applied to code): the alternative was the same
/// four signatures in two dialects, diverging the first time a fifth format arrived.
/// </remarks>
public static class Base64MediaType
{
    // Not `u8` literals: 0x89 and 0xFF are not ASCII, and a UTF-8 literal would encode them as
    // two bytes each — a signature that matches nothing.
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47];

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    /// <summary>
    /// The media type, or a <see cref="Base64MediaTypeException"/> naming which of the two things
    /// went wrong. Failing clean beats mislabelling: an upstream that cannot carry this image
    /// should say so here, not answer about something it never decoded.
    /// </summary>
    public static string Sniff(string base64)
    {
        // 16 base64 chars decode to 12 bytes — enough for every signature below, and a valid
        // standalone block, so no padding games.
        Span<byte> header = stackalloc byte[12];
        var prefix = base64.Length >= 16 ? base64[..16] : base64;

        if (!Convert.TryFromBase64String(prefix, header, out var written) || written < 4)
        {
            throw new Base64MediaTypeException(
                "an image could not be forwarded upstream: its media type is unreadable");
        }

        var bytes = header[..written];

        if (bytes.StartsWith(PngSignature)) return "image/png";
        if (bytes.StartsWith(JpegSignature)) return "image/jpeg";
        if (bytes.StartsWith("GIF8"u8)) return "image/gif";
        if (written >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return "image/webp";

        throw new Base64MediaTypeException(
            "an image could not be forwarded upstream: only PNG, JPEG, GIF and WebP are recognised");
    }
}

/// <summary>
/// Thrown by <see cref="Base64MediaType.Sniff"/>. Each dialect rewraps it in its own request
/// exception, because what a 400 looks like on the wire is the dialect's business and not this
/// helper's.
/// </summary>
public sealed class Base64MediaTypeException(string message) : Exception(message);
