using InferHub.Shared.Contracts;

namespace InferHub.Node.Tools;

/// <summary>
/// One tool, as declared on disk (phase 41, D3): what it can do, how to start it, and the deadlines
/// it runs under.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Command"/> is an argv array, never a command line, and there is no shell.</b>
/// A command assembled by concatenation is one quoting bug away from being an injection point, and
/// the values around it — model names — come from requests. It is spawned through
/// <c>ProcessStartInfo.ArgumentList</c> only.
/// </para>
/// <para>
/// <b>Nothing from a request ever reaches the argv.</b> Model, options, and file paths all travel
/// in the protocol, over stdin, after the process is already running. The argv is fixed at load
/// time and never rebuilt.
/// </para>
/// </remarks>
public sealed record ToolManifest
{
    public required string Id { get; init; }

    /// <summary>
    /// What this tool claims. It is a ceiling, not a promise: a worker may report a narrower set at
    /// handshake (a Whisper worker that finds only one of two model files), and never a wider one.
    /// </summary>
    public required IReadOnlyList<NodeCapability> Capabilities { get; init; }

    /// <summary>The executable and its arguments, already split. Element 0 is the program.</summary>
    public required IReadOnlyList<string> Command { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Extra environment for the child, on top of a short pass-through list. It is <em>not</em> the
    /// node's environment plus these — see <c>ToolWorkerProcess</c> for why inheriting wholesale is
    /// a credential leak wearing a convenience's clothes.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Workers started eagerly when the pool opens. Default 0 — nothing runs until asked.</summary>
    public int MinWorkers { get; init; }

    /// <summary>
    /// Default <b>1</b>, deliberately. A second Whisper process on the same card is two copies of
    /// the weights and a memory error at the worst possible moment. Operators who want parallelism
    /// raise it knowingly.
    /// </summary>
    public int MaxWorkers { get; init; } = 1;

    /// <summary>Deadline for <c>hello</c> → <c>ready</c>. Generous: loading weights is slow, not broken.</summary>
    public int StartTimeoutSeconds { get; init; } = 120;

    public int RequestTimeoutSeconds { get; init; } = 600;

    /// <summary>
    /// How long a worker may sit idle before it is retired, so a rarely-used tool does not hold
    /// VRAM forever.
    /// </summary>
    public int IdleTimeoutSeconds { get; init; } = 900;

    public TimeSpan StartTimeout => TimeSpan.FromSeconds(StartTimeoutSeconds);

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(RequestTimeoutSeconds);

    public TimeSpan IdleTimeout => TimeSpan.FromSeconds(IdleTimeoutSeconds);

    /// <summary>Whether this manifest claims the given (capability, model) pair.</summary>
    public bool Provides(string capability, string model) =>
        Capabilities.Any(c =>
            string.Equals(c.Kind, capability, StringComparison.OrdinalIgnoreCase)
            && c.Models.Any(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase)));
}
