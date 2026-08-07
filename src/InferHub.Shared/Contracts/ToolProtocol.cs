using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// The node ↔ worker line protocol (phase 41, D5): one JSON object per line, UTF-8, on the child
/// process's stdin and stdout.
/// </summary>
/// <remarks>
/// <para>
/// <b>The node speaks a process protocol, not Python</b> (D1). Nothing here knows what language a
/// worker is written in; the first two shipped workers happen to be Python, and a worker that is a
/// Go binary, an <c>ffmpeg</c> invocation or a vendor's CLI wrapper is the same thing to this file.
/// </para>
/// <para>
/// <b>Binary does not travel on the pipe.</b> Attachments are written to a per-request scratch
/// directory and a frame carries the <em>path</em>; the worker writes its output beside it and
/// names it back. Base64 over stdio was rejected: it is 4/3 the bytes and it materialises the whole
/// payload as a string in both runtimes at once — a 25 MB audio file becomes ~33 MB of .NET string
/// plus ~33 MB of Python <c>str</c> plus the decoded copies, for a handoff both sides' libraries
/// would rather do with a path anyway.
/// </para>
/// <para>
/// <c>stderr</c> is deliberately <b>not</b> protocol. It is pumped into the node's log under the
/// tool's id, because that is where a Python traceback goes and a traceback is the single most
/// useful thing a tool author ever sees. A worker that wants a structured line uses a
/// <see cref="ToolFrameTypes.Log"/> frame.
/// </para>
/// </remarks>
public static class ToolProtocol
{
    /// <summary>
    /// Bumped only on a breaking change to the frame shapes. A worker announcing a version this
    /// node does not know is refused at the handshake, with both numbers in the message — the
    /// alternative is a worker that starts, half-works, and fails on the request nobody expected.
    /// </summary>
    public const int Version = 1;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Serializes a frame to exactly one line. <c>System.Text.Json</c> escapes control characters,
    /// so a payload containing newlines cannot break the framing — which is the whole reason the
    /// transport is "one JSON object per line" rather than a length prefix nobody can read in a
    /// terminal.
    /// </summary>
    public static string Write(ToolFrame frame) => JsonSerializer.Serialize(frame, Json);

