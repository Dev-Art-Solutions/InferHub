using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

public sealed record InferenceJob(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("requestJson")] string RequestJson,
    /// <summary>
    /// Phase 150: the id of the job somebody follows (the one a caller holds — this dispatch's own id changes on a
    /// failover), so the node reports on it while it runs
    /// (<see cref="InferenceJobProgress"/>). Null on every job nobody follows and from a hub before 3.103 —
    /// a node then sends nothing it did not send before.
    /// </summary>
    [property: JsonPropertyName("watch"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? Watch = null);
