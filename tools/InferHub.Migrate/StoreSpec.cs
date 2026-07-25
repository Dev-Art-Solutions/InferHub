using Microsoft.Extensions.Configuration;

namespace InferHub.Migrate;

/// <summary>
/// Turns one side of the migration — <c>--from</c> or <c>--to</c> — into the
/// <see cref="IConfiguration"/> the coordinator's vector composition root already knows how to read.
/// <para>
/// Two forms. A shorthand (<c>local:./data/vectors</c>, <c>qdrant:http://localhost:6333</c>,
/// <c>postgres:Host=…</c>) covers the common case, and the path to a JSON file holding a real
/// <c>VectorStore</c> section covers everything else — including a deployment's own
/// <c>appsettings.json</c>, which is the honest way to migrate with the exact settings the hub uses.
/// A secret on a command line ends up in shell history; the file form is the one to reach for when
/// a connection string or an API key is involved.
/// </para>
/// </summary>
internal static class StoreSpec
{
    public static IConfiguration Parse(string spec, string? apiKey, string side)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            throw new ArgumentException($"--{side} is required (a provider shorthand or a path to a JSON config file).");
        }

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            // Whatever the source said, this side of the migration is on: a config file with
            // Enabled=false would otherwise compose no store at all and fail with a confusing
            // "no IVectorStore registered" instead of doing the obvious thing.
            ["VectorStore:Enabled"] = "true"
        };

        var builder = new ConfigurationBuilder();

        if (TryShorthand(spec, overrides, side))
        {
            // Shorthand carries no file.
        }
        else if (File.Exists(spec))
        {
            builder.AddJsonFile(Path.GetFullPath(spec), optional: false);
        }
        else
        {
            throw new ArgumentException(
                $"--{side} '{spec}' is neither a known provider shorthand (local:…, postgres:…, qdrant:…) nor an existing file.");
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            overrides["VectorStore:Qdrant:ApiKey"] = apiKey;
        }

        return builder.AddInMemoryCollection(overrides).Build();
    }

    private static bool TryShorthand(string spec, IDictionary<string, string?> overrides, string side)
    {
        // Npgsql also accepts a postgres:// URI, whose scheme separator would otherwise be mistaken
        // for the shorthand's own — so match those before splitting on the first colon.
        if (spec.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            spec.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            overrides["VectorStore:Provider"] = "postgres";
            overrides["VectorStore:Postgres:ConnectionString"] = spec;
            return true;
        }

        var colon = spec.IndexOf(':');
        if (colon <= 0) return false;

        var provider = spec[..colon].ToLowerInvariant();
        var rest = spec[(colon + 1)..].Trim();

        switch (provider)
        {
            case "local":
                Require(rest, side, "a data directory, e.g. local:./data/vectors");
                overrides["VectorStore:Provider"] = "local";
                overrides["VectorStore:DataDirectory"] = rest;
                return true;

            case "postgres":
                Require(rest, side, "a connection string, e.g. postgres:Host=localhost;Database=inferhub;Username=…");
                overrides["VectorStore:Provider"] = "postgres";
                overrides["VectorStore:Postgres:ConnectionString"] = rest;
                return true;

            case "qdrant":
                Require(rest, side, "a URL, e.g. qdrant:http://localhost:6333");
                overrides["VectorStore:Provider"] = "qdrant";
                overrides["VectorStore:Qdrant:Url"] = rest;
                return true;

            default:
                return false;
        }
    }

    private static void Require(string value, string side, string expected)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"--{side} needs {expected}.");
        }
    }
}
