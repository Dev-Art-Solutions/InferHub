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

    /// <summary>
    /// RAG collections this client may touch (phase 31). <c>null</c>/absent = all collections,
    /// which is what every key had before v2.13 — so a config that never heard of scoping is
    /// unchanged. Entries are exact names or a single trailing-<c>*</c> prefix (<c>tenant-a-*</c>).
    /// Unlike <see cref="ClientLimits"/> this is not a limit but an isolation boundary, so it sits
    /// on the client itself rather than inside a "limits" bag an operator might read as advisory.
    /// </summary>
    public List<string>? Collections { get; set; }
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
