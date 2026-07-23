namespace InferHub.Coordinator.Vector;

public enum VectorStoreProvider
{
    Local,
    Postgres,
    Qdrant
}

public static class VectorStoreProviderExtensions
{
    public const string Local = "local";
    public const string Postgres = "postgres";
    public const string Qdrant = "qdrant";

    public static bool TryParse(string? value, out VectorStoreProvider provider)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case Local:
                provider = VectorStoreProvider.Local;
                return true;
            case Postgres:
                provider = VectorStoreProvider.Postgres;
                return true;
            case Qdrant:
                provider = VectorStoreProvider.Qdrant;
                return true;
            default:
                provider = VectorStoreProvider.Local;
                return false;
        }
    }

    public static bool IsPostgres(string? value) =>
        TryParse(value, out var provider) && provider == VectorStoreProvider.Postgres;

    public static bool IsQdrant(string? value) =>
        TryParse(value, out var provider) && provider == VectorStoreProvider.Qdrant;

    /// <summary>
    /// True for a provider that is its own external, durable source of truth (Postgres, Qdrant).
    /// These share one architecture: node replication and self-healing are off, the rebuild
    /// endpoint is not applicable, and status placement is zeroed — the store owns durability, so
    /// pushing a second derived copy onto the fleet would be a second write path and a second truth.
    /// The distinction that matters at every call site is external-vs-local, not which external one.
    /// </summary>
    public static bool IsExternal(string? value) =>
        TryParse(value, out var provider) && provider != VectorStoreProvider.Local;

    public static string ToWireString(this VectorStoreProvider provider) => provider switch
    {
        VectorStoreProvider.Postgres => Postgres,
        VectorStoreProvider.Qdrant => Qdrant,
        _ => Local
    };

    /// <summary>The canonical wire string for a configured provider value; unknown falls back to local.</summary>
    public static string NormalizeWire(string? value) =>
        TryParse(value, out var provider) ? provider.ToWireString() : Local;
}
