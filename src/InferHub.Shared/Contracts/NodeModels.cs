namespace InferHub.Shared.Contracts;

public sealed record NodeModels(
    string NodeId,
    IReadOnlyList<ModelInfo> Models,
    DateTimeOffset RefreshedAt,
    /// What the node can do with them (phase 40). Travels with the model report because that is
    /// where the model list is refreshed, so a capability that follows from what is installed is
    /// corrected on the existing loop rather than needing a re-registration. Null means "not
    /// declared" — see <see cref="NodeRegistration.Capabilities"/>.
    IReadOnlyList<NodeCapability>? Capabilities = null,
    /// Re-declared on the model report as well as at registration (phase 53, D5), for the reason
    /// capabilities are: it follows from configuration the node can be given without reconnecting,
    /// and a hub that learned it once would keep believing it after it stopped being true.
    bool? SupportsStreamedAttachments = null);
