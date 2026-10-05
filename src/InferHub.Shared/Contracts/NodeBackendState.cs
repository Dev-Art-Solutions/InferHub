using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// What a node running several engines says about each of them (phase 95): which ones it may run,
/// which are running, and what each one serves.
/// </summary>
/// <remarks>
/// <para>
/// It is the phase-44 D6 mailbox for the fifth time: the node reports on its own loop and whenever
/// an engine changes state, the hub records it, and <b>the hub never asks</b>. A node with a single
/// <c>Backend:Type</c> sends none — and a hub that has none for a node shows what it always showed.
/// </para>
/// <para>
/// <b>The list is the ceiling, not the running set.</b> Every engine the operator configured is in
/// it, stopped ones included, because "which engines could I start here" is the question the start
/// button asks, and a node that only reported running engines could not answer it.
/// </para>
/// </remarks>
public sealed record NodeBackendState(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("engines")] IReadOnlyList<NodeEngineInfo> Engines,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc);

/// <summary>One configured engine on a node and what became of it.</summary>
public sealed record NodeEngineInfo(
    /// <summary>The operator's name for it — the key a coordinator starts and stops.</summary>
    [property: JsonPropertyName("name")] string Name,
    /// <summary><c>ollama</c>, <c>llamacpp</c>, <c>colibri</c> or <c>openai</c>.</summary>
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    /// <summary>
    /// <see cref="Running"/> | <see cref="Starting"/> | <see cref="Stopped"/> |
    /// <see cref="Unreachable"/> | <see cref="Failed"/>. Five because each has a different fix.
    /// </summary>
    [property: JsonPropertyName("state")] string State,
    /// <summary>Whether the node starts it at boot. A coordinator can override it either way.</summary>
    [property: JsonPropertyName("autostart")] bool Autostart,
    /// <summary>Whether the node launches the process itself, rather than driving one running beside it.</summary>
    [property: JsonPropertyName("launched")] bool Launched,
    [property: JsonPropertyName("kinds")] IReadOnlyList<string> Kinds,
    /// <summary>What it answered with on its last listing. Empty for a stopped engine.</summary>
    [property: JsonPropertyName("models")] IReadOnlyList<string> Models,
    [property: JsonPropertyName("inFlight")] int InFlight,
    [property: JsonPropertyName("lastError")] string? LastError = null)
{
    /// <summary>Running and answering.</summary>
    public const string Running = "running";

    /// <summary>Asked to run, and has not answered yet — a launched engine loading its weights.</summary>
    public const string Starting = "starting";

    /// <summary>Not running: its autostart was off, or a coordinator stopped it. Startable in place.</summary>
    public const string Stopped = "stopped";

    /// <summary>Asked to run and stopped answering. Its models are withdrawn until it does.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>Could not be launched at all — a missing model file or binary. The fix is on the box.</summary>
    public const string Failed = "failed";
}
