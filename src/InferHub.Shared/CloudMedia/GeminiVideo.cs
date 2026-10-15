using System.Globalization;
using System.Text.Json;
using InferHub.Shared.Contracts;

namespace InferHub.Shared.CloudMedia;

/// <summary>
/// A clip from Google (phase 105): Veo through <c>:predictLongRunning</c> and an operation polled until
/// it is done, and Gemini Omni through <c>/interactions</c>, which answers the clip in the same call.
/// Both end in the shape the diffusion worker answers with — one MP4 attachment and
/// <c>{videos:[{width,height,seconds}], megapixel_steps, warnings}</c> — so <c>VideoRenderer</c> and the
/// job store do not learn that a vendor made it (105 D1).
/// </summary>
internal static class GeminiVideo
{
    /// <summary>What Veo renders: <c>durationSeconds</c> is one of these, as a string.</summary>
    internal static readonly int[] VeoSeconds = [4, 6, 8];

    /// <summary>
    /// The sizes a vendor clip comes in, as the <c>size</c> a caller writes: ratio and resolution are what
    /// the vendor takes, and any other pair is refused naming these rather than rounded (105 D2).
    /// </summary>
    internal static readonly (string Size, string Ratio, string Resolution, int Width, int Height)[] Sizes =
    [
        ("1280x720", "16:9", "720p", 1280, 720),
        ("720x1280", "9:16", "720p", 720, 1280),
        ("1920x1080", "16:9", "1080p", 1920, 1080),
        ("1080x1920", "9:16", "1080p", 1080, 1920)
    ];

    /// <summary>A vendor clip takes minutes; Veo's own guidance is up to six at peak. A provider's 300 s default would cut it.</summary>
    internal const int MinTimeoutSeconds = 600;

    public static bool IsOmni(string upstreamModel) =>
        upstreamModel.Contains("omni", StringComparison.OrdinalIgnoreCase);

    public static async Task<ToolResult> GenerateAsync(CloudMediaCall call, JsonElement root)
    {
        var prompt = Payload.Text(root, "prompt") ?? throw new CloudMediaRefusal("prompt is required");

        if (!string.IsNullOrWhiteSpace(Payload.Text(root, "negative_prompt")))
        {
            throw new CloudMediaRefusal(
                $"provider '{call.Route.ProviderId}' ({call.Route.UpstreamModel}) takes no negative prompt; " +
                "remove negative_prompt and say in the prompt what the clip should leave out.");
        }

        var size = Size(root, call.Route);
        var warnings = new List<string>();

        foreach (var name in (string[])["steps", "guidance", "seed"])
        {
            if (Payload.Present(root, name))
            {
                warnings.Add($"{name} is not used by provider '{call.Route.ProviderId}' ({call.Route.UpstreamModel})");
            }
        }

        return IsOmni(call.Route.UpstreamModel)
            ? await OmniAsync(call, root, prompt, size, warnings)
            : await VeoAsync(call, root, prompt, size, warnings);
    }

