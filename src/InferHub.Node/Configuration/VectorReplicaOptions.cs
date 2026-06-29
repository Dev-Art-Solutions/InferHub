namespace InferHub.Node.Configuration;

public sealed class VectorReplicaOptions
{
    public const string SectionName = "Vector";

    /// <summary>
    /// Where assigned replicas are persisted on disk. The node rebuilds each replica's
    /// index from this directory on startup, so a restart does not trigger a full re-push.
    /// </summary>
    public string ReplicaDirectory { get; set; } = "./data/vector-replicas";
}
