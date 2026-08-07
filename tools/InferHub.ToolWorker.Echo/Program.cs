using System.Text;
using System.Text.Json;

// The echo worker: a real child process that speaks the phase-41 tool protocol and can be told to
// fail in every way the runtime claims to survive. Every behaviour below exists because a test
// asserts it; nothing here is a demonstration.
//
//   inferhub-echo-worker [--no-ready] [--exit-on-start] [--slow-ready <ms>]
//                        [--capabilities <kind>:<model>,<model>;<kind>:<model>]
//                        [--audio-fail <code>] [--audio-no-segments]
//                        [--image-fail <code>] [--image-pad-bytes <n>]
//                        [--redeclare-on-ping <kind>:<model>,<model>] [--wedge-on-ping]
//
// Phase 42 added two behaviours that are chosen by the request's *capability* rather than by a
// "behaviour" field, because the audio edge builds the worker payload itself and a client cannot
// reach into it:
//
//   transcribe  answer with a canned verbose transcript (text + segments + duration)
//   speak       write a real RIFF wav into the scratch directory and name it back; refuse any
//               response_format other than wav/pcm with an `unsupported_format` code, which is how
//               a box with no ffmpeg behaves and is what the edge renders as a 400
//   image       (phase 46) write `n` real PNGs into the scratch directory and name them back, with
//               the width, height, steps and seed reported per image; refuse a size outside the
//               fixture's aspect buckets with `invalid_request` naming them, which is how a real
//               diffusion recipe answers and is the path the edge deliberately leaves to the worker
//
// Behaviours are asked for in the request payload's "behaviour" field:
//   (absent)   echo the payload back
//   chunks     emit "count" chunk frames, then a result
//   sleep      sleep "seconds" then answer  (drives requestTimeoutSeconds)
//   wedge      never answer, never exit     (drives the kill path)
//   exit       Environment.Exit mid-request (drives the died-mid-request path)
//   error      answer with an error frame   (a failed job, a live worker)
//   stderr     write "message" to stderr, then answer
//   env        answer with the value of the environment variable named in "name" — or null.
//              This is the probe for the D3 environment-inheritance test, and it must run in a
//              real process to mean anything.
//   files      read every input file, write one output file into the scratch directory, and name
//              it back in the result

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
};

var arguments = Args.Parse(args);

var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

if (arguments.ExitOnStart)
{
    return 3;
}

// stdout is the protocol and one frame is one line, so a progress frame written from a request
// task must not interleave with the read loop's own writes.
var writeLock = new object();

void Send(object frame)
{
    lock (writeLock)
    {
        stdout.WriteLine(JsonSerializer.Serialize(frame, json));
    }
}

// Phase 48. Models this worker has "pulled", so a second pull of the same one coalesces into
// "already-present" rather than pretending to download it again — which is the behaviour a real
// worker has, because it checks its readiness marker first.
var pulledModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

// Phase 47. The requests currently running, so a `cancel` frame can find one.
var inFlight = new System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>();
Task? current = null;

