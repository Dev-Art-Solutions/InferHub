using System.Text;
using System.Text.Json;

// The echo worker: a real child process that speaks the phase-41 tool protocol and can be told to
// fail in every way the runtime claims to survive. Every behaviour below exists because a test
// asserts it; nothing here is a demonstration.
//
//   inferhub-echo-worker [--no-ready] [--exit-on-start] [--slow-ready <ms>]
//                        [--capabilities <kind>:<model>,<model>;<kind>:<model>]
//                        [--audio-fail <code>] [--audio-no-segments]
//
// Phase 42 added two behaviours that are chosen by the request's *capability* rather than by a
// "behaviour" field, because the audio edge builds the worker payload itself and a client cannot
// reach into it:
//
//   transcribe  answer with a canned verbose transcript (text + segments + duration)
//   speak       write a real RIFF wav into the scratch directory and name it back; refuse any
//               response_format other than wav/pcm with an `unsupported_format` code, which is how
//               a box with no ffmpeg behaves and is what the edge renders as a 400
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

void Send(object frame) => stdout.WriteLine(JsonSerializer.Serialize(frame, json));

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
            Send(new { type = "pong" });
            continue;

        case "request":
            await HandleRequestAsync(frame);
            continue;

        default:
            continue;
    }
}

return 0;

async Task HandleRequestAsync(JsonElement frame)
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

    if (behaviour is null && capability is "transcribe" or "speak")
    {
        await HandleAudioAsync(id, capability, payload, frame);
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
    bool AudioNoSegments = false)
{
    public static Args Parse(string[] args)
    {
        var noReady = false;
        var exitOnStart = false;
        var slowReadyMs = 0;
        object[]? capabilities = null;
        string? audioFailCode = null;
        var audioNoSegments = false;

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
            }
        }

        return new Args(noReady, exitOnStart, slowReadyMs, capabilities, audioFailCode, audioNoSegments);
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
