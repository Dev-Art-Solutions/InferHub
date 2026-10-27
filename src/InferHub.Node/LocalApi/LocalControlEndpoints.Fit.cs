using InferHub.Node.Fit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace InferHub.Node.LocalApi;

/// <summary>
/// Phase 118 — <c>POST /api/control/fit</c> (a link and a context → this node's options, with the arithmetic) and
/// <c>POST /api/control/fit/install</c> (the chosen option → the pull the node already runs, its events on the stream);
/// phase 119 — <c>GET /api/control/fit/catalog</c> (what is trending and popular, ordered by what fits here).
/// </summary>
internal static partial class LocalControlEndpoints
{
    internal sealed record FitRequest(string? Link, int? Context, string? Engine, string? Variant, bool Force = false);

    private static async Task<IResult> FitAsync(HttpContext context, FitRequest body, CancellationToken cancellationToken)
    {
        var service = context.RequestServices.GetRequiredService<FitService>();

        try
        {
            return Results.Json(await service.EstimateAsync(body.Link, body.Context, cancellationToken), Json);
        }
        catch (FitRefusedException ex)
        {
            return Error(ex.Status, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Error(502, $"the model could not be looked up: {ex.Message}");
        }
    }

    private static async Task<IResult> FitInstallAsync(HttpContext context, FitRequest body, CancellationToken cancellationToken)
    {
        var service = context.RequestServices.GetRequiredService<FitService>();

        if (string.IsNullOrWhiteSpace(body.Engine))
        {
            return Error(400, "name the engine to install for (engine: ollama, llamacpp, colibri or strata) — the estimate's options say which can");
        }

        try
        {
            var command = await service.CommandAsync(body.Link, body.Engine.Trim().ToLowerInvariant(), body.Variant, body.Context, body.Force, cancellationToken);
            return StartModelCommand(context, command);
        }
        catch (FitRefusedException ex)
        {
            return Error(ex.Status, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Error(502, $"the model could not be looked up: {ex.Message}");
        }
    }

    private static async Task<IResult> CatalogListAsync(HttpContext context, string? source, string? sort, string? kind, string? q, int? limit, bool? fitsOnly, CancellationToken cancellationToken)
    {
        var service = context.RequestServices.GetRequiredService<CatalogService>();

        try
        {
            return Results.Json(await service.ListAsync(new CatalogService.Request(source, sort, kind, q, limit, fitsOnly ?? false), cancellationToken), Json);
        }
        catch (FitRefusedException ex)
        {
            return Error(ex.Status, ex.Message);
        }
    }
}
