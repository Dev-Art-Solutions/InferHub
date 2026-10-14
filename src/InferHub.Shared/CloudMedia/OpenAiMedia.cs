using System.Net.Http.Headers;
using System.Text.Json;
using InferHub.Shared.Audio;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;

namespace InferHub.Shared.CloudMedia;

/// <summary>
/// OpenAI's Images and Audio APIs (104 D3): <c>/images/generations</c>, <c>/images/edits</c>,
/// <c>/images/variations</c>, <c>/audio/speech</c> and <c>/audio/transcriptions</c>.
/// </summary>
internal static class OpenAiMedia
{
    public static async Task<ToolResult> GenerateAsync(CloudMediaCall call, JsonElement root)
    {
        CloudImages.RefuseWhatChangesThePicture(root, call.Route);

        var prompt = Payload.Text(root, "prompt") ?? throw new CloudMediaRefusal("prompt is required");
        var size = CloudImages.Size(root);
        var warnings = CloudImages.Unused(root, call.Route, "steps", "guidance", "seed");

        var body = new Dictionary<string, object?>
        {
            ["model"] = call.Route.UpstreamModel,
            ["prompt"] = prompt,
            ["n"] = CloudImages.Count(root),

            // Passed through, never checked here: the sizes each OpenAI model draws differ and move,
            // and the vendor's own 400 names them (62 D5).
            ["size"] = size?.ToString()
        };

        AskForBase64(body, call.Route);

        var (document, failure) = await call.SendJsonAsync(call.PostJson("images/generations", Drop(body)));
        return failure ?? Pictures(call, document!, size, warnings);
    }

    public static async Task<ToolResult> EditAsync(CloudMediaCall call, JsonElement root)
    {
        CloudImages.RefuseWhatChangesThePicture(root, call.Route);

        var image = call.Attachment(ImageEditRequest.ImagePart)
                    ?? throw new CloudMediaRefusal("an 'image' part is required");
        var mask = call.Attachment(ImageEditRequest.MaskPart);
        var variation = string.Equals(Payload.Text(root, "op"), ImageOperations.Variation, StringComparison.OrdinalIgnoreCase);

        if (mask is not null
            && !string.Equals(MaskConventions.Normalise(Payload.Text(root, "mask_convention")), MaskConventions.OpenAi, StringComparison.Ordinal))
        {
            // 50 D2: OpenAI reads transparent as "edit here". Inverting a luminance mask means
            // decoding pixels, which happens in a worker and never in this library (46 D6).
            throw new CloudMediaRefusal(
                $"provider '{call.Route.ProviderId}' reads a mask as OpenAI does — transparent pixels are the area to edit — and " +
                "this mask was declared 'luminance'. Send a mask with an alpha channel and mask_convention=openai.");
        }

        var size = CloudImages.Size(root);
        var warnings = CloudImages.Unused(root, call.Route, "steps", "guidance", "seed", "strength");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(call.Route.UpstreamModel), "model");
        form.Add(new StringContent(CloudImages.Count(root).ToString(System.Globalization.CultureInfo.InvariantCulture)), "n");

        if (size is { } asked)
        {
            form.Add(new StringContent(asked.ToString()), "size");
        }

        if (!variation)
        {
            form.Add(new StringContent(Payload.Text(root, "prompt") ?? throw new CloudMediaRefusal("prompt is required for an edit")), "prompt");
        }

        if (variation || call.Route.UpstreamModel.StartsWith("dall-e", StringComparison.OrdinalIgnoreCase))
        {
            form.Add(new StringContent("b64_json"), "response_format");
        }

        form.Add(File(image.Bytes, MediaBytes.ImageMediaType(image.Bytes, image.MediaType)), "image", "image." + MediaBytes.ExtensionFor(MediaBytes.ImageMediaType(image.Bytes, image.MediaType)));

