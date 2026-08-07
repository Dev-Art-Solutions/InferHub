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
    [property: JsonPropertyName("modelName")] string ModelName,
    /// <summary>
    /// Which tool's models this is about (phase 48, D4). <b>Null means the node's inference
    /// backend</b>, which is every command that existed before v3.16 — so a v3.15 hub's command
    /// deserialises to exactly what it meant, and a v3.15 node ignores a field it does not know.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FLUX.1-schnell is ~24 GB on the wire and Qwen-Image is larger. A lazy first-use download
    /// inside a request blows <c>requestTimeoutSeconds</c> — v3.14.0 shipped exactly that and every
    /// first <c>sdxl</c> call was a 502 after 900 seconds — and raising that timeout to cover a
    /// 24 GB download means every genuinely wedged job also takes forty minutes to fail.
    /// </para>
    /// <para>
    /// So weights are pulled by an <em>explicit command</em>, on the channel phase 26 already built:
    /// it travels down the node's own outbound connection, progress streams back as
    /// <c>ModelCommandProgress</c>, and the coordinator relays it on the existing
    /// <c>/api/admin/stream</c>. No new transport, and the console gets a progress bar for free —
    /// along with the coalescing and the no-persistent-state property that came with it.
    /// </para>
    /// </remarks>
    [property: JsonPropertyName("tool")] string? Tool = null)
{
    public const string KindPull = "pull";
    public const string KindDelete = "delete";
    public const string KindWarm = "warm";

    public static bool IsKnownKind(string kind) =>
        kind is KindPull or KindDelete or KindWarm;

    /// <summary>Whether this command is about a tool's models rather than the backend's.</summary>
    public bool IsToolCommand => !string.IsNullOrWhiteSpace(Tool);

    /// <summary>
    /// <c>warm</c> has no meaning for a tool model, and saying so is better than inventing one.
    /// </summary>
    /// <remarks>
    /// Warming an Ollama model is an empty generate; warming a diffusion recipe would mean loading
    /// several gigabytes onto the card and holding it there, which is what
    /// <c>Tools:Image:ResidentRecipes</c> and the idle hint already decide between them. A third
    /// opinion about what is resident is a third thing to be wrong.
    /// </remarks>
    public static bool IsKnownToolKind(string kind) => kind is KindPull or KindDelete;
}
