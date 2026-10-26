namespace InferHub.Shared.Fit;

/// <summary>What the node's machine has to put a model in (phase 118) — read on the node, sent to whoever asks.</summary>
/// <param name="RamMiB">Physical memory (or the container's limit).</param>
/// <param name="RamAvailableMiB">What is free right now — shown beside the estimate, never used to refuse one: a
/// model is loaded later, when other programs may have let go.</param>
public sealed record MachineFacts(
    IReadOnlyList<GpuFacts> Gpus,
    long RamMiB,
    long? RamAvailableMiB,
    int Cores,
    IReadOnlyList<EngineFacts> Engines,
    IReadOnlyList<string> Notes);

/// <param name="FreeMiB">Free now — other programs (another engine, a game) may hold the rest.</param>
public sealed record GpuFacts(int Index, string Name, long TotalMiB, long? FreeMiB);

/// <summary>One kind of engine, as this node has it or not.</summary>
/// <param name="Kind"><see cref="FitEngines"/>' names.</param>
/// <param name="Name">The engine's name under <c>Backend:Engines</c>, null for a single-backend node or one it lacks.</param>
/// <param name="Installs">Whether a model can be installed for it here (not only served): the store, the router's
/// directory, Strata's setup, an Ollama that pulls.</param>
/// <param name="Absent">Why it cannot, in a sentence; null when it can.</param>
/// <param name="DiskFreeMiB">Free space where its models land.</param>
/// <param name="VramLimitMiB">The owner's limit for this runtime (102); null is none.</param>
/// <param name="KvCopies">How many contexts the engine keeps per loaded model: Ollama gives each of its <c>OLLAMA_NUM_PARALLEL</c>
/// slots the whole context; llama.cpp shares one (<c>kv_unified</c>).</param>
public sealed record EngineFacts(
    string Kind,
    string? Name,
    bool Installs,
    string? Absent,
    string? Directory,
    long? DiskFreeMiB,
    long? VramLimitMiB,
    long? RamLimitMiB,
    int KvCopies = 1);

public static class FitEngines
{
    public const string Ollama = "ollama";
    public const string LlamaCpp = "llamacpp";
    public const string Colibri = "colibri";
    public const string Strata = "strata";

    public static readonly IReadOnlyList<string> All = [Ollama, LlamaCpp, Colibri, Strata];
}
