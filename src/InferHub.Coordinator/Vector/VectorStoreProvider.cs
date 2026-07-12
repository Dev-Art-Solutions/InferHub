namespace InferHub.Coordinator.Vector;

public enum VectorStoreProvider
{
    Local,
    Postgres
}

public static class VectorStoreProviderExtensions
{
    public const string Local = "local";
    public const string Postgres = "postgres";

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
            default:
                provider = VectorStoreProvider.Local;
                return false;
        }
    }

    public static bool IsPostgres(string? value) =>
        TryParse(value, out var provider) && provider == VectorStoreProvider.Postgres;

    public static string ToWireString(this VectorStoreProvider provider) => provider switch
    {
        VectorStoreProvider.Postgres => Postgres,
        _ => Local
    };
}
