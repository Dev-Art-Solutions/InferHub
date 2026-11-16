using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// Phase 150: what a node says about a followed job while it runs it — a count and a stage, never the text.
/// Sent when the job arrives, when the engine's first piece arrives, and every few seconds in between and
/// after, so a hub that hears nothing for a while knows it is the node that went quiet and not the model.
/// </summary>
public sealed record InferenceJobProgress(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("pieces")] int Pieces,
    [property: JsonPropertyName("elapsedMs")] long ElapsedMs,
    [property: JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null)
{
    /// <summary>The node has the job and its engine has not produced anything yet: loading the model, reading the prompt.</summary>
    public const string Preparing = "preparing";

    /// <summary>The engine is producing the answer.</summary>
    public const string Generating = "generating";
}
