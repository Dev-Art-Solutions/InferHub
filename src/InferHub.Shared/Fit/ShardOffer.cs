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
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc,
    [property: JsonPropertyName("tunnel")] ShardTunnelState? Tunnel = null,
    [property: JsonPropertyName("peers")] IReadOnlyList<ShardPeerState>? Peers = null,
    [property: JsonPropertyName("groups")] IReadOnlyList<ShardGroupState>? Groups = null)
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

/// <summary>
/// Phase 135: this node's tunnel — the TLS port other nodes reach its worker through (D2). <see cref="Advertise"/> is what it
/// asked to be reached at (<c>Node:Shards:TunnelAdvertise</c>); null lets the hub use the address its connection came from.
/// </summary>
/// <param name="Off">Why there is no tunnel (no certificate, no port), or null when it listens.</param>
public sealed record ShardTunnelState(
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("advertise")] string? Advertise,
    [property: JsonPropertyName("thumbprint")] string? Thumbprint,
    [property: JsonPropertyName("off")] string? Off);

/// <summary>Phase 135 D1: one node this node may connect to and accept, as the hub decided — reached at <see cref="Endpoint"/>,
/// proved by the SHA-256 of its certificate.</summary>
public sealed record ShardPeer(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("thumbprint")] string Thumbprint);

/// <summary>The hub's list for one node (D1). Replaces the previous one whole; empty closes every door.</summary>
public sealed record ShardPeerList(
    [property: JsonPropertyName("peers")] IReadOnlyList<ShardPeer> Peers);

/// <summary>A peer as this node sees it: its local forward (D3) and the round trip through the tunnel (D4).</summary>
public sealed record ShardPeerState(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("local")] string? Local,
    [property: JsonPropertyName("rttMs")] double? RttMs,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("atUtc")] DateTimeOffset? AtUtc);

/// <summary>
/// The tunnel's first byte after the TLS handshake (D2): <see cref="Rpc"/> pipes to the worker, <see cref="Ping"/> echoes
/// 8-byte frames. And the thumbprint both ends compare — 127's record, the SHA-256 of the certificate's DER, lower-case hex.
/// </summary>
public static class ShardTunnelWire
{
    public const byte Rpc = (byte)'R';
    public const byte Ping = (byte)'P';

    public static string Thumbprint(ReadOnlySpan<byte> der) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(der));
}

/// <summary>
/// Phase 136 D3: one member of a group as the head runs it — its worker, in this order after the head's own, holding a
/// share of the layers in proportion to <see cref="Weight"/> (its offer, MiB).
/// </summary>
public sealed record ShardGroupWorker(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("weight")] long Weight);

/// <summary>Phase 136 D1: a group the head should run — the model file (a name in its <c>Node:Shards:ModelsDir</c>), the
/// context, its own share and the members'. The model answers under <see cref="Name"/>.</summary>
public sealed record ShardGroupSpec(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("context")] int Context,
    [property: JsonPropertyName("headWeight")] long HeadWeight,
    [property: JsonPropertyName("members")] IReadOnlyList<ShardGroupWorker> Members);

/// <summary>The hub's list for one head (D1). Replaces the previous one whole; a group not in it is stopped.</summary>
public sealed record ShardGroupList(
    [property: JsonPropertyName("groups")] IReadOnlyList<ShardGroupSpec> Groups);

/// <summary>A group as its head runs it: <see cref="State"/> is <c>waiting</c> (a member's forward not open yet),
/// <c>starting</c>, <c>running</c> (its llama-server answers /health) or <c>failed</c>, with the sentence.</summary>
public sealed record ShardGroupState(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("members")] IReadOnlyList<string> Members,
    [property: JsonPropertyName("endpoint")] string? Endpoint,
    [property: JsonPropertyName("error")] string? Error)
{
    public const string Waiting = "waiting";
    public const string Starting = "starting";
    public const string Running = "running";
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