while (await stdin.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    JsonElement frame;

    try
    {
        frame = JsonDocument.Parse(line).RootElement;
    }
    catch (JsonException)
    {
        // A worker that dies on a line it does not understand is a worker that dies on the day
        // somebody adds a field. Ignore it.
        continue;
    }

    var type = frame.TryGetProperty("type", out var t) ? t.GetString() : null;

    switch (type)
    {
        case "hello":
            if (arguments.SlowReadyMs > 0)
            {
                await Task.Delay(arguments.SlowReadyMs);
            }

            if (arguments.NoReady)
            {
                // Deliberately silent. The node must kill this after startTimeoutSeconds rather
                // than wait on a read that will never return.
                continue;
            }

            Send(new
            {
                type = "ready",
                protocol = 1,
                capabilities = arguments.Capabilities
            });
            continue;

        case "ping":
            if (arguments.WedgeOnPing)
            {
                // Alive, and no longer answering. `IsAlive` is true, so only the liveness probe can
                // tell — which is the case phase-41 D6 wrote the probe for.
                continue;
            }

            // A LATE READY, before the pong (v3.14.1). A worker whose answer to "what can you do"
            // changed while it ran — weights that finished downloading — says so out of band, and
            // an idle worker's stdout is only drained by this probe. Emitting it here rather than
            // on a timer makes the test deterministic.
            if (arguments.RedeclareOnPing is { } later)
            {
                arguments = arguments with { RedeclareOnPing = null };
                Send(new { type = "ready", protocol = 1, capabilities = later });
            }

            Send(new { type = "pong" });
            continue;

        case "request":
            // Phase 47. A request is handled on a background task so the read loop stays free to
            // pick up the `cancel` frame that arrives while it is running. A worker that only reads
            // between requests cannot be cancelled at all, which is the single most common way a
            // first cancellable worker is written wrong — and it looks exactly like a hang.
            {
                var id = frame.TryGetProperty("id", out var requestId) ? requestId.GetString() : null;
                var cancellation = new CancellationTokenSource();

                if (id is not null)
                {
                    inFlight[id] = cancellation;
                }

                current = HandleRequestAsync(frame, cancellation.Token).ContinueWith(
                    finished =>
                    {
                        if (id is not null)
                        {
                            inFlight.TryRemove(id, out _);
                        }

                        cancellation.Dispose();
                        _ = finished;
                    },
                    TaskScheduler.Default);
            }

            continue;

        case "cancel":
            {
                var id = frame.TryGetProperty("id", out var requestId) ? requestId.GetString() : null;

                if (id is not null && inFlight.TryGetValue(id, out var cancellation))
                {
                    if (arguments.IgnoreCancel)
                    {
                        // Deliberately deaf, so the node's cancel grace has something real to
                        // expire against and the terminate-and-restart path is exercised by a
                        // process that actually refuses rather than by a mock that pretends to.
                        continue;
                    }

                    cancellation.Cancel();
                }
            }

            continue;

        default:
            continue;
    }
}

// Let a request that is still running finish writing its frame before stdout goes away.
if (current is not null)
{
    try
    {
        await current.WaitAsync(TimeSpan.FromSeconds(5));
    }
    catch (Exception)
    {
    }
}

return 0;