    /// <summary>
    /// Parses a line, returning null for anything that is not a frame. A worker that prints a
    /// stray line to stdout — a library's progress bar, a warning — must not take the process down;
    /// the caller logs it and keeps reading.
    /// </summary>
    public static ToolFrame? TryRead(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            var frame = JsonSerializer.Deserialize<ToolFrame>(line, Json);
            return string.IsNullOrWhiteSpace(frame?.Type) ? null : frame;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The frame types. Node → worker: hello, request, ping. Worker → node: the rest.</summary>
public static class ToolFrameTypes
{
    /// <summary>Node → worker, once, immediately after spawn. Carries the protocol version.</summary>
    public const string Hello = "hello";

    /// <summary>
    /// Worker → node, once, in reply to <see cref="Hello"/>. Until it arrives the worker is
    /// starting; past <c>startTimeoutSeconds</c> it is killed.
    /// </summary>
    public const string Ready = "ready";

    /// <summary>Node → worker. One at a time per worker; concurrency is <c>maxWorkers</c>.</summary>
    public const string Request = "request";

    /// <summary>Worker → node. A partial answer. Optional — a worker may go straight to a result.</summary>
    public const string Chunk = "chunk";

    /// <summary>Worker → node. Terminal, and the worker is idle again afterwards.</summary>
    public const string Result = "result";

    /// <summary>Worker → node. Terminal for the request; the worker stays alive.</summary>
    public const string Error = "error";

    /// <summary>Worker → node. A structured log line, at any time.</summary>
    public const string Log = "log";

    /// <summary>Node → worker. Liveness probe for an idle worker.</summary>
    public const string Ping = "ping";

    /// <summary>Worker → node. The reply to <see cref="Ping"/>.</summary>
    public const string Pong = "pong";

    /// <summary>
    /// Worker → node, any number of times during a request (phase 47): <c>{step, totalSteps}</c>.
    /// </summary>
    /// <remarks>
    /// <b>Additive, and silence is the old behaviour.</b> A worker written against 3.14 never sends
    /// one and behaves exactly as it did; a caller who never watches gets exactly the response they
    /// got before. It is separate from <see cref="Chunk"/> because a chunk is a <em>partial answer</em>
    /// and this is not an answer at all — conflating them would put "7 of 28" into the body of a
    /// streaming tool response, where a client is entitled to expect content.
    /// </remarks>
    public const string Progress = "progress";

    /// <summary>
    /// Node → worker (phase 47, D3): stop working on this request id, cooperatively.
    /// </summary>
    /// <remarks>
    /// The worker is expected to answer with an <see cref="Error"/> frame coded
    /// <see cref="ToolErrorCodes.Cancelled"/> and then be <b>reusable</b>. That is the whole point:
    /// killing it would be simpler and would punish the next caller with a fresh multi-gigabyte
    /// weight load, and the punishment gets worse with every model the catalogue gains. A worker
    /// that has not answered within <c>Tools:CancelGraceSeconds</c> is by definition not
    /// cooperating and is terminated — phase-41 deviation 6's instinct, for the case it was written
    /// for.
    /// </remarks>
    public const string Cancel = "cancel";

    /// <summary>
    /// Node → worker (phase 48, D3): nobody has asked for anything in <c>idleTimeoutSeconds</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <b>hint</b>, not an instruction, and there is no reply. What to do about it is the worker's
    /// business: the node knows nothing about torch, and a node-side unload would be the node
    /// reaching into a tool's internals — phase-41 D1's line. A diffusion worker frees its pipelines
    /// and stays alive; a worker that holds nothing worth freeing ignores it.
    /// </para>
    /// <para>
    /// The worker must <b>not</b> exit on it. A warm process with no weights reloads a model in
    /// ~40 s; a cold one costs that plus the interpreter and the import of torch, which is not free
    /// — and retiring a worker is already what <c>idleTimeoutSeconds</c> does at the pool level when
    /// it is above <c>minWorkers</c>.
    /// </para>
    /// </remarks>
    public const string Idle = "idle";
}

/// <summary>
/// One frame. Deliberately a single record with optional fields rather than a polymorphic
/// hierarchy: the wire form is a line of JSON that a worker author reads in a terminal and writes
/// in whatever language they have, and a discriminated union over <c>System.Text.Json</c> would buy
/// nothing on this side while costing a custom converter on every other.
/// </summary>
public sealed record ToolFrame
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>Protocol version, on <c>hello</c> and <c>ready</c>.</summary>
    [JsonPropertyName("protocol")]
    public int? Protocol { get; init; }

    /// <summary>The manifest id, on <c>hello</c>. Lets one script back several manifests.</summary>
    [JsonPropertyName("tool")]
    public string? Tool { get; init; }

    /// <summary>Correlates <c>request</c> with <c>chunk</c> / <c>result</c> / <c>error</c>.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("capability")]
    public string? Capability { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>The client's dialect, passed through opaquely in both directions.</summary>
    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }

    /// <summary>Files handed over by path (D5), in on a request and out on a result.</summary>
    [JsonPropertyName("files")]
    public IReadOnlyList<ToolFile>? Files { get; init; }

    /// <summary>The per-request scratch directory. The worker may write into it; the node deletes it.</summary>
    [JsonPropertyName("scratch")]
    public string? Scratch { get; init; }

    /// <summary>
    /// On <c>ready</c>: what this worker actually found it can do. It may only <em>narrow</em> the
    /// manifest's declaration — see <c>ToolWorkerPool</c> for why a worker is never allowed to
    /// widen it.
    /// </summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<NodeCapability>? Capabilities { get; init; }

    /// <summary>
    /// On <c>ready</c>: total VRAM the worker can see, in MiB (phase 48, D1). Optional.
    /// </summary>
    /// <remarks>
    /// <b>It is a cross-check, never an override.</b> <c>Node:Vram:BudgetMiB</c> is what the operator
    /// declared and it is what the admission gate uses; this is what a process on the card actually
    /// measured, and the node logs the two side by side when they disagree materially. A node that
    /// silently adopted this number would be detecting VRAM after all — and would get it wrong on
    /// a shared card, or on a box where somebody else's process is already holding half of it.
    /// </remarks>
    [JsonPropertyName("vramTotalMiB")]
    public int? VramTotalMiB { get; init; }

    [JsonPropertyName("level")]
    public string? Level { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>
    /// On an <c>error</c> frame: which <em>kind</em> of failure this was, from
    /// <see cref="ToolErrorCodes"/>. Optional, and an unknown value is treated as absent.
    /// </summary>
    /// <remarks>
    /// It exists so the edge never has to read the error <em>text</em> to pick a status code —
    /// phase-29 D6's refusal, and the same reasoning that put <c>RetryAfterSeconds</c> on
    /// <c>ToolResult</c> in phase 41. A TTS worker asked for mp3 on a box with no <c>ffmpeg</c>
    /// states "this was the caller's fault, here is what I can do" as a field, and the edge renders
    /// a 400. Without it, that refusal is a 502 and the caller reasonably concludes the server is
    /// broken.
    /// </remarks>
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    /// <summary>On a <c>progress</c> frame: the step just finished, 1-based.</summary>
    [JsonPropertyName("step")]
    public int? Step { get; init; }

    /// <summary>
    /// On a <c>progress</c> frame: how many steps this run will take, as the worker currently knows
    /// it. A recipe may clamp the requested count, so this is the worker's number and not the
    /// caller's — the same reason metering reads what was produced rather than what was asked.
    /// </summary>
    [JsonPropertyName("totalSteps")]
    public int? TotalSteps { get; init; }

    /// <summary>The payload as raw JSON, or null when the frame carried none.</summary>
    public string? PayloadJson() => Payload?.GetRawText();
}

/// <summary>
/// The failure kinds a worker may name on an <c>error</c> frame (phase 42). Deliberately a very
/// short list: each one exists because an edge renders it as something other than the default 502,
/// and a code nobody renders is a code that is wrong by the time somebody reads it.
/// </summary>
public static class ToolErrorCodes
{
    /// <summary>The request was wrong. Rendered as a <c>400</c>.</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>
    /// The worker cannot produce the format that was asked for — no encoder, no such voice.
    /// Rendered as a <c>400</c>, and the worker's own message must name what it <em>can</em> do.
    /// </summary>
    public const string UnsupportedFormat = "unsupported_format";

    /// <summary>
    /// The worker is running but its weights are not there and it was not allowed to fetch them
    /// (<c>Tools:AllowModelDownload</c>). It stays a <c>502</c>: nothing the caller sends will fix
    /// it, and the message names the flag and the pre-fetch command for whoever runs the box.
    /// </summary>
    public const string ModelUnavailable = "model_unavailable";

    /// <summary>
    /// The worker stopped because it was asked to (phase 47, D3). Not a client error and not a
    /// server error: the job ends <c>cancelled</c>, the worker stays warm, and nothing is metered.
    /// </summary>
    public const string Cancelled = "cancelled";

    /// <summary>Whether this code changes the status the edge renders.</summary>
    public static bool IsClientError(string? code) =>
        code is InvalidRequest or UnsupportedFormat;
}

/// <summary>A file handed between the node and a worker by path, never by value.</summary>
public sealed record ToolFile(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("path")] string Path);