    private static async Task<ToolResult> VeoAsync(
        CloudMediaCall call, JsonElement root, string prompt,
        (string Size, string Ratio, string Resolution, int Width, int Height) size, List<string> warnings)
    {
        int? seconds = null;

        if (Payload.Number(root, "seconds") is { } asked)
        {
            if (Math.Abs(asked - Math.Round(asked)) > 1e-9 || !VeoSeconds.Contains((int)Math.Round(asked)))
            {
                throw new CloudMediaRefusal(
                    $"provider '{call.Route.ProviderId}' ({call.Route.UpstreamModel}) renders {string.Join(", ", VeoSeconds)} seconds; " +
                    $"{asked.ToString(CultureInfo.InvariantCulture)} is not one of them.");
            }

            seconds = (int)Math.Round(asked);
        }

        if (size.Resolution != "720p")
        {
            if (seconds is { } chosen && chosen != 8)
            {
                throw new CloudMediaRefusal(
                    $"provider '{call.Route.ProviderId}' ({call.Route.UpstreamModel}) renders {size.Resolution} only at 8 seconds; " +
                    $"ask for seconds=8 or a 720p size ({Sizes[0].Size} or {Sizes[1].Size}).");
            }

            seconds = 8;
        }

        var parameters = new Dictionary<string, object?>
        {
            ["aspectRatio"] = size.Ratio,
            ["resolution"] = size.Resolution,
            ["durationSeconds"] = seconds?.ToString(CultureInfo.InvariantCulture)
        };

        var (submitted, failure) = await call.SendJsonAsync(call.PostJson(
            $"{GeminiMedia.ModelPath(call.Route.UpstreamModel)}:predictLongRunning",
            new { instances = new[] { new { prompt } }, parameters }));

        if (submitted is null)
        {
            return failure!;
        }

        string operation;

        using (submitted)
        {
            operation = Payload.Text(submitted.RootElement, "name")
                ?? throw new InvalidDataException($"provider '{call.Route.ProviderId}' accepted the clip and named no operation to poll");
        }

        // 56: from here the clip is paid for. Nothing below submits it again; a failure fails the job.
        var started = DateTimeOffset.UtcNow;
        string uri;

        while (true)
        {
            call.ReportProgress(started);
            await Task.Delay(call.PollInterval, call.CancellationToken);

            var (polled, pollFailure) = await call.SendJsonAsync(new HttpRequestMessage(HttpMethod.Get, call.Url(operation)));

            if (polled is null)
            {
                return Failed(call, $"polling operation '{operation}' failed: {pollFailure!.Error}");
            }

            using (polled)
            {
                var state = polled.RootElement;

                if (!Payload.True(state, "done"))
                {
                    continue;
                }

                if (state.TryGetProperty("error", out var error) && error.ValueKind is JsonValueKind.Object)
                {
                    return Failed(call, $"operation '{operation}' ended with an error: {Payload.Text(error, "message") ?? "no message"}");
                }

                uri = VeoUri(state)
                      ?? throw new InvalidDataException(
                          $"provider '{call.Route.ProviderId}' finished operation '{operation}' without a video" +
                          (Filtered(state) is { } reason ? $" ({reason})" : ""));
                break;
            }
        }

        var bytes = await call.DownloadAsync(uri);
        return Answer(call, bytes, size, MediaBytes.Mp4Seconds(bytes) ?? seconds ?? 8, warnings);
    }

    private static async Task<ToolResult> OmniAsync(
        CloudMediaCall call, JsonElement root, string prompt,
        (string Size, string Ratio, string Resolution, int Width, int Height) size, List<string> warnings)
    {
        if (Payload.Present(root, "seconds"))
        {
            // Omni takes no duration (read 2026-10-15); the length is the model's, and the answer reports the real one.
            warnings.Add($"provider '{call.Route.ProviderId}' ({call.Route.UpstreamModel}) chooses the clip's length; seconds was not sent");
        }

        call.ReportProgress(DateTimeOffset.UtcNow);

        var (answer, failure) = await call.SendJsonAsync(call.PostJson("interactions", new
        {
            model = call.Route.UpstreamModel.StartsWith("models/", StringComparison.Ordinal)
                ? call.Route.UpstreamModel["models/".Length..]
                : call.Route.UpstreamModel,
            input = prompt,
            response_format = new { type = "video", aspect_ratio = size.Ratio, resolution = size.Resolution }
        }));

        if (answer is null)
        {
            return failure!;
        }

        byte[] bytes;

        using (answer)
        {
            var status = Payload.Text(answer.RootElement, "status");

            if (status is not null and not "completed")
            {
                return Failed(call, $"the interaction ended '{status}'");
            }

            var video = OmniVideo(answer.RootElement)
                        ?? throw new InvalidDataException($"provider '{call.Route.ProviderId}' completed the interaction without a video");

            if (Payload.Text(video, "data") is { } base64)
            {
                try
                {
                    bytes = Convert.FromBase64String(base64);
                }
                catch (FormatException)
                {
                    throw new InvalidDataException($"provider '{call.Route.ProviderId}' answered a video that is not base64");
                }
            }
            else if (Payload.Text(video, "uri") is { } uri)
            {
                bytes = await call.DownloadAsync(uri);
            }
            else
            {
                throw new InvalidDataException($"provider '{call.Route.ProviderId}' answered a video with neither data nor a uri");
            }
        }

        if (bytes.Length > call.MaxDownloadBytes)
        {
            throw new InvalidDataException($"the clip from provider '{call.Route.ProviderId}' is over {call.MaxDownloadBytes} bytes (Tools:MaxAttachmentBytes)");
        }

        var seconds = MediaBytes.Mp4Seconds(bytes);

        if (seconds is null)
        {
            warnings.Add("the clip's length could not be read from its MP4 header; video seconds are not metered for it");
        }

        return Answer(call, bytes, size, seconds ?? 0, warnings);
    }

