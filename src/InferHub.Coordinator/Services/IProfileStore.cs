using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Durability for node profiles (phase 43, D3). Three implementations behind one seam, selected by
/// <c>Fleet:Profiles:Persistence</c>, exactly as <c>IAffinityStore</c> and <c>IUsageLedger</c> are.
/// </summary>
/// <remarks>
/// Load is synchronous and happens once, at registry construction: a coordinator that came up
/// without its profiles and acquired them a second later would spend that second re-asserting an
/// empty fleet configuration onto every node that registered in the meantime.
/// </remarks>
public interface IProfileStore
{
    IReadOnlyCollection<NodeProfile> Load();

    void Save(NodeProfile profile);

    void Delete(string name);
}

/// <summary>The default. Profiles live as long as the coordinator does, like everything else on it.</summary>
public sealed class NoProfileStore : IProfileStore
{
    public IReadOnlyCollection<NodeProfile> Load() => Array.Empty<NodeProfile>();

    public void Save(NodeProfile profile)
    {
    }

    public void Delete(string name)
    {
    }
}
