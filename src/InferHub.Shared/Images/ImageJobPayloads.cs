using System.Text.Json;
using InferHub.Shared.Contracts;

namespace InferHub.Shared.Images;

/// <summary>
/// Reads a progress frame back out of the <see cref="ToolChunk"/> it travelled in (phase 47, D2).
/// </summary>
/// <remarks>
/// It is deliberately tolerant: a chunk that is not a progress frame is not an error, because the
/// same transport carries partial answers for every other capability and a job watcher must not
/// fail on one it does not recognise. What it will not do is <em>guess</em> — a frame without a
/// step number reports nothing rather than incrementing a counter of its own.
/// </remarks>
public static class ImageProgress
{
    public static bool TryRead(string? payloadJson, out int step, out int? totalSteps)
    {
        step = 0;
        totalSteps = null;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object
                || !root.TryGetProperty("step", out var stepElement)
                || stepElement.ValueKind is not JsonValueKind.Number
                || !stepElement.TryGetInt32(out step)
                || step < 0)
            {
                return false;
            }

            if (root.TryGetProperty("totalSteps", out var total)
                && total.ValueKind is JsonValueKind.Number
                && total.TryGetInt32(out var parsed)
                && parsed > 0)
            {
                totalSteps = parsed;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Turns a finished image <see cref="ToolResult"/> into the images a job holds, pairing each
/// attachment with what the worker said about it.
/// </summary>
/// <remarks>
/// The pairing matters because the async surface hands images back <em>one at a time</em>, so the
/// size and the seed have to survive beside the bytes rather than only inside a JSON envelope the
/// caller may never fetch. Nothing here decodes a pixel — phase-46 D6 is unchanged, and there is
/// still no image library anywhere in the C#.
/// </remarks>
public static class ImageResults
{
    /// <param name="request">
    /// What was asked for, used only where the worker described nothing. A worker that reports its
    /// own numbers always wins: a recipe may clamp a size or a step count, and taking the request's
    /// figures over the worker's would bill for work that was not done.
    /// </param>
    public static IReadOnlyList<ImageJobImage> Collect(ToolResult result, IImageRequest? request = null)
    {
        var attachments = result.Attachments ?? [];

        if (attachments.Count == 0)
        {
            return [];
        }

        var report = ImageWorkerReport.TryParse(result.Payload);
        var images = new List<ImageJobImage>(attachments.Count);

        for (var i = 0; i < attachments.Count; i++)
        {
            var described = report?.ImageAt(i);

            images.Add(new ImageJobImage(
                attachments[i].Bytes,
                string.IsNullOrWhiteSpace(attachments[i].MediaType) ? "image/png" : attachments[i].MediaType,
                described?.Size ?? request?.Size,
                described?.Seed,
                ImageProjections.Normalise(described?.Projection ?? report?.Projection),
                described?.SeamDelta,
                described?.Steps ?? report?.Steps ?? request?.Steps,
                described?.SeamDeltaBefore,
                described?.SeamRepair));
        }

        return images;
    }

    /// <summary>
    /// What is true of the whole request rather than of one image: whether the recipe's trigger
    /// phrase was appended, and any warnings the worker attached (phase 49, D2 and D5).
    /// </summary>
    public static ImageJobSummary Summarise(ToolResult result)
    {
        var report = ImageWorkerReport.TryParse(result.Payload);

        return report is null
            ? ImageJobSummary.None
            : new ImageJobSummary(report.PromptAugmented, report.Trigger, report.Warnings ?? []);
    }
}
