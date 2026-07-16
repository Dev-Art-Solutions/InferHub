namespace InferHub.Coordinator.Auth;

/// <summary>
/// A named inference client (phase 25): an id, a key, and an optional set of limits. Lives in
/// the <c>Auth:Clients</c> list. The legacy flat <c>Auth:ApiKeys</c> list keeps working — its
/// entries become anonymous clients with no limits, so nobody's config breaks.
/// </summary>
public sealed class ClientConfig
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Environment or user-secrets only. Never <c>appsettings.json</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public ClientLimits? Limits { get; set; }
}

/// <summary>All limits are nullable; <c>null</c> means unlimited.</summary>
public sealed class ClientLimits
{
    public int? MaxConcurrent { get; set; }

    public int? RequestsPerMinute { get; set; }

    public long? TokensPerMinute { get; set; }

    public long? TokensPerDay { get; set; }

    /// <summary>
    /// Models this client may use. Empty/null = all. A request outside the list is a 404
    /// identical to a model that does not exist — a client is not told what exists but is
    /// not for them.
    /// </summary>
    public List<string>? AllowedModels { get; set; }

    public bool HasAny =>
        MaxConcurrent is not null
        || RequestsPerMinute is not null
        || TokensPerMinute is not null
        || TokensPerDay is not null
        || AllowedModels is { Count: > 0 };
}