        if (mask is not null && !variation)
        {
            form.Add(File(mask.Bytes, "image/png"), "mask", "mask.png");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, call.Url(variation ? "images/variations" : "images/edits")) { Content = form };
        var (document, failure) = await call.SendJsonAsync(request);
        return failure ?? Pictures(call, document!, size, warnings);
    }

    public static async Task<ToolResult> SpeakAsync(CloudMediaCall call, JsonElement root)
    {
        RefuseStreaming(root, call.Route);

        var input = Payload.Text(root, "input") ?? throw new CloudMediaRefusal("input is required");
        var format = Payload.Text(root, "response_format") ?? SpeechFormats.Wav;

        var body = Drop(new Dictionary<string, object?>
        {
            ["model"] = call.Route.UpstreamModel,
            ["input"] = input,

            // OpenAI requires a voice and Ollama-style clients often send none; "alloy" is the
            // vendor's own first example and every TTS model it serves has it.
            ["voice"] = Payload.Text(root, "voice") ?? "alloy",
            ["response_format"] = format,
            ["speed"] = Payload.Number(root, "speed")
        });

        var (response, failure) = await call.SendAsync(call.PostJson("audio/speech", body));

        if (response is null)
        {
            return failure!;
        }

        using (response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(call.CancellationToken);

            return bytes.Length == 0
                ? ToolResult.Failed(call.Job.JobId, $"provider '{call.Route.ProviderId}' answered with no audio")
                : ToolResult.Succeeded(
                    call.Job.JobId,
                    "{}",
                    [new ToolAttachment(SpeechFormats.FileNameFor(format), SpeechFormats.ContentTypeOf(format), bytes)]);
        }
    }

    public static async Task<ToolResult> TranscribeAsync(CloudMediaCall call, JsonElement root)
    {
        var audio = call.Attachments.FirstOrDefault() ?? throw new CloudMediaRefusal("a 'file' part is required");
        var mediaType = MediaBytes.AudioMediaType(audio.Bytes, audio.MediaType) ?? audio.MediaType;

        // whisper-1 answers verbose_json with segments and a duration; the gpt-4o transcribers answer
        // json only, and their transcript cannot become srt or vtt — the renderer says so (42 D1).
        var verbose = call.Route.UpstreamModel.StartsWith("whisper", StringComparison.OrdinalIgnoreCase);

        using var form = new MultipartFormDataContent();
        form.Add(File(audio.Bytes, mediaType), "file", "audio." + MediaBytes.ExtensionFor(mediaType));
        form.Add(new StringContent(call.Route.UpstreamModel), "model");
        form.Add(new StringContent(verbose ? "verbose_json" : "json"), "response_format");

        if (Payload.Text(root, "language") is { } language)
        {
            form.Add(new StringContent(language), "language");
        }

        if (Payload.Text(root, "prompt") is { } prompt)
        {
            form.Add(new StringContent(prompt), "prompt");
        }

        if (Payload.Number(root, "temperature") is { } temperature)
        {
            form.Add(new StringContent(temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)), "temperature");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, call.Url("audio/transcriptions")) { Content = form };
        var (document, failure) = await call.SendJsonAsync(request);

        if (document is null)
        {
            return failure!;
        }

        using (document)
        {
            var answer = document.RootElement;
            var text = Payload.Text(answer, "text")
                       ?? throw new InvalidDataException($"provider '{call.Route.ProviderId}' answered with no transcript text");

            // 104 D5: the seconds the vendor reports, then the file's own header, then nothing — never a guess.
            var duration = Payload.Number(answer, "duration")
                           ?? (answer.TryGetProperty("usage", out var usage) ? Payload.Number(usage, "seconds") : null)
                           ?? MediaBytes.WavSeconds(audio.Bytes);

            var segments = answer.TryGetProperty("segments", out var list) && list.ValueKind is JsonValueKind.Array
                ? list.EnumerateArray()
                    .Where(segment => segment.ValueKind is JsonValueKind.Object)
                    .Select((segment, index) => new TranscriptSegment(
                        Payload.Int(segment, "id") ?? index,
                        Payload.Number(segment, "start") ?? 0,
                        Payload.Number(segment, "end") ?? 0,
                        Payload.Text(segment, "text") ?? string.Empty))
                    .ToArray()
                : [];

            var transcript = JsonSerializer.Serialize(
                new { text, language = Payload.Text(answer, "language"), duration, segments },
                CloudMediaClient.Json);

            return ToolResult.Succeeded(call.Job.JobId, transcript);
        }
    }

    internal static void RefuseStreaming(JsonElement root, CloudMediaRoute route)
    {
        if (Payload.Text(root, "stream_format") is not null)
        {
            throw new CloudMediaRefusal(
                $"model '{route.LocalModel}' is a cloud voice (provider '{route.ProviderId}') and is answered once; " +
                "send the request without stream_format.");
        }
    }

    private static void AskForBase64(Dictionary<string, object?> body, CloudMediaRoute route)
    {
        // The gpt-image models always answer base64 and refuse the field; the dall-e models answer a
        // URL unless asked, and a URL is the one thing this API never returns (46 D5).
        if (route.UpstreamModel.StartsWith("dall-e", StringComparison.OrdinalIgnoreCase))
        {
            body["response_format"] = "b64_json";
        }
    }

    private static ToolResult Pictures(CloudMediaCall call, JsonDocument document, ImageSize? size, List<string> warnings)
    {
        using (document)
        {
            var pictures = new List<(byte[], string?)>();

            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind is JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (Payload.Text(item, "b64_json") is { } base64)
                    {
                        pictures.Add((CloudImages.FromBase64(base64, call.Route), null));
                    }
                    else if (Payload.Text(item, "url") is not null)
                    {
                        return ToolResult.Failed(
                            call.Job.JobId,
                            $"provider '{call.Route.ProviderId}' answered with a URL instead of the picture; this hub never returns URLs");
                    }
                }
            }

            if (Payload.Text(document.RootElement, "output_format") is { } format && format != "png")
            {
                warnings.Add($"provider '{call.Route.ProviderId}' answered in {format}, not PNG");
            }

            return CloudImages.Answer(call.Job, call.Route, pictures, size, warnings);
        }
    }

    private static ByteArrayContent File(byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        return content;
    }

    private static Dictionary<string, object?> Drop(Dictionary<string, object?> body)
    {
        foreach (var key in body.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray())
        {
            body.Remove(key);
        }

        return body;
    }
}
