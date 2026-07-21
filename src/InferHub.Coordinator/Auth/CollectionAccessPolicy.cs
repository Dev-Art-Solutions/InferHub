namespace InferHub.Coordinator.Auth;

/// <summary>
/// The one place a client's collection scope is decided (phase 31). Like <c>FleetSaturation</c> is
/// the one place saturation is defined, this is the one place "may this key touch this collection"
/// is answered — every path that names a collection asks here, so a new path cannot quietly grow a
/// second, subtly different rule.
///
/// This is an <em>authorization filter over the one vector store</em>, not a per-tenant store: rule
/// 4 survives untouched. There is still one collection namespace and one source of truth; a scope
/// only decides which names a key is allowed to say.
/// </summary>
public static class CollectionAccessPolicy
{
    /// <summary>
    /// A client with no <c>Collections</c> list may touch every collection — that is the pre-v2.13
    /// behaviour and the default, so a config that never heard of scoping is unchanged. A list is
    /// exhaustive: each entry is either an exact name or a <c>prefix*</c> glob.
    /// </summary>
    public static bool CanAccess(ResolvedClient client, string? collection)
    {
        if (client.Collections is not { Count: > 0 } scopes)
        {
            return true;
        }

        if (string.IsNullOrEmpty(collection))
        {
            return false;
        }

        foreach (var scope in scopes)
        {
            if (Matches(scope, collection))
            {
                return true;
            }
        }

        return false;
    }

    public static bool CanAccess(HttpContext context, string? collection)
        => CanAccess(BearerApiKeyMiddleware.ClientOf(context), collection);

    /// <summary>
    /// Only a trailing <c>*</c> is a wildcard. A full glob dialect (or a regex) in a config file is
    /// a footgun aimed at an isolation boundary — <c>tenant-a-*</c> is what provisioning actually
    /// needs, and everything else is an exact name.
    /// </summary>
    private static bool Matches(string scope, string collection)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return false;
        }

        scope = scope.Trim();

        if (scope.EndsWith('*'))
        {
            var prefix = scope[..^1];
            return prefix.Length == 0
                || collection.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(scope, collection, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The denial. It is deliberately the <em>same sentence</em> a genuinely missing collection
    /// produces (<c>LocalVectorStore</c>, <c>PostgresVectorStore</c>, <c>IngestionPipeline</c> all
    /// say this), at the same status — phase-25 D4's principle: a tenant is not told another
    /// tenant's collections exist.
    ///
    /// And because the check runs <em>before</em> the store is consulted, a name outside a client's
    /// scope reads identically whether or not it exists, so nothing leaks either way.
    /// </summary>
    public static string NotFoundMessage(string? collection)
        => $"collection '{collection}' does not exist";
}

/// <summary>
/// Enforces <see cref="CollectionAccessPolicy"/> on every route in a group that carries a
/// <c>{collection}</c> route parameter. A filter rather than a line in each handler: the ingestion
/// group alone has five routes, and the one that gets forgotten is the isolation hole.
/// </summary>
public static class CollectionScopeFilterExtensions
{
    public const string RouteValue = "collection";

    public static TBuilder RequireCollectionScope<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var collection = http.Request.RouteValues.TryGetValue(RouteValue, out var raw)
                ? raw as string
                : null;

            if (!CollectionAccessPolicy.CanAccess(http, collection))
            {
                return Results.Json(
                    new { error = CollectionAccessPolicy.NotFoundMessage(collection) },
                    statusCode: StatusCodes.Status404NotFound);
            }

            return await next(context);
        });

        return builder;
    }
}

/// <summary>
/// Thrown by the inline-retrieval path (<c>X-InferHub-Retrieve</c>) when the named collection is
/// outside the caller's scope. It is <em>not</em> routed through <c>Retrieval:OnMissing</c>: a
/// passthrough would answer without the context the caller asked for and never say so, which on a
/// tenancy boundary is worse than an error. Both client dialects catch it and render a 404.
/// </summary>
public sealed class CollectionNotVisibleException(string collection)
    : InvalidOperationException(CollectionAccessPolicy.NotFoundMessage(collection))
{
    public string Collection { get; } = collection;
}
