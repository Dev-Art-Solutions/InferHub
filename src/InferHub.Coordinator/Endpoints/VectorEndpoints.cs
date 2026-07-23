using System.Diagnostics;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Vector;
using Microsoft.AspNetCore.Http;

namespace InferHub.Coordinator.Endpoints;

public static class VectorEndpoints
{
    public static IEndpointRouteBuilder MapVectorEndpoints(this IEndpointRouteBuilder app, bool supportsReplication = true)
    {
        MapDataPlane(app);
        MapAdminPlane(app, supportsReplication);
        return app;
    }

    private static void MapDataPlane(IEndpointRouteBuilder app)
    {
        // The raw data plane names a collection on every route, so it carries the same client
        // scope the document and search surfaces do — an isolation boundary with a hole in it
        // where the low-level API lives would not be one.
        var group = app.MapGroup("/api/vector/{collection}").RequireCollectionScope();

        group.MapPost("/upsert", async (
            string collection,
            VectorUpsert upsert,
            IVectorStore store,
            IEmbeddingDispatcher embeddings,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var prepared = await ResolveVectorAsync(upsert, embeddings, cancellationToken);
                var record = await store.UpsertAsync(collection, prepared, cancellationToken);
                return Results.Ok(record);
            }
            catch (KeyNotFoundException ex)
            {
                return Error(StatusCodes.Status404NotFound, ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Error(StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (NoEmbeddingNodeException ex)
            {
                return Error(StatusCodes.Status404NotFound, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Error(StatusCodes.Status400BadRequest, ex.Message);
            }
        });

        group.MapPost("/query", async (
            string collection,
            VectorQuery query,
            IVectorStore store,
            IEmbeddingDispatcher embeddings,
            IVectorQueryRouter queryRouter,
            Metrics metrics,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var prepared = await ResolveVectorAsync(query, embeddings, cancellationToken);
                var matches = await RouteQueryAsync(collection, prepared, store, queryRouter, metrics, cancellationToken);
                return Results.Ok(new { matches });
            }
            catch (KeyNotFoundException ex)
            {
                return Error(StatusCodes.Status404NotFound, ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Error(StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (NoEmbeddingNodeException ex)
            {
                return Error(StatusCodes.Status404NotFound, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Error(StatusCodes.Status400BadRequest, ex.Message);
            }
        });

        // Convenience RAG read: text → embed → search → matched payloads. Same body shape as
        // /query (vector OR text + optional model + k), but the response is just the matches.
        group.MapPost("/retrieve", async (
            string collection,
            VectorQuery query,
            IVectorStore store,
            IEmbeddingDispatcher embeddings,
            IVectorQueryRouter queryRouter,
            Metrics metrics,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var prepared = await ResolveVectorAsync(query, embeddings, cancellationToken);
                var matches = await RouteQueryAsync(collection, prepared, store, queryRouter, metrics, cancellationToken);
                return Results.Ok(new { matches });
            }
            catch (KeyNotFoundException ex)
            {
                return Error(StatusCodes.Status404NotFound, ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Error(StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (NoEmbeddingNodeException ex)
            {
                return Error(StatusCodes.Status404NotFound, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Error(StatusCodes.Status400BadRequest, ex.Message);
            }
        });

        group.MapGet("/{id}", async (
            string collection,
            string id,
            IVectorStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var record = await store.GetAsync(collection, id, cancellationToken);
                return record is null
                    ? Error(StatusCodes.Status404NotFound, $"record '{id}' not found")
                    : Results.Ok(record);
            }
            catch (KeyNotFoundException ex)
            {
                return Error(StatusCodes.Status404NotFound, ex.Message);
            }
        });

        group.MapDelete("/{id}", async (
            string collection,
            string id,
            IVectorStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var removed = await store.DeleteAsync(collection, id, cancellationToken);
                return removed
                    ? Results.Ok(new { id, deleted = true })
                    : Error(StatusCodes.Status404NotFound, $"record '{id}' not found");
            }
            catch (KeyNotFoundException ex)
            {
                return Error(StatusCodes.Status404NotFound, ex.Message);
            }
        });
    }

    private static void MapAdminPlane(IEndpointRouteBuilder app, bool supportsReplication)
    {
        var group = app.MapGroup("/api/admin/vector/collections");

        group.MapGet("/", async (
            IVectorStore store,
            ReplicaRegistry replicas,
            INodeRegistry nodes,
            IClientRegistry clients,
            Microsoft.Extensions.Options.IOptions<VectorStoreOptions> options,
            CancellationToken cancellationToken) =>
        {
            var collections = await store.ListCollectionsAsync(cancellationToken);
            var isExternal = VectorStoreProviderExtensions.IsExternal(options.Value.Provider);
            var placement = collections
                .Select(c => isExternal
                    ? ZeroPlacement(c.Name)
                    : BuildPlacement(c.Name, replicas, nodes, options.Value.ReplicationFactor))
                .ToArray();

            // Who can reach each collection (phase 31). Admin sees everything — it always has —
            // and this is the view that makes a tenancy misconfiguration visible before a tenant
            // finds it. A collection no scoped client matches is listed with an empty array, which
            // is a real answer: only unscoped keys and admins can see it.
            var scopes = collections
                .Select(c => new
                {
                    collection = c.Name,
                    clients = clients.NamedClients
                        .Where(client => client.Collections is { Count: > 0 }
                            && CollectionAccessPolicy.CanAccess(new ResolvedClient(client.Id, client.Limits, client.Collections), c.Name))
                        .Select(client => client.Id)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray()
                })
                .ToArray();

            return Results.Ok(new { collections, placement, scopes });
        });

        // Per-collection detail: everything the console needs to draw one row and its
        // replica health at a glance — no need to cross-reference /api/status.
        group.MapGet("/{collection}", async (
            string collection,
            IVectorStore store,
            ReplicaRegistry replicas,
            INodeRegistry nodes,
            Metrics metrics,
            Microsoft.Extensions.Options.IOptions<VectorStoreOptions> options,
            CancellationToken cancellationToken) =>
        {
            var info = await store.GetCollectionAsync(collection, cancellationToken);
            if (info is null)
            {
                return Error(StatusCodes.Status404NotFound, $"collection '{collection}' not found");
            }

            var isExternal = VectorStoreProviderExtensions.IsExternal(options.Value.Provider);
            CollectionPlacement placement;
            bool underReplicated;
            if (isExternal)
            {
                placement = ZeroPlacement(info.Name);
                underReplicated = false;
            }
            else
            {
                placement = BuildPlacement(info.Name, replicas, nodes, options.Value.ReplicationFactor);
                var connected = nodes.Snapshot(DateTimeOffset.UtcNow);
                var eligibleCount = connected.Count(n => !n.Cordoned);
                var desired = Math.Min(options.Value.ReplicationFactor, eligibleCount);
                underReplicated = placement.LiveReplicas < desired;
            }
            var stats = metrics.GetVectorCollectionSnapshot(info.Name);

            return Results.Ok(new
            {
                collection = info,
                placement,
                underReplicated,
                stats
            });
        });

        group.MapPost("/", async (
            CreateCollectionRequest request,
            HttpContext context,
            IVectorStore store,
            IAuditLog audit,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var info = await store.CreateCollectionAsync(request.Name, request.Dimension, request.Distance, cancellationToken);
                audit.Record(request.Name, "vector.create", ActorOf(context), DateTimeOffset.UtcNow);
                return Results.Created($"/api/admin/vector/collections/{info.Name}", info);
            }
            catch (InvalidOperationException ex)
            {
                return Error(StatusCodes.Status409Conflict, ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Error(StatusCodes.Status400BadRequest, ex.Message);
            }
        });

        group.MapDelete("/{collection}", async (
            string collection,
            HttpContext context,
            IVectorStore store,
            IAuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var dropped = await store.DropCollectionAsync(collection, cancellationToken);
            if (!dropped)
            {
                return Error(StatusCodes.Status404NotFound, $"collection '{collection}' not found");
            }

            audit.Record(collection, "vector.drop", ActorOf(context), DateTimeOffset.UtcNow);
            return Results.Ok(new { collection, dropped = true });
        });

        if (supportsReplication)
        {
            // Force a heal-to-target re-push from the raw store. Useful when an operator
            // wants to confirm replica integrity or proactively re-seed after a fleet event.
            group.MapPost("/{collection}/rebuild", async (
                string collection,
                HttpContext context,
                HealingService healing,
                IAuditLog audit,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    await healing.RebuildAsync(collection, cancellationToken);
                    audit.Record(collection, "vector.rebuild", ActorOf(context), DateTimeOffset.UtcNow);
                    return Results.Ok(new { collection, rebuilt = true });
                }
                catch (KeyNotFoundException ex)
                {
                    return Error(StatusCodes.Status404NotFound, ex.Message);
                }
            });
        }
        else
        {
            // Under postgres there is no HealingService and nothing to re-push. A route that
            // vanished would 404 and look like a bug; a 409 with a reason is honest.
            group.MapPost("/{collection}/rebuild", (string collection) =>
                Error(StatusCodes.Status409Conflict,
                    "rebuild is not applicable when VectorStore:Provider=postgres — Postgres owns durability"));
        }
    }

    private static async Task<IReadOnlyList<VectorMatch>> RouteQueryAsync(
        string collection,
        VectorQuery prepared,
        IVectorStore store,
        IVectorQueryRouter queryRouter,
        Metrics metrics,
        CancellationToken cancellationToken)
    {
        // Prefer a node replica when available; the hub-local index is the floor and
        // takes over silently when no replica answers (zero nodes, transient failure, etc.).
        var started = Stopwatch.GetTimestamp();
        try
        {
            var matches = await queryRouter.TryQueryOnNodeAsync(collection, prepared, cancellationToken);
            if (matches is not null) return matches;
            return await store.QueryAsync(collection, prepared, cancellationToken);
        }
        finally
        {
            metrics.RecordVectorQuery(collection, Stopwatch.GetElapsedTime(started));
        }
    }

    private static async Task<VectorUpsert> ResolveVectorAsync(
        VectorUpsert upsert,
        IEmbeddingDispatcher embeddings,
        CancellationToken cancellationToken)
    {
        if (upsert.Vector is { Length: > 0 })
        {
            return upsert;
        }

        if (string.IsNullOrWhiteSpace(upsert.Text))
        {
            throw new ArgumentException("either 'vector' or 'text' must be provided");
        }

        var vector = await embeddings.EmbedSingleAsync(upsert.Text, upsert.Model, cancellationToken);
        return upsert with { Vector = vector };
    }

    private static async Task<VectorQuery> ResolveVectorAsync(
        VectorQuery query,
        IEmbeddingDispatcher embeddings,
        CancellationToken cancellationToken)
    {
        if (query.Vector is { Length: > 0 })
        {
            return query;
        }

        if (string.IsNullOrWhiteSpace(query.Text))
        {
            throw new ArgumentException("either 'vector' or 'text' must be provided");
        }

        var vector = await embeddings.EmbedSingleAsync(query.Text, query.Model, cancellationToken);
        return query with { Vector = vector };
    }

    private static CollectionPlacement BuildPlacement(
        string collection,
        ReplicaRegistry replicas,
        INodeRegistry nodes,
        int targetReplicas)
    {
        var holders = replicas.Holders(collection);
        var connected = nodes.Snapshot(DateTimeOffset.UtcNow);
        var holderNodeIds = connected
            .Where(n => holders.Contains(n.ConnectionId))
            .Select(n => n.NodeId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        return new CollectionPlacement(collection, targetReplicas, holderNodeIds.Length, holderNodeIds);
    }

    // Under postgres there are no node replicas; placement is uniformly zeroed.
    private static CollectionPlacement ZeroPlacement(string collection) =>
        new(collection, 0, 0, Array.Empty<string>());

    private static IResult Error(int statusCode, string message) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    private static string ActorOf(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null) return "admin";
        return System.Net.IPAddress.IsLoopback(ip) ? "local" : ip.ToString();
    }
}
