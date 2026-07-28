using InferHub.Shared.Vector;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace InferHub.Node.LocalApi;

/// <summary>
/// Collection lifecycle and the raw vector data plane, served by a standalone node (phase 38).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Recorded deviation from the phase brief, and from the hub's shape.</strong> On the
/// coordinator, collection lifecycle lives under <c>/api/admin/vector/collections</c> — and solo
/// mode has no admin surface at all (phase-37 D5), on purpose. So the three lifecycle routes ride
/// the client prefix here instead. Most deployments will never call them: ingesting into a name
/// that does not exist <em>provisions</em> it, on exactly phase-31 D5's reasoning — there the
/// client's configured collection scope was the provisioning grant, and here the node's own config
/// is, because a node has one corpus and one operator. The dimension is still <b>measured</b> from
/// the first embedded batch rather than guessed.
/// </para>
/// <para>
/// The <c>/api/vector/{collection}</c> data plane, by contrast, is the hub's routes verbatim —
/// same paths, same bodies, same statuses — because those a client actually ports between hosts.
/// </para>
/// </remarks>
internal static class LocalCollectionEndpoints
{
    public static IEndpointRouteBuilder MapLocalCollectionEndpoints(this IEndpointRouteBuilder app)
    {
        MapLifecycle(app);
        MapDataPlane(app);
        return app;
    }

    private static void MapLifecycle(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/collections", async (IVectorStore store, CancellationToken ct) =>
            Results.Json(new { collections = await store.ListCollectionsAsync(ct) }, LocalApiEndpoints.JsonOptions));

        app.MapPost("/api/collections", async (
            CreateCollectionBody body,
            IVectorStore store,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name))
            {
                return Error(StatusCodes.Status400BadRequest, "name is required");
            }

            if (body.Dimension is not > 0)
            {
                return Error(
                    StatusCodes.Status400BadRequest,
                    "dimension is required and must be >= 1; or omit this call entirely and let the first ingest measure it");
            }

            try
            {
                var info = await store.CreateCollectionAsync(body.Name!, body.Dimension.Value, body.Distance, ct);
                return Results.Json(info, LocalApiEndpoints.JsonOptions);
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

        app.MapGet("/api/collections/{collection}", async (
            string collection,
            IVectorStore store,
            CancellationToken ct) =>
        {
            var info = await store.GetCollectionAsync(collection, ct);
            return info is null
                ? Error(StatusCodes.Status404NotFound, $"collection '{collection}' does not exist")
                : Results.Json(info, LocalApiEndpoints.JsonOptions);
        });

        app.MapDelete("/api/collections/{collection}", async (
            string collection,
            IVectorStore store,
            CancellationToken ct) =>
        {
            var dropped = await store.DropCollectionAsync(collection, ct);
            return dropped
                ? Results.Json(new { collection, dropped = true }, LocalApiEndpoints.JsonOptions)
                : Error(StatusCodes.Status404NotFound, $"collection '{collection}' does not exist");
        });
    }

    private static void MapDataPlane(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vector/{collection}");

        group.MapPost("/upsert", async (
            string collection,
            VectorUpsert upsert,
            IVectorStore store,
            IEmbeddingDispatcher embeddings,
            CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                var prepared = await ResolveAsync(upsert, embeddings, ct);
                return Results.Json(await store.UpsertAsync(collection, prepared, ct), LocalApiEndpoints.JsonOptions);
            }));

        group.MapPost("/query", async (
            string collection,
            VectorQuery query,
            IVectorStore store,
            IEmbeddingDispatcher embeddings,
            CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                var prepared = await ResolveAsync(query, embeddings, ct);
                var matches = await store.QueryAsync(collection, prepared, ct);
                return Results.Json(new { matches }, LocalApiEndpoints.JsonOptions);
            }));

        group.MapPost("/retrieve", async (
            string collection,
            VectorQuery query,
            IVectorStore store,
            IEmbeddingDispatcher embeddings,
            CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                var prepared = await ResolveAsync(query, embeddings, ct);
                var matches = await store.QueryAsync(collection, prepared, ct);
                return Results.Json(new { matches }, LocalApiEndpoints.JsonOptions);
            }));

        group.MapGet("/{id}", async (
            string collection,
            string id,
            IVectorStore store,
            CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                var record = await store.GetAsync(collection, id, ct);
                return record is null
                    ? Error(StatusCodes.Status404NotFound, $"record '{id}' not found")
                    : Results.Json(record, LocalApiEndpoints.JsonOptions);
            }));

        group.MapDelete("/{id}", async (
            string collection,
            string id,
            IVectorStore store,
            CancellationToken ct) =>
            await GuardAsync(async () =>
            {
                var removed = await store.DeleteAsync(collection, id, ct);
                return removed
                    ? Results.Json(new { id, deleted = true }, LocalApiEndpoints.JsonOptions)
                    : Error(StatusCodes.Status404NotFound, $"record '{id}' not found");
            }));
    }

    /// <summary>The hub's exception-to-status mapping for the data plane, kept identical.</summary>
    private static async Task<IResult> GuardAsync(Func<Task<IResult>> run)
    {
        try
        {
            return await run();
        }
        catch (KeyNotFoundException ex)
        {
            return Error(StatusCodes.Status404NotFound, ex.Message);
        }
        catch (NoEmbeddingNodeException ex)
        {
            return Error(StatusCodes.Status404NotFound, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Error(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Error(StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    private static async Task<VectorUpsert> ResolveAsync(
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

        return upsert with { Vector = await embeddings.EmbedSingleAsync(upsert.Text, upsert.Model, cancellationToken) };
    }

    private static async Task<VectorQuery> ResolveAsync(
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

        return query with { Vector = await embeddings.EmbedSingleAsync(query.Text, query.Model, cancellationToken) };
    }

    private static IResult Error(int statusCode, string message)
        => Results.Json(new { error = message }, LocalApiEndpoints.JsonOptions, statusCode: statusCode);

    internal sealed record CreateCollectionBody(string? Name, int? Dimension, string? Distance);
}
