using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InferHub.Tests;

/// <summary>
/// OpenAI's Images and Audio APIs and the Gemini API's <c>:predict</c> / <c>:generateContent</c>, as one
/// handler (phase 104). It answers what the real vendors answer in shape — base64 PNGs, raw PCM, a
/// verbose transcript — and records every request so a test can read what left the hub.
/// </summary>
internal sealed class FakeMediaVendor : HttpMessageHandler
{
    public sealed record Seen(string Method, string Path, string? ContentType, IReadOnlyDictionary<string, string> Headers, byte[] Body, string Host = "")
    {
        public string Text => Encoding.UTF8.GetString(Body);

        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
    }

    public ConcurrentQueue<Seen> Requests { get; } = new();

    /// <summary>When set and it returns a status, the request is answered with it and this body.</summary>
    public Func<Seen, (HttpStatusCode Status, string Body, int? RetryAfter)?>? Fail { get; set; }

    /// <summary>The size of every PNG this vendor draws.</summary>
    public (int Width, int Height) DrawSize { get; set; } = (1024, 1024);

    public string TranscriptText { get; set; } = "the quick brown fox";

    public static readonly byte[] Mp3 = [.. "ID3"u8, 3, 0, 0, 0, 0, 0, 1, 0xFF, 0xFB, 0x90, 0x00];

    /// <summary>Phase 105: how many polls a Veo operation answers <c>done: false</c> before it is done.</summary>
    public int VeoPendingPolls { get; set; } = 1;

    /// <summary>Phase 105: where a finished operation says the clip is. Null means this vendor's own <c>/files/clip-1:download</c>.</summary>
    public string? VeoVideoUri { get; set; }

    /// <summary>Phase 105: the download answers a 302 to another host, as Google's file download does.</summary>
    public bool RedirectDownload { get; set; } = true;

    /// <summary>Phase 105: the length the clips this vendor renders claim in their MP4 header.</summary>
    public double ClipSeconds { get; set; } = 8;

