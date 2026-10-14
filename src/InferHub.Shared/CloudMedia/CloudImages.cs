using System.Globalization;
using System.Text.Json;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;

namespace InferHub.Shared.CloudMedia;

/// <summary>
/// What both vendors' picture routes share: the refusals before the call (104 D4), the warnings for
/// what was answered without, and the answer in the worker's shape (104 D1, D5).
/// </summary>
internal static class CloudImages
{
    /// <summary>Refused rather than dropped: each of these changes <em>what</em> is drawn (91).</summary>
    public static void RefuseWhatChangesThePicture(JsonElement root, CloudMediaRoute route)
    {
        if (!string.IsNullOrWhiteSpace(Payload.Text(root, "negative_prompt")))
        {
            throw new CloudMediaRefusal(
                $"model '{route.LocalModel}' is served by provider '{route.ProviderId}' ({route.UpstreamModel}), which takes no " +
                "negative prompt. Remove negative_prompt, or say what you do not want in the prompt itself.");
        }

        if (Payload.Present(root, "seam_repair") || Payload.Present(root, "reproject"))
        {
            throw new CloudMediaRefusal(
                $"seam repair and re-projection are made by a local panorama recipe; model '{route.LocalModel}' is a cloud model " +
                $"(provider '{route.ProviderId}'). Send the request to a panorama recipe on a node, or without those headers.");
        }
    }

    /// <summary>The fields that shape <em>how</em> a picture is made, answered without and said so in <c>warnings</c>.</summary>
    public static List<string> Unused(JsonElement root, CloudMediaRoute route, params string[] fields)
    {
        var warnings = new List<string>();

        foreach (var field in fields)
        {
            if (Payload.Present(root, field))
            {
                warnings.Add($"{field} is not used by provider '{route.ProviderId}' ({route.UpstreamModel}); the picture was made without it");
            }
        }

        return warnings;
    }

    public static ImageSize? Size(JsonElement root) =>
        Payload.Int(root, "width") is { } width && Payload.Int(root, "height") is { } height
            ? new ImageSize(width, height)
            : null;

    public static int Count(JsonElement root) => Math.Max(1, Payload.Int(root, "n") ?? 1);

    /// <summary>
    /// The pictures as a worker would hand them back: one attachment each, and a report whose
    /// <c>steps</c> is 1 so a cloud picture meters its megapixels (104 D5). The size is read off the
    /// PNG header the vendor sent, not the one asked for — a vendor that chose its own pixels is
    /// reported as having done so.
    /// </summary>
    public static ToolResult Answer(
        ToolJob job,
        CloudMediaRoute route,
        IReadOnlyList<(byte[] Bytes, string? MediaType)> pictures,
        ImageSize? asked,
        IReadOnlyList<string> warnings)
    {
        if (pictures.Count == 0)
        {
            return ToolResult.Failed(job.JobId, $"provider '{route.ProviderId}' ({route.UpstreamModel}) answered with no picture");
        }

        var attachments = new List<ToolAttachment>(pictures.Count);
        var described = new List<object>(pictures.Count);

        for (var i = 0; i < pictures.Count; i++)
        {
            var (bytes, declared) = pictures[i];
            var mediaType = MediaBytes.ImageMediaType(bytes, declared);
            var size = MediaBytes.PngSize(bytes);

            attachments.Add(new ToolAttachment($"image-{i}.{MediaBytes.ExtensionFor(mediaType)}", mediaType, bytes));
            described.Add(new
            {
                width = size?.Width ?? asked?.Width,
                height = size?.Height ?? asked?.Height
            });
        }

        var report = JsonSerializer.Serialize(
            new
            {
                steps = 1,
                images = described,
                warnings = warnings.Count == 0 ? null : warnings
            },
            CloudMediaClient.Json);

        return ToolResult.Succeeded(job.JobId, report, attachments);
    }

    public static byte[] FromBase64(string? value, CloudMediaRoute route)
    {
        try
        {
            return Convert.FromBase64String(value ?? string.Empty);
        }
        catch (FormatException)
        {
            throw new InvalidDataException($"provider '{route.ProviderId}' sent a picture that is not base64");
        }
    }

    /// <summary>
    /// A requested size as one of the vendor's aspect ratios, or a refusal naming them (104 D4).
    /// Exact ratios only: 1000x1001 is not "about square" to somebody who asked for it.
    /// </summary>
    public static string? AspectRatio(ImageSize? size, IReadOnlyList<string> allowed, CloudMediaRoute route)
    {
        if (size is not { } asked)
        {
            return null;
        }

        var divisor = Gcd(asked.Width, asked.Height);
        var ratio = string.Create(CultureInfo.InvariantCulture, $"{asked.Width / divisor}:{asked.Height / divisor}");

        if (allowed.Contains(ratio, StringComparer.Ordinal))
        {
            return ratio;
        }

        throw new CloudMediaRefusal(
            $"size {asked} is the aspect ratio {ratio}, and provider '{route.ProviderId}' ({route.UpstreamModel}) draws only " +
            $"{string.Join(", ", allowed)}. Send a size with one of those ratios, e.g. 1024x1024 (1:1) or 1600x900 (16:9); the vendor picks the pixels.");
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return Math.Max(1, a);
    }
}