    /// <summary>
    /// The worker's envelope (57). <c>megapixel_steps</c> is one step per megapixel per second (105 D3): a
    /// vendor clip spends no card, and zero would make every video quota free the day a key is set.
    /// </summary>
    private static ToolResult Answer(
        CloudMediaCall call, byte[] bytes,
        (string Size, string Ratio, string Resolution, int Width, int Height) size, double seconds, List<string> warnings)
    {
        var payload = JsonSerializer.Serialize(new
        {
            videos = new[] { new { width = size.Width, height = size.Height, seconds = Math.Round(seconds, 3) } },
            megapixel_steps = Math.Round(size.Width * size.Height / 1_000_000.0 * seconds, 4),
            warnings = warnings.Count > 0 ? warnings : null
        }, CloudMediaClient.Json);

        return ToolResult.Succeeded(call.Job.JobId, payload, [new ToolAttachment("video.mp4", "video/mp4", bytes)]);
    }

    /// <summary>The caller's <c>size</c>, or the vendor's default (16:9 720p) when they named none.</summary>
    private static (string Size, string Ratio, string Resolution, int Width, int Height) Size(JsonElement root, CloudMediaRoute route)
    {
        var width = Payload.Int(root, "width");
        var height = Payload.Int(root, "height");

        if (width is null || height is null)
        {
            return Sizes[0];
        }

        foreach (var size in Sizes)
        {
            if (size.Width == width && size.Height == height)
            {
                return size;
            }
        }

        throw new CloudMediaRefusal(
            $"provider '{route.ProviderId}' ({route.UpstreamModel}) renders {string.Join(", ", Sizes.Select(s => s.Size))}; " +
            $"{width}x{height} is not one of them.");
    }

    private static ToolResult Failed(CloudMediaCall call, string why) =>
        ToolResult.Failed(call.Job.JobId, $"provider '{call.Route.ProviderId}' ({call.Route.UpstreamModel}): {why}. It was not submitted again.");

    private static string? VeoUri(JsonElement operation) =>
        operation.TryGetProperty("response", out var response)
        && response.TryGetProperty("generateVideoResponse", out var generated)
        && generated.TryGetProperty("generatedSamples", out var samples)
        && samples.ValueKind is JsonValueKind.Array
        && samples.GetArrayLength() > 0
        && samples[0].TryGetProperty("video", out var video)
            ? Payload.Text(video, "uri")
            : null;

    /// <summary>Veo's word for a prompt its filter stopped, when it gives one.</summary>
    private static string? Filtered(JsonElement operation) =>
        operation.TryGetProperty("response", out var response)
        && response.TryGetProperty("generateVideoResponse", out var generated)
        && generated.TryGetProperty("raiMediaFilteredReasons", out var reasons)
        && reasons.ValueKind is JsonValueKind.Array
        && reasons.GetArrayLength() > 0
            ? reasons[0].GetString()
            : null;

    /// <summary>The first <c>video</c> content of the interaction's model output.</summary>
    private static JsonElement? OmniVideo(JsonElement interaction)
    {
        if (interaction.TryGetProperty("steps", out var steps) && steps.ValueKind is JsonValueKind.Array)
        {
            foreach (var step in steps.EnumerateArray())
            {
                if (step.TryGetProperty("content", out var content) && content.ValueKind is JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (Payload.Text(part, "type") == "video")
                        {
                            return part;
                        }
                    }
                }
            }
        }

        // The SDKs' convenience field, in case the REST answer ever carries it too.
        return interaction.TryGetProperty("output_video", out var output) && output.ValueKind is JsonValueKind.Object ? output : null;
    }
}