    private int polls;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);

        if (request.Headers.Authorization is { } authorization)
        {
            headers["Authorization"] = authorization.ToString();
        }

        var seen = new Seen(
            request.Method.Method,
            request.RequestUri!.AbsolutePath,
            request.Content?.Headers.ContentType?.ToString(),
            headers,
            body,
            request.RequestUri.Host);

        Requests.Enqueue(seen);

        if (Fail?.Invoke(seen) is { } failure)
        {
            var response = new HttpResponseMessage(failure.Status) { Content = new StringContent(failure.Body, Encoding.UTF8, "application/json") };

            if (failure.RetryAfter is { } seconds)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return response;
        }

        var path = seen.Path;

        if (path.EndsWith("/images/generations", StringComparison.Ordinal))
        {
            var n = seen.Json.TryGetProperty("n", out var count) ? count.GetInt32() : 1;
            return Json(Pictures(n));
        }

        if (path.EndsWith("/images/edits", StringComparison.Ordinal) || path.EndsWith("/images/variations", StringComparison.Ordinal))
        {
            return Json(Pictures(1));
        }

        if (path.EndsWith("/audio/speech", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Mp3) };
        }

        if (path.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
        {
            var verbose = seen.Text.Contains("verbose_json", StringComparison.Ordinal);

            return Json(verbose
                ? new JsonObject
                {
                    ["task"] = "transcribe",
                    ["language"] = "english",
                    ["duration"] = 2.5,
                    ["text"] = TranscriptText,
                    ["segments"] = new JsonArray(new JsonObject { ["id"] = 0, ["start"] = 0.0, ["end"] = 2.5, ["text"] = TranscriptText })
                }
                : new JsonObject { ["text"] = TranscriptText, ["usage"] = new JsonObject { ["type"] = "tokens", ["input_tokens"] = 10 } });
        }

        // ---- phase 105: Veo's long-running operation, Google's file download, Gemini Omni --------------
        if (path.EndsWith(":predictLongRunning", StringComparison.Ordinal))
        {
            Interlocked.Exchange(ref polls, 0);
            return Json(new JsonObject { ["name"] = "models/veo-3.1-generate-preview/operations/op-1" });
        }

        if (path.Contains("/operations/", StringComparison.Ordinal))
        {
            if (Interlocked.Increment(ref polls) <= VeoPendingPolls)
            {
                return Json(new JsonObject { ["name"] = "models/veo-3.1-generate-preview/operations/op-1", ["done"] = false });
            }

            var uri = VeoVideoUri ?? $"https://{seen.Host}/v1beta/files/clip-1:download?alt=media";

            return Json(new JsonObject
            {
                ["name"] = "models/veo-3.1-generate-preview/operations/op-1",
                ["done"] = true,
                ["response"] = new JsonObject
                {
                    ["generateVideoResponse"] = new JsonObject
                    {
                        ["generatedSamples"] = new JsonArray(new JsonObject { ["video"] = new JsonObject { ["uri"] = uri } })
                    }
                }
            });
        }

        if (path.EndsWith("/files/clip-1:download", StringComparison.Ordinal))
        {
            if (RedirectDownload)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://storage.example/clip-1.mp4?signature=x");
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Mp4(ClipSeconds)) };
        }

        if (seen.Host == "storage.example")
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Mp4(ClipSeconds)) };
        }

        if (path.EndsWith("/interactions", StringComparison.Ordinal))
        {
            return Json(new JsonObject
            {
                ["id"] = "v1_1",
                ["object"] = "interaction",
                ["status"] = "completed",
                ["model"] = seen.Json.GetProperty("model").GetString(),
                ["steps"] = new JsonArray(
                    new JsonObject { ["type"] = "user_input", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "…" }) },
                    new JsonObject
                    {
                        ["type"] = "model_output",
                        ["content"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "video",
                            ["mime_type"] = "video/mp4",
                            ["data"] = Convert.ToBase64String(Mp4(ClipSeconds))
                        })
                    })
            });
        }

        if (path.EndsWith(":predict", StringComparison.Ordinal))
        {
            var n = seen.Json.GetProperty("parameters").TryGetProperty("sampleCount", out var count) ? count.GetInt32() : 1;
            var predictions = new JsonArray();

            for (var i = 0; i < n; i++)
            {
                predictions.Add(new JsonObject { ["mimeType"] = "image/png", ["bytesBase64Encoded"] = Convert.ToBase64String(Png(DrawSize.Width, DrawSize.Height)) });
            }

            return Json(new JsonObject { ["predictions"] = predictions });
        }

        if (path.EndsWith(":generateContent", StringComparison.Ordinal))
        {
            var modalities = seen.Json.TryGetProperty("generationConfig", out var config) && config.TryGetProperty("responseModalities", out var list)
                ? list.EnumerateArray().Select(m => m.GetString()).ToArray()
                : [];

            JsonObject part = modalities.Contains("AUDIO")
                ? new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = "audio/L16;codec=pcm;rate=24000", ["data"] = Convert.ToBase64String(new byte[4800]) } }
                : modalities.Contains("IMAGE")
                    ? new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(Png(DrawSize.Width, DrawSize.Height)) } }
                    : new JsonObject { ["text"] = TranscriptText };

            return Json(new JsonObject
            {
                ["candidates"] = new JsonArray(new JsonObject
                {
                    ["content"] = new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(part) },
                    ["finishReason"] = "STOP"
                })
            });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":{"message":"no such route"}}""") };
    }

    private JsonObject Pictures(int n)
    {
        var data = new JsonArray();

        for (var i = 0; i < n; i++)
        {
            data.Add(new JsonObject { ["b64_json"] = Convert.ToBase64String(Png(DrawSize.Width, DrawSize.Height)) });
        }

        return new JsonObject { ["created"] = 1, ["data"] = data };
    }

    private static HttpResponseMessage Json(JsonNode node) =>
        new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    /// <summary>A PNG signature and an IHDR chunk carrying the size — all a header reader looks at.</summary>
    public static byte[] Png(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        bytes[24] = 8;
        bytes[25] = 6;
        return bytes;
    }

    /// <summary>
    /// The smallest MP4 a header reader walks: <c>ftyp</c>, then <c>moov</c> holding an <c>mvhd</c> whose
    /// timescale is 1000 and whose duration is the clip's length — and some <c>mdat</c> padding.
    /// </summary>
    public static byte[] Mp4(double seconds)
    {
        var bytes = new byte[16 + 8 + 100 + 64];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 16);
        "ftypisom"u8.CopyTo(bytes.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 108);
        "moov"u8.CopyTo(bytes.AsSpan(20));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), 100);
        "mvhd"u8.CopyTo(bytes.AsSpan(28));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(44), 1000);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(48), (uint)(seconds * 1000));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(124), 64);
        "mdat"u8.CopyTo(bytes.AsSpan(128));
        return bytes;
    }

    /// <summary>A canonical 16-bit mono WAV of the given length in seconds at 16 kHz.</summary>
    public static byte[] Wav(double seconds)
    {
        var samples = (int)(16000 * seconds);
        var data = new byte[samples * 2];
        var wav = new byte[44 + data.Length];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + data.Length);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), data.Length);
        return wav;
    }

    /// <summary>A handler that lends this one to an <c>HttpClient</c> without letting it dispose it.</summary>
    public HttpMessageHandler Borrowed() => new BorrowedHandler(this);

    private sealed class BorrowedHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}
