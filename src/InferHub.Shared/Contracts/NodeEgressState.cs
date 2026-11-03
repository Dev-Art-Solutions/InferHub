using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// Phase 128. What a sealed node found when it tried to reach outside its network itself, below InferHub's own code: a TCP
/// connect to each probe address. <see cref="Open"/> empty means the operating system refused every one — the machine is
/// sealed, not only the program. Reported to the hub through <c>ReportEgressState</c>; never sent by a node that is not sealed.
/// </summary>
public sealed record NodeEgressState(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("checkedUtc")] DateTimeOffset CheckedUtc,
    [property: JsonPropertyName("probed")] IReadOnlyList<string> Probed,
    [property: JsonPropertyName("open")] IReadOnlyList<string> Open)
{
    /// <summary>Every probe was refused or timed out. False with nothing probed: nothing was checked.</summary>
    [JsonPropertyName("closed")]
    public bool Closed => Probed.Count > 0 && Open.Count == 0;
}
