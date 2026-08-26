using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Endpoints;

/// <summary>
/// What a client may call, in one list (phase 65, D5). Until v3.33 the two discovery surfaces
/// reported <c>registry.DistinctModels()</c> — the fleet's inventory — while
/// <c>/api/chat</c> would happily serve a model no node held, so a mapped model was one a client
/// <b>could not discover and could call</b>.
/// </summary>
/// <remarks>
/// <para>
/// The node's entry wins a name held by both, because <c>digest</c> and <c>size</c> are facts about
/// a file on a box. A provider-only entry carries <b>null</b> for both rather than a zero somebody
/// would later read as a measurement — v3.13.1's lesson, and 25 D3's.
/// </para>
/// <para>
/// <b>No vendor appears here</b>, and the projected <c>Fallback:</c> provider is absent entirely:
/// <see cref="IProviderRegistry.ClaimedModels"/> reports named providers only, so a hub carrying the
/// v3.28 section and no <c>Providers:</c> block keeps the exact listing it had.
/// </para>
/// </remarks>
internal static class ModelDiscovery
{
    /// <summary>
    /// The capability a provider-served model is honestly listed with. <c>ProviderDispatcher</c>
    /// serves chat and generate; <c>EmbeddingDispatcher</c> has no provider arm, so listing
    /// <c>embed</c> would be a promise answered with a 404.
    /// </summary>
    public static readonly IReadOnlyList<string> ProviderCapabilities = [CapabilityKinds.Chat];

    public static IReadOnlyList<ModelInfo> Merge(INodeRegistry registry, IProviderRegistry? providers)
    {
        var fleet = registry.DistinctModels();

        if (providers is null)
        {
            return fleet.ToArray();
        }

        var held = fleet.Select(model => model.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var claimed = providers.ClaimedModels
            .Where(model => !held.Contains(model))
            .Select(model => new ModelInfo(model, Digest: null, SizeBytes: null));

        return fleet
            .Concat(claimed)
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>True where no node holds the name and a named provider claims it.</summary>
    public static bool IsProviderOnly(INodeRegistry registry, IProviderRegistry? providers, string model)
        => providers is not null
           // Possession, not serviceability (69 D2): a model must not vanish from /api/tags and
           // reappear as a provider's because its node's Ollama is wedged. `digest` and `size` are
           // facts about a file on a box (65 D5), and they are still true while it is down.
           && registry.FindNodesWithModel(model, includeUnserviceable: true).Count == 0
           && providers.ClaimedModels.Contains(model, StringComparer.OrdinalIgnoreCase);
}
