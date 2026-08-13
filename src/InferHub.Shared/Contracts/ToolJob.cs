using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// Work that has no Ollama shape to be (phase 40, D3; dispatched from phase 41). A transcription
/// is not an <see cref="InferenceJob"/> with a foreign body — it is a different kind of job, so it
/// travels as its own contract and design rule 6 keeps meaning exactly what it says.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Payload"/> is the <em>client's</em> dialect, passed through opaquely: the mesh does
/// not translate it, because unlike chat and embeddings there is no second dialect to translate
/// between. The node hands it to the worker and hands the worker's answer back.
/// </para>
/// <para>
/// What is <em>not</em> duplicated is the machinery — a tool job goes through the same job
/// registry, the same stream plumbing and the same pre-stream failover as an inference job.
/// </para>
/// </remarks>
public sealed record ToolJob(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("capability")] string Capability,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("attachments")] IReadOnlyList<ToolAttachment>? Attachments = null,
    /// <summary>
    /// The bytes are not here — pull them (phase 53, D1). True means the node should invoke
    /// <c>StreamAttachments(jobId)</c> and write what comes back into its scratch directory before
    /// building the worker's request frame. Appended last and defaulting to false, so a v3.20 hub
    /// and a v3.21 node agree about every job that existed before this phase.
    /// </summary>
    [property: JsonPropertyName("hasStreamedAttachments")] bool HasStreamedAttachments = false);
