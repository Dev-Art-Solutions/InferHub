namespace InferHub.Shared.Contracts;

public sealed record NodeModels(
    string NodeId,
    IReadOnlyList<ModelInfo> Models,
    DateTimeOffset RefreshedAt);