async Task HandleRequestAsync(JsonElement frame, CancellationToken cancellationToken)
{
    var id = frame.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
    var payload = frame.TryGetProperty("payload", out var p) && p.ValueKind is not JsonValueKind.Null
        ? p
        : default;

    var behaviour = payload.ValueKind is JsonValueKind.Object
        && payload.TryGetProperty("behaviour", out var b)
            ? b.GetString()
            : null;

    var capability = frame.TryGetProperty("capability", out var kind) ? kind.GetString() : null;

    // Phase 48. A hub-issued model command reaches a tool as a request whose payload names an `op`,
    // and answering it here is what makes the whole pull path — coalescing, the SSE relay, a
    // progress bar in the console — testable with no GPU, no network and no weights. Same
    // discipline as phase 41's echo worker and phase 47's slow image mode.
    if (payload.ValueKind is JsonValueKind.Object
        && payload.TryGetProperty("op", out var opElement)
        && opElement.GetString() is { } op and ("pull" or "delete"))
    {
        await HandleModelCommandAsync(id, op, frame, payload, cancellationToken);
        return;
    }

    if (behaviour is null && capability is "transcribe" or "speak")
    {
        await HandleAudioAsync(id, capability, payload, frame);
        return;
    }

    if (behaviour is null && capability is "image")
    {
        await HandleImageAsync(id, payload, frame, cancellationToken);
        return;
    }

    switch (behaviour)
    {
        case "chunks":
        {
            var count = payload.TryGetProperty("count", out var c) ? c.GetInt32() : 3;

            for (var i = 0; i < count; i++)
            {
                Send(new { type = "chunk", id, payload = new { index = i } });
            }

            Send(new { type = "result", id, payload = new { chunks = count } });
            return;
        }

        case "sleep":
        {
            var seconds = payload.TryGetProperty("seconds", out var s) ? s.GetDouble() : 1.0;
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            Send(new { type = "result", id, payload = new { slept = seconds } });
            return;
        }

        case "wedge":
            // Accepted and never answered. The request deadline is the only thing that ends this.
            await Task.Delay(Timeout.Infinite);
            return;

        case "exit":
        {
            var code = payload.TryGetProperty("code", out var e) ? e.GetInt32() : 9;
            stdout.Flush();
            Environment.Exit(code);
            return;
        }

        case "error":
        {
            var message = payload.TryGetProperty("message", out var m) ? m.GetString() : "the echo worker was asked to fail";
            Send(new { type = "error", id, message });
            return;
        }

        case "stderr":
        {
            var message = payload.TryGetProperty("message", out var m) ? m.GetString() : "echo worker stderr";
            await Console.Error.WriteLineAsync(message);
            await Console.Error.FlushAsync();
            Send(new { type = "result", id, payload = new { wroteToStderr = true } });
            return;
        }

        case "env":
        {
            var name = payload.TryGetProperty("name", out var n) ? n.GetString() : null;
            var value = name is null ? null : Environment.GetEnvironmentVariable(name);

            // "present: false" rather than omitting the key: a test that asserts on an absent
            // field cannot tell "not leaked" from "the worker did not answer the question".
            Send(new
            {
                type = "result",
                id,
                payload = new { name, present = value is not null, value }
            });
            return;
        }

        case "escape":
        {
            // Names a file outside the scratch directory. The node must refuse to read it — this
            // is the one behaviour where the worker is the adversary, and the check that stops it
            // has to be exercised by a real child process naming a real path.
            var path = payload.TryGetProperty("path", out var pth) ? pth.GetString() : null;

            Send(new
            {
                type = "result",
                id,
                payload = new { escaped = true },
                files = new[] { new { name = "stolen", mediaType = "text/plain", path } }
            });
            return;
        }

        case "files":
        {
            var scratch = frame.TryGetProperty("scratch", out var s) ? s.GetString() : null;
            var inputs = new List<object>();

            if (frame.TryGetProperty("files", out var files) && files.ValueKind is JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    var path = file.GetProperty("path").GetString()!;
                    var bytes = await File.ReadAllBytesAsync(path);

                    inputs.Add(new
                    {
                        name = file.GetProperty("name").GetString(),
                        bytes = bytes.Length,
                        text = Encoding.UTF8.GetString(bytes)
                    });
                }
            }

            object? outputs = null;

            if (scratch is not null)
            {
                var outPath = Path.Combine(scratch, "echo-output.txt");
                await File.WriteAllTextAsync(outPath, $"echoed {inputs.Count} file(s)");
                outputs = new[]
                {
                    new { name = "echo-output.txt", mediaType = "text/plain", path = outPath }
                };
            }

            Send(new
            {
                type = "result",
                id,
                payload = new { received = inputs },
                files = outputs
            });
            return;
        }

        default:
            Send(new
            {
                type = "result",
                id,
                payload = new
                {
                    tool = "echo",
                    capability = frame.TryGetProperty("capability", out var cap) ? cap.GetString() : null,
                    model = frame.TryGetProperty("model", out var mod) ? mod.GetString() : null,
                    echoed = payload.ValueKind is JsonValueKind.Undefined ? null : (object)payload
                }
            });
            return;
    }
}

// ---- phase 42: the audio behaviours ------------------------------------------------------------

