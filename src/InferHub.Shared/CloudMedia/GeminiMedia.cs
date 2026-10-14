using System.Globalization;
using System.Text;
using System.Text.Json;
using InferHub.Shared.Audio;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;

namespace InferHub.Shared.CloudMedia;

/// <summary>
/// Google's Gemini API (104 D3): Imagen through <c>:predict</c>, the Gemini image models and the TTS
/// models through <c>:generateContent</c> with <c>responseModalities</c>, and transcription as an
/// audio part with a fixed instruction.
/// </summary>
internal static class GeminiMedia
{
    /// <summary>What the Gemini image models draw (their <c>imageConfig.aspectRatio</c>).</summary>
    private static readonly string[] GeminiRatios = ["1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "9:16", "16:9", "21:9"];

    /// <summary>What Imagen draws (its <c>parameters.aspectRatio</c>).</summary>
    private static readonly string[] ImagenRatios = ["1:1", "3:4", "4:3", "9:16", "16:9"];

    /// <summary>The voice a Gemini TTS model uses when the request names none — the vendor's own first example.</summary>
    internal const string DefaultVoice = "Kore";

    /// <summary>
    /// 20 MB is the vendor's whole-request ceiling for inline data, and base64 makes a file a third
    /// larger. Over this the vendor's Files API is the path, and it is not in this release.
    /// </summary>
    internal const int MaxInlineAudioBytes = 14 * 1024 * 1024;

    private const string TranscribeInstruction =
        "Transcribe the speech in this audio verbatim. Answer with the transcript text only — no title, no commentary, no timestamps.";

    public static async Task<ToolResult> GenerateAsync(CloudMediaCall call, JsonElement root)
    {
        CloudImages.RefuseWhatChangesThePicture(root, call.Route);

        var prompt = Payload.Text(root, "prompt") ?? throw new CloudMediaRefusal("prompt is required");
        var size = CloudImages.Size(root);

        if (IsImagen(call.Route.UpstreamModel))
        {
            var warnings = CloudImages.Unused(root, call.Route, "steps", "guidance", "seed");
            var ratio = CloudImages.AspectRatio(size, ImagenRatios, call.Route);
            Pixels(size, ratio, call.Route, warnings);

            var body = new
            {
                instances = new[] { new { prompt } },
                parameters = new { sampleCount = CloudImages.Count(root), aspectRatio = ratio }
            };

            var (document, failure) = await call.SendJsonAsync(call.PostJson($"{ModelPath(call.Route.UpstreamModel)}:predict", body));

            if (document is null)
            {
                return failure!;
            }

            using (document)
            {
                var pictures = new List<(byte[], string?)>();

                if (document.RootElement.TryGetProperty("predictions", out var predictions) && predictions.ValueKind is JsonValueKind.Array)
                {
                    foreach (var prediction in predictions.EnumerateArray())
                    {
                        if (Payload.Text(prediction, "bytesBase64Encoded") is { } base64)
                        {
                            pictures.Add((CloudImages.FromBase64(base64, call.Route), Payload.Text(prediction, "mimeType")));
                        }
                    }
                }

                if (pictures.Count < CloudImages.Count(root))
                {
                    // Imagen drops a picture its safety filter stopped and says nothing else.
                    warnings.Add($"asked for {CloudImages.Count(root)} picture(s) and provider '{call.Route.ProviderId}' returned {pictures.Count}");
                }

                return CloudImages.Answer(call.Job, call.Route, pictures, size, warnings);
            }
        }

        return await ContentImagesAsync(call, root, prompt, size, input: null);
    }

    public static async Task<ToolResult> EditAsync(CloudMediaCall call, JsonElement root)
    {
        CloudImages.RefuseWhatChangesThePicture(root, call.Route);

        if (string.Equals(Payload.Text(root, "op"), ImageOperations.Variation, StringComparison.OrdinalIgnoreCase))
        {
            throw new CloudMediaRefusal(
                $"provider '{call.Route.ProviderId}' ({call.Route.UpstreamModel}) edits by instruction and has no variation operation. " +
                "Send op=edit with a prompt saying what to change.");
        }

        if (IsImagen(call.Route.UpstreamModel))
        {
            throw new CloudMediaRefusal(
                $"provider '{call.Route.ProviderId}' ({call.Route.UpstreamModel}) is an Imagen model, which only generates on the Gemini API. " +
                "Map a Gemini image model (e.g. gemini-2.5-flash-image) under image-edit.");
        }

        if (call.Attachment(ImageEditRequest.MaskPart) is not null)
        {
            throw new CloudMediaRefusal(
                $"provider '{call.Route.ProviderId}' ({call.Route.UpstreamModel}) edits by instruction and reads no mask. " +
                "Send the edit without a mask and say in the prompt which part to change.");
        }

        var image = call.Attachment(ImageEditRequest.ImagePart) ?? throw new CloudMediaRefusal("an 'image' part is required");
        var prompt = Payload.Text(root, "prompt") ?? throw new CloudMediaRefusal("prompt is required for an edit");

        return await ContentImagesAsync(call, root, prompt, CloudImages.Size(root), image);
    }

    public static async Task<ToolResult> SpeakAsync(CloudMediaCall call, JsonElement root)
    {
        OpenAiMedia.RefuseStreaming(root, call.Route);

        var input = Payload.Text(root, "input") ?? throw new CloudMediaRefusal("input is required");
        var format = Payload.Text(root, "response_format") ?? SpeechFormats.Wav;

        if (format is not (SpeechFormats.Wav or SpeechFormats.Pcm))
        {
            // The vendor answers raw 24 kHz PCM; wrapping it in a WAV header is the one container
            // written here, and an mp3 encoder in C# is a dependency (rule 5).
            throw new CloudMediaRefusal(
                $"provider '{call.Route.ProviderId}' ({call.Route.UpstreamModel}) answers raw PCM, which this hub can return as " +
                $"'{SpeechFormats.Wav}' or '{SpeechFormats.Pcm}'; '{format}' would need an encoder. Ask for response_format=wav.",
                ToolErrorCodes.UnsupportedFormat);
        }

        if (Payload.Number(root, "speed") is { } speed && Math.Abs(speed - 1.0) > 1e-9)
        {
            throw new CloudMediaRefusal(
                $"provider '{call.Route.ProviderId}' ({call.Route.UpstreamModel}) takes no speed; remove it, or ask for the pace in the input " +
                "(e.g. \"Say slowly: …\").");
        }

        var body = new
        {
            contents = new[] { new { parts = new[] { new { text = input } } } },
            generationConfig = new
            {
                responseModalities = new[] { "AUDIO" },
                speechConfig = new
                {
                    voiceConfig = new { prebuiltVoiceConfig = new { voiceName = Payload.Text(root, "voice") ?? DefaultVoice } }
                }
            }
        };

        var (document, failure) = await call.SendJsonAsync(call.PostJson($"{ModelPath(call.Route.UpstreamModel)}:generateContent", body));

        if (document is null)
        {
            return failure!;
        }

        using (document)
        {
            var audio = InlineParts(document.RootElement)
                .FirstOrDefault(part => part.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true);

            if (audio.Data is null)
            {
                return ToolResult.Failed(call.Job.JobId, $"provider '{call.Route.ProviderId}' answered with no audio{Blocked(document.RootElement)}");
            }

            var pcm = CloudImages.FromBase64(audio.Data, call.Route);
            var bytes = format == SpeechFormats.Wav ? MediaBytes.WrapPcm16(pcm, SampleRate(audio.MimeType)) : pcm;

            return ToolResult.Succeeded(
                call.Job.JobId,
                "{}",
                [new ToolAttachment(SpeechFormats.FileNameFor(format), SpeechFormats.ContentTypeOf(format), bytes)]);
        }
    }

    public static async Task<ToolResult> TranscribeAsync(CloudMediaCall call, JsonElement root)
    {
        var audio = call.Attachments.FirstOrDefault() ?? throw new CloudMediaRefusal("a 'file' part is required");

        if (audio.Bytes.Length > MaxInlineAudioBytes)
        {
            throw new CloudMediaRefusal(
                $"the audio is {audio.Bytes.Length} bytes and provider '{call.Route.ProviderId}' takes at most {MaxInlineAudioBytes} " +
                "inline. Send a shorter or more compressed file (mp3, ogg), or use an OpenAI transcriber or a node's.");
        }

        var mediaType = MediaBytes.AudioMediaType(audio.Bytes, audio.MediaType)
                        ?? throw new CloudMediaRefusal(
                            $"could not tell the audio format, and provider '{call.Route.ProviderId}' needs it. " +
                            "Send wav, mp3, ogg, flac, aac or aiff with its content type.",
                            ToolErrorCodes.UnsupportedFormat);

        var instruction = new StringBuilder(TranscribeInstruction);

        if (Payload.Text(root, "language") is { } language)
        {
            instruction.Append(CultureInfo.InvariantCulture, $" The speech is in the language '{language}'.");
        }

        if (Payload.Text(root, "prompt") is { } prompt)
        {
            instruction.Append(" Spelling hints and context: ").Append(prompt);
        }

        var body = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = instruction.ToString() },
                        new { inlineData = new { mimeType = mediaType, data = Convert.ToBase64String(audio.Bytes) } }
                    }
                }
            },
            generationConfig = Payload.Number(root, "temperature") is { } temperature ? new { temperature } : null
        };

        var (document, failure) = await call.SendJsonAsync(call.PostJson($"{ModelPath(call.Route.UpstreamModel)}:generateContent", body));

        if (document is null)
        {
            return failure!;
        }

        using (document)
        {
            var text = Text(document.RootElement);

            if (text is null)
            {
                return ToolResult.Failed(call.Job.JobId, $"provider '{call.Route.ProviderId}' answered with no transcript{Blocked(document.RootElement)}");
            }

            // No segments: a generative transcriber has no timestamps to give, and inventing them
            // would be worse than the renderer's refusal of srt/vtt (42 D1). Duration from the WAV
            // header only — 104 D5.
            var transcript = JsonSerializer.Serialize(
                new
                {
                    text = text.Trim(),
                    language = Payload.Text(root, "language"),
                    duration = MediaBytes.WavSeconds(audio.Bytes)
                },
                CloudMediaClient.Json);

            return ToolResult.Succeeded(call.Job.JobId, transcript);
        }
    }

    private static async Task<ToolResult> ContentImagesAsync(
        CloudMediaCall call,
        JsonElement root,
        string prompt,
        ImageSize? size,
        ToolAttachment? input)
    {
        var warnings = CloudImages.Unused(root, call.Route, "steps", "guidance", "seed", "strength");
        var ratio = CloudImages.AspectRatio(size, GeminiRatios, call.Route);
        Pixels(size, ratio, call.Route, warnings);

        var parts = new List<object> { new { text = prompt } };

        if (input is not null)
        {
            parts.Add(new { inlineData = new { mimeType = MediaBytes.ImageMediaType(input.Bytes, input.MediaType), data = Convert.ToBase64String(input.Bytes) } });
        }

        var body = new
        {
            contents = new[] { new { role = "user", parts } },
            generationConfig = new
            {
                responseModalities = new[] { "TEXT", "IMAGE" },
                imageConfig = ratio is null ? null : new { aspectRatio = ratio }
            }
        };

        var pictures = new List<(byte[], string?)>();
        var count = CloudImages.Count(root);

        // One picture per call is what these models answer; n is n calls, made one after another so
        // a rate limit stops the batch at the first 429 rather than spending the rest of it.
        for (var i = 0; i < count; i++)
        {
            var (document, failure) = await call.SendJsonAsync(call.PostJson($"{ModelPath(call.Route.UpstreamModel)}:generateContent", body));

            if (document is null)
            {
                return failure!;
            }

            using (document)
            {
                var found = InlineParts(document.RootElement)
                    .Where(part => part.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true && part.Data is not null)
                    .ToArray();

                if (found.Length == 0)
                {
                    return ToolResult.Failed(call.Job.JobId, $"provider '{call.Route.ProviderId}' answered with no picture{Blocked(document.RootElement)}");
                }

                pictures.AddRange(found.Select(part => (CloudImages.FromBase64(part.Data, call.Route), part.MimeType)));
            }
        }

        return CloudImages.Answer(call.Job, call.Route, pictures, size, warnings);
    }

    private static void Pixels(ImageSize? size, string? ratio, CloudMediaRoute route, List<string> warnings)
    {
        if (size is { } asked && ratio is not null)
        {
            warnings.Add($"size {asked} was sent as the aspect ratio {ratio}; provider '{route.ProviderId}' chose the pixels");
        }
    }

    private static bool IsImagen(string model) =>
        model.Contains("imagen", StringComparison.OrdinalIgnoreCase);

    /// <summary>The model as a URL path: <c>models/x</c> unless the id already is a path (64 D2, normalised the same way).</summary>
    private static string ModelPath(string model)
    {
        var trimmed = model.Trim().TrimStart('/');
        return trimmed.Contains('/') ? trimmed : "models/" + trimmed;
    }

    private static IEnumerable<(string? MimeType, string? Data)> InlineParts(JsonElement root)
    {
        foreach (var part in Parts(root))
        {
            if (part.TryGetProperty("inlineData", out var inline) && inline.ValueKind is JsonValueKind.Object)
            {
                yield return (Payload.Text(inline, "mimeType"), Payload.Text(inline, "data"));
            }
        }
    }

    private static string? Text(JsonElement root)
    {
        var texts = Parts(root)
            .Where(part => !Payload.True(part, "thought"))
            .Select(part => Payload.Text(part, "text"))
            .Where(text => text is not null)
            .ToArray();

        return texts.Length == 0 ? null : string.Concat(texts);
    }

    private static IEnumerable<JsonElement> Parts(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind is not JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (candidate.TryGetProperty("content", out var content)
                && content.TryGetProperty("parts", out var parts)
                && parts.ValueKind is JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.ValueKind is JsonValueKind.Object)
                    {
                        yield return part;
                    }
                }
            }
        }
    }

    /// <summary>The vendor's own reason when a 200 carries no answer — a safety block or a finish reason.</summary>
    private static string Blocked(JsonElement root)
    {
        if (root.TryGetProperty("promptFeedback", out var feedback) && Payload.Text(feedback, "blockReason") is { } reason)
        {
            return $" (blocked: {reason})";
        }

        if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind is JsonValueKind.Array
            && candidates.GetArrayLength() > 0 && Payload.Text(candidates[0], "finishReason") is { } finish)
        {
            return $" (finish reason: {finish})";
        }

        return string.Empty;
    }

    /// <summary><c>audio/L16;codec=pcm;rate=24000</c> → 24000. 24 kHz is what every Gemini TTS model has answered.</summary>
    private static int SampleRate(string? mimeType)
    {
        foreach (var parameter in (mimeType ?? string.Empty).Split(';'))
        {
            var pair = parameter.Split('=', 2);

            if (pair.Length == 2 && pair[0].Trim().Equals("rate", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rate) && rate > 0)
            {
                return rate;
            }
        }

        return 24000;
    }
}
