using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InferHub.Shared.Fit;

/// <summary>
/// Phase 134: what a node's owner set aside for groups (<c>Node:Shards</c>) — memory its own models never count on (D4),
/// and the RPC protocol its worker speaks, which is what a group's members must share (D3).
/// </summary>
/// <param name="Protocol">What the worker announced (<c>Starting RPC server vX.Y.Z</c>); null until it has.</param>
public sealed record ShardOffer(
    [property: JsonPropertyName("ramMiB")] long RamMiB,
    [property: JsonPropertyName("vramMiB")] long VramMiB,
    [property: JsonPropertyName("protocol")] string? Protocol = null)
{
    [JsonIgnore]
    public long TotalMiB => RamMiB + VramMiB;
}

/// <summary>
/// A node's shard worker as it reports it to the hub (phase 134, D5) — the 44 D6 mailbox: the node says, the hub shows.
/// </summary>
/// <param name="State"><see cref="Off"/> | <see cref="Starting"/> | <see cref="Running"/> | <see cref="Failed"/>.</param>
/// <param name="Endpoint">Where the worker listens on its own machine — always loopback (D1).</param>
/// <param name="Devices">The devices it said it offers, as it printed them (<c>CPU: … (N MiB, M MiB free)</c>).</param>
public sealed record NodeShardState(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("ramMiB")] long RamMiB,
    [property: JsonPropertyName("vramMiB")] long VramMiB,
    [property: JsonPropertyName("endpoint")] string? Endpoint,
    [property: JsonPropertyName("protocol")] string? Protocol,
    [property: JsonPropertyName("devices")] IReadOnlyList<string> Devices,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc)
{
    /// <summary><c>Node:Shards:Enabled</c> is false: nothing offered, nothing running.</summary>
    public const string Off = "off";

    /// <summary>Launched, and it has not printed its banner yet.</summary>
    public const string Starting = "starting";

    /// <summary>It announced its protocol and its devices: a head on this machine can connect.</summary>
    public const string Running = "running";

    /// <summary>It cannot run — no executable, an offer of nothing — or it exited; <see cref="LastError"/> says which.</summary>
    public const string Failed = "failed";
}

/// <summary>What llama.cpp's <c>rpc-server</c> prints when it starts, read line by line (phase 134, D2).</summary>
public static partial class ShardBanner
{
    /// <summary>The protocol in <c>Starting RPC server v8.0.1</c>, or null when the line is not that.</summary>
    public static string? Protocol(string? line) =>
        line is not null && ProtocolLine().Match(line) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>A device line under <c>Devices:</c> — <c>CPU: AMD Ryzen … (261985 MiB, 110652 MiB free)</c> — trimmed; null otherwise.</summary>
    public static string? Device(string? line) =>
        line is not null && DeviceLine().IsMatch(line) ? line.Trim() : null;

    /// <summary>The total MiB a device line names, or null.</summary>
    public static long? DeviceMiB(string device) =>
        DeviceMemory().Match(device) is { Success: true } match
        && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var mib)
            ? mib
            : null;

    [GeneratedRegex(@"Starting RPC server v(\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ProtocolLine();

    [GeneratedRegex(@"^\s+\S.*:\s.*\((\d+) MiB, \d+ MiB free\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceLine();

    [GeneratedRegex(@"\((\d+) MiB, \d+ MiB free\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceMemory();
}