async Task HandleAudioAsync(string? id, string capability, JsonElement payload, JsonElement frame)
{
    if (arguments.AudioFailCode is { } code)
    {
        // A worker naming which *kind* of failure this was. The edge renders a 400 for a client
        // error and a 502 for anything else, without ever reading this message.
        Send(new { type = "error", id, code, message = $"the echo worker was asked to fail with {code}" });
        return;
    }

    if (capability == "transcribe")
    {
        // A canned verbose transcript. The distinctive phrase is what AudioPrivacyTests looks for
        // in the logs and the ledger — it must appear in the response and nowhere else.
        var segments = arguments.AudioNoSegments
            ? Array.Empty<object>()
            :
            [
                new { id = 0, start = 0.0, end = 1.5, text = " The quick brown fox" },
                new { id = 1, start = 1.5, end = 3.25, text = " jumps over the lazy dog." }
            ];

        Send(new
        {
            type = "result",
            id,
            payload = new
            {
                text = "The quick brown fox jumps over the lazy dog.",
                language = "en",
                duration = 3.25,
                segments
            }
        });

        return;
    }

    var format = payload.ValueKind is JsonValueKind.Object
        && payload.TryGetProperty("response_format", out var f)
            ? f.GetString() ?? "wav"
            : "wav";

    if (format is not ("wav" or "pcm"))
    {
        // Exactly how a box without ffmpeg behaves: it refuses and names what it can do. Never a
        // substitution — an mp3 that is really a wav is a corrupted file with a confident content
        // type, and the caller finds out in a media player.
        Send(new
        {
            type = "error",
            id,
            code = "unsupported_format",
            message = $"this worker cannot produce '{format}'. It can produce: wav, pcm"
        });

        return;
    }

    var scratch = frame.TryGetProperty("scratch", out var s) ? s.GetString() : null;

    if (scratch is null)
    {
        Send(new { type = "error", id, message = "no scratch directory" });
        return;
    }

    var name = format == "wav" ? "speech.wav" : "speech.pcm";
    var path = Path.Combine(scratch, name);
    await File.WriteAllBytesAsync(path, format == "wav" ? Wav.Silence(0.25) : Wav.Pcm(0.25));

    Send(new
    {
        type = "result",
        id,
        payload = new { format, characters = payload.TryGetProperty("input", out var i) ? (i.GetString() ?? "").Length : 0 },
        files = new[]
        {
            new { name, mediaType = format == "wav" ? "audio/wav" : "audio/pcm", path }
        }
    });
}

// ---- phase 48: model commands ------------------------------------------------------------------

/// <summary>
/// A <c>pull</c> or a <c>delete</c> against this worker's model catalogue.
/// </summary>
/// <remarks>
/// <para>
/// A pull emits a <c>chunk</c> per step with a <c>status</c> string and ends with a result — the
/// same shape the diffusion worker produces, so what the node relays onto the phase-26 progress
/// channel is exercised for real rather than stubbed.
/// </para>
/// <para>
/// <b>The status carries no percentage, deliberately.</b> Hugging Face gives no download callback,
/// and a denominator a worker had to guess is a number a dashboard would happily plot — phase-28
/// D5's line. What it reports instead is a fact: how much has landed.
/// </para>
/// </remarks>
async Task HandleModelCommandAsync(
    string? id,
    string op,
    JsonElement frame,
    JsonElement payload,
    CancellationToken cancellationToken)
{
    var model = frame.TryGetProperty("model", out var m) ? m.GetString() ?? "unknown" : "unknown";

    if (payload.TryGetProperty("fail", out var fail) && fail.ValueKind is JsonValueKind.String)
    {
        Send(new { type = "error", id, code = "model_unavailable", message = fail.GetString() });
        return;
    }

    if (op is "delete")
    {
        pulledModels.Remove(model);
        Send(new { type = "result", id, payload = new { model, status = "deleted", freedBytes = 4096 } });
        return;
    }

    if (!pulledModels.Add(model))
    {
        Send(new { type = "chunk", id, payload = new { status = "already-present", model } });
        Send(new { type = "result", id, payload = new { model, status = "already-present", ready = true } });
        return;
    }

    var stepMs = payload.TryGetProperty("stepMs", out var ms) && ms.ValueKind is JsonValueKind.Number
        ? ms.GetInt32()
        : 5;

    foreach (var landed in new[] { 128, 512, 1024 })
    {
        await Task.Delay(stepMs, cancellationToken);
        Send(new { type = "chunk", id, payload = new { status = $"downloading ({landed} MiB)", model, mib = landed } });
    }

    Send(new { type = "chunk", id, payload = new { status = "verifying", model } });
    Send(new { type = "result", id, payload = new { model, status = "ready", ready = true } });
}

