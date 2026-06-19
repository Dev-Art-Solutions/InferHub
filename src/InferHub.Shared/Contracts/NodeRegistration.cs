namespace InferHub.Shared.Contracts;

public sealed record NodeRegistration(
    string NodeId,
    string Name,
    string OllamaEndpoint,
    string Version,
    IReadOnlyDictionary<string, string>? Labels = null,
    int? MaxConcurrency = null);
