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

    [JsonPropertyName("level")]
    public string? Level { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>The payload as raw JSON, or null when the frame carried none.</summary>
    public string? PayloadJson() => Payload?.GetRawText();
}

/// <summary>A file handed between the node and a worker by path, never by value.</summary>
public sealed record ToolFile(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("path")] string Path);