// ---- phase 46: the image behaviour -------------------------------------------------------------

/// <summary>
/// Answers an <c>image</c> job with real PNG files written into the scratch directory.
/// </summary>
/// <remarks>
/// <para>
/// The <b>sizes</b> this fixture accepts are a stand-in for a recipe's aspect buckets, and it
/// refuses anything else with <c>invalid_request</c> naming them — which is how a real diffusion
/// worker answers, and is the path phase 46 deliberately left to the worker rather than to the edge:
/// a recipe is a file on the node and the hub has no model catalogue until phase 48.
/// </para>
/// <para>
/// The bytes are a real PNG with a real IHDR, because the alternative is a test that passes on 200
/// bytes of zeros. Phase 42 learned this with a WAV header and it is the same lesson.
/// </para>
/// </remarks>
async Task HandleImageAsync(string? id, JsonElement payload, JsonElement frame, CancellationToken cancellationToken)
{
    if (arguments.ImageFailCode is { } failure)
    {
        Send(new { type = "error", id, code = failure, message = $"the echo worker was asked to fail with {failure}" });
        return;
    }

    var width = payload.ValueKind is JsonValueKind.Object && payload.TryGetProperty("width", out var w) && w.ValueKind is JsonValueKind.Number
        ? w.GetInt32()
        : 512;

    var height = payload.ValueKind is JsonValueKind.Object && payload.TryGetProperty("height", out var h) && h.ValueKind is JsonValueKind.Number
        ? h.GetInt32()
        : 512;

    if ((width, height) is not ((512, 512) or (768, 768) or (1024, 1024) or (640, 384)))
    {
        Send(new
        {
            type = "error",
            id,
            code = "invalid_request",
            message = $"this recipe cannot render {width}x{height}. It was trained on: 512x512, 768x768, 1024x1024, 640x384"
        });

        return;
    }

    var steps = payload.ValueKind is JsonValueKind.Object && payload.TryGetProperty("steps", out var st) && st.ValueKind is JsonValueKind.Number
        ? st.GetInt32()
        : 30;

    var count = payload.ValueKind is JsonValueKind.Object && payload.TryGetProperty("n", out var n) && n.ValueKind is JsonValueKind.Number
        ? n.GetInt32()
        : 1;

    var seed = payload.ValueKind is JsonValueKind.Object && payload.TryGetProperty("seed", out var s) && s.ValueKind is JsonValueKind.Number
        ? s.GetInt64()
        : 4242;

    var scratch = frame.TryGetProperty("scratch", out var sc) ? sc.GetString() : null;

    if (scratch is null)
    {
        Send(new { type = "error", id, message = "no scratch directory" });
        return;
    }

    // Phase 47. `--image-step-ms N` makes each diffusion step take real time and emit a real
    // progress frame, which is what lets the whole job model — SSE, monotonic progress, cooperative
    // cancel, a worker that stays warm — be tested on a machine with no GPU and no weights. Every
    // acceptance criterion in the phase is reachable from here.
    if (arguments.ImageStepMs > 0)
    {
        for (var step = 1; step <= steps; step++)
        {
            try
            {
                await Task.Delay(arguments.ImageStepMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Answered from `callback_on_step_end`, exactly as a real diffusion worker does:
                // an error frame coded `cancelled`, and then this process is still alive, still
                // holding its weights, and ready for the next request. That is the whole reason
                // cancel is a frame and not a kill.
                Send(new
                {
                    type = "error",
                    id,
                    code = "cancelled",
                    message = $"cancelled at step {step} of {steps}"
                });

                return;
            }

            Send(new { type = "progress", id, step, totalSteps = steps });
        }
    }

    var files = new List<object>(count);
    var images = new List<object>(count);

    for (var i = 0; i < count; i++)
    {
        var name = $"image-{i}.png";
        var path = Path.Combine(scratch, name);

        // `--image-bytes N` pads the file, so ImageWireSizeTests can push megabytes across a real
        // SignalR connection. The 32 KB default cap tore the connection down rather than failing the
        // message, and phase 41 "proved" attachments with a 16-byte file.
        await File.WriteAllBytesAsync(path, Png.Create(width, height, arguments.ImagePadBytes));

        files.Add(new { name, mediaType = "image/png", path });
        images.Add(new { width, height, steps, seed = seed + i });
    }

    Send(new
    {
        type = "result",
        id,
        payload = new { model = frame.TryGetProperty("model", out var m) ? m.GetString() : null, steps, images },
        files
    });
}

/// <summary>
/// A structurally valid PNG: signature, IHDR, a zlib-stored IDAT of real scanlines, IEND.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taken from a library for design rule 5's reason — this worker has no
/// project references and is not in any image — and real rather than random for phase-42's reason:
/// an assertion on <c>Content-Length</c> passes just as happily on the wrong bytes, so the test
/// reads the IHDR back and checks the dimensions the worker claimed.
/// </remarks>
internal static class Png
{
    public static byte[] Create(int width, int height, int padBytes)
    {
        using var buffer = new MemoryStream();

        buffer.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 2;   // truecolour
        Chunk(buffer, "IHDR", ihdr);

        // One filter byte plus three bytes per pixel, per scanline. A diagonal ramp rather than a
        // constant, so a truncation is visible to anything that actually decodes it.
        var raw = new byte[height * (1 + (width * 3))];
        var at = 0;

        for (var y = 0; y < height; y++)
        {
            raw[at++] = 0;

            for (var x = 0; x < width; x++)
            {
                raw[at++] = (byte)(x & 0xFF);
                raw[at++] = (byte)(y & 0xFF);
                raw[at++] = (byte)((x + y) & 0xFF);
            }
        }

        Chunk(buffer, "IDAT", ZlibStored(raw));

        if (padBytes > 0)
        {
            // An ancillary chunk (lowercase first letter) a decoder skips. Padding the IDAT would
            // corrupt the image; padding here keeps the file both valid and as large as asked for.
            Chunk(buffer, "teXt", new byte[padBytes]);
        }

        Chunk(buffer, "IEND", []);
        return buffer.ToArray();
    }

    private static void Chunk(Stream target, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        target.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        target.Write(typeBytes);
        target.Write(data);

        var crc = new byte[4];
        WriteBigEndian(crc, 0, (int)Crc32(typeBytes, data));
        target.Write(crc);
    }

    /// <summary>zlib with stored (uncompressed) deflate blocks — no compressor, still a valid stream.</summary>
    private static byte[] ZlibStored(byte[] data)
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte(0x78);
        buffer.WriteByte(0x01);

        var offset = 0;

        while (offset < data.Length)
        {
            var block = Math.Min(0xFFFF, data.Length - offset);
            var final = offset + block >= data.Length;

            buffer.WriteByte((byte)(final ? 1 : 0));
            buffer.WriteByte((byte)(block & 0xFF));
            buffer.WriteByte((byte)((block >> 8) & 0xFF));
            buffer.WriteByte((byte)(~block & 0xFF));
            buffer.WriteByte((byte)((~block >> 8) & 0xFF));
            buffer.Write(data, offset, block);

            offset += block;
        }

        uint a = 1, b = 0;

        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        var adler = new byte[4];
        WriteBigEndian(adler, 0, (int)((b << 16) | a));
        buffer.Write(adler);

        return buffer.ToArray();
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)((value >> 24) & 0xFF);
        target[offset + 1] = (byte)((value >> 16) & 0xFF);
        target[offset + 2] = (byte)((value >> 8) & 0xFF);
        target[offset + 3] = (byte)(value & 0xFF);
    }

    private static uint Crc32(byte[] first, byte[] second)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var value in first)
        {
            crc = Step(crc, value);
        }

        foreach (var value in second)
        {
            crc = Step(crc, value);
        }

        return crc ^ 0xFFFFFFFFu;

        static uint Step(uint crc, byte value)
        {
            crc ^= value;

            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }

            return crc;
        }
    }
}

