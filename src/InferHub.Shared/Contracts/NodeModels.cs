namespace InferHub.Shared.Contracts;

public sealed record NodeModels(
    string NodeId,
    IReadOnlyList<ModelInfo> Models,
    DateTimeOffset RefreshedAt,
    /// What the node can do with them (phase 40). Travels with the model report because that is
    /// where the model list is refreshed, so a capability that follows from what is installed is
    /// corrected on the existing loop rather than needing a re-registration. Null means "not
    /// declared" — see <see cref="NodeRegistration.Capabilities"/>.
    IReadOnlyList<NodeCapability>? Capabilities = null);
