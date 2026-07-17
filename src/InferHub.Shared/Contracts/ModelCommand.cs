using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// A hub → node instruction to manage a model on the node's backend (phase 26). Model commands
/// travel down the existing outbound SignalR connection as a job would — the node never grows an
/// inbound surface, so the NAT story is unchanged.
/// </summary>
public sealed record ModelCommand(
    [property: JsonPropertyName("commandId")] Guid CommandId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("modelName")] string ModelName)
{
    public const string KindPull = "pull";
    public const string KindDelete = "delete";
    public const string KindWarm = "warm";

    public static bool IsKnownKind(string kind) =>
        kind is KindPull or KindDelete or KindWarm;
}