/// <summary>
/// A real RIFF/WAVE file, not a byte array with a plausible length. A test that asserts on
/// `Content-Length` passes just as happily on 44 bytes of zeros, and "the header is actually a
/// header" is the one thing an automated audio assertion can honestly check.
/// </summary>
internal static class Wav
{
    private const int SampleRate = 16000;

    public static byte[] Pcm(double seconds)
    {
        var samples = (int)(SampleRate * seconds);
        var pcm = new byte[samples * 2];

        // A quiet 440 Hz tone rather than silence: a file of zeros compresses to nothing and would
        // hide a truncation bug behind a plausible byte count.
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * 440 * i / SampleRate) * 3000);
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[(i * 2) + 1] = (byte)((value >> 8) & 0xFF);
        }

        return pcm;
    }

    public static byte[] Silence(double seconds)
    {
        var pcm = Pcm(seconds);
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);              // PCM
        writer.Write((short)1);              // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);        // byte rate
        writer.Write((short)2);              // block align
        writer.Write((short)16);             // bits per sample
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();

        return buffer.ToArray();
    }
}

internal sealed record Args(
    bool NoReady,
    bool ExitOnStart,
    int SlowReadyMs,
    object[]? Capabilities,
    string? AudioFailCode = null,
    bool AudioNoSegments = false,
    string? ImageFailCode = null,
    int ImagePadBytes = 0,
    object[]? RedeclareOnPing = null,
    bool WedgeOnPing = false,
    int ImageStepMs = 0,
    bool IgnoreCancel = false)
{
    public static Args Parse(string[] args)
    {
        var noReady = false;
        var exitOnStart = false;
        var slowReadyMs = 0;
        object[]? capabilities = null;
        string? audioFailCode = null;
        var audioNoSegments = false;
        string? imageFailCode = null;
        var imagePadBytes = 0;
        object[]? redeclareOnPing = null;
        var wedgeOnPing = false;
        var imageStepMs = 0;
        var ignoreCancel = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--no-ready":
                    noReady = true;
                    break;
                case "--exit-on-start":
                    exitOnStart = true;
                    break;
                case "--slow-ready" when i + 1 < args.Length:
                    slowReadyMs = int.Parse(args[++i]);
                    break;
                case "--capabilities" when i + 1 < args.Length:
                    capabilities = ParseCapabilities(args[++i]);
                    break;
                case "--audio-fail" when i + 1 < args.Length:
                    audioFailCode = args[++i];
                    break;
                case "--audio-no-segments":
                    audioNoSegments = true;
                    break;
                case "--image-fail" when i + 1 < args.Length:
                    imageFailCode = args[++i];
                    break;
                case "--image-pad-bytes" when i + 1 < args.Length:
                    imagePadBytes = int.Parse(args[++i]);
                    break;
                case "--wedge-on-ping":
                    wedgeOnPing = true;
                    break;
                case "--redeclare-on-ping" when i + 1 < args.Length:
                    redeclareOnPing = ParseCapabilities(args[++i]);
                    break;
                case "--image-step-ms" when i + 1 < args.Length:
                    imageStepMs = int.Parse(args[++i]);
                    break;
                case "--ignore-cancel":
                    ignoreCancel = true;
                    break;
            }
        }

        return new Args(
            noReady,
            exitOnStart,
            slowReadyMs,
            capabilities,
            audioFailCode,
            audioNoSegments,
            imageFailCode,
            imagePadBytes,
            redeclareOnPing,
            wedgeOnPing,
            imageStepMs,
            ignoreCancel);
    }

    /// <summary>"transcribe:a,b;speak:c" → the capability list a ready frame carries.</summary>
    private static object[] ParseCapabilities(string spec) =>
        spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .Select(object (parts) => new
            {
                kind = parts[0].Trim(),
                models = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            })
            .ToArray();
}
