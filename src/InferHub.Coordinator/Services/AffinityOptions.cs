namespace InferHub.Coordinator.Services;

/// <summary>
/// Optional persistence for conversation affinity (phase 30). Off by default: with
/// <see cref="Persistence"/> = <c>none</c> the map is purely in-memory and a restart resets it,
/// byte-identical to v2.11. With <c>file</c> the map is a *derived cache of routing hints* on disk
/// — a lost or stale entry costs one cold model load, never a wrong answer (rule 4).
/// </summary>
public sealed class AffinityOptions
{
    public const string SectionName = "Affinity";

    public const string PersistenceNone = "none";
    public const string PersistenceFile = "file";

    /// <summary><c>none</c> (default, in-memory) or <c>file</c> (append + periodic snapshot on disk).</summary>
    public string Persistence { get; set; } = PersistenceNone;

    /// <summary>
    /// Where the <c>file</c> store lives. Defaults under the same <c>./data</c> tree the local vector
    /// store uses; the container image overrides it to <c>/data/affinity</c> (a path <c>USER app</c>
    /// can write) exactly like <c>VectorStore:DataDirectory</c>.
    /// </summary>
    public string DataDirectory { get; set; } = "./data/affinity";

    /// <summary>Rewrite the compacted snapshot after this many appended ops. Bounds the ops log.</summary>
    public int SnapshotEveryOps { get; set; } = 256;

    public bool FileEnabled =>
        string.Equals(Persistence?.Trim(), PersistenceFile, StringComparison.OrdinalIgnoreCase);
}
