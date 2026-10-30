using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Phase 124. <c>/api/admin/supply</c> — behind the admin key like every <c>/api/admin</c> route: what the supply holds,
/// a fetch, an import from under <c>Relay:Supply:ImportDirectory</c>, a removal. A fetch and an import are jobs (202);
/// a twenty-gigabyte model is not an HTTP request's lifetime.
/// </summary>
public static class RelaySupplyEndpoints
{
    public static void MapRelaySupply(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/supply");

        group.MapGet("", (RelaySupply supply, SupplyJobs jobs, IOptionsMonitor<RelayOptions> options) =>
        {
            if (!supply.Enabled)
            {
                return Off();
            }

            var relay = options.CurrentValue;
            return Results.Ok(new
            {
                enabled = true,
                relay = relay.Enabled,
                only = supply.Only,
                directory = Path.GetFullPath(supply.Options.DataDirectory),
                importDirectory = Path.GetFullPath(supply.Options.ImportDirectory),
                bytes = RelaySupply.BytesOf(supply.Models()),
                models = supply.Models().Select(model => new
                {
                    model.Id,
                    model.Kind,
                    model.Name,
                    model.Revision,
                    model.Commit,
                    files = model.Files.Count,
                    model.Bytes,
                    model.Source,
                    model.From,
                    model.SuppliedUtc
                }),
                jobs = jobs.Jobs()
            });
        });

        group.MapGet("/{id}", (string id, RelaySupply supply) =>
            !supply.Enabled ? Off()
            : supply.Get(id) is { } model ? Results.Ok(model)
            : Results.NotFound(new { error = $"nothing supplied as '{id}'" }));

        group.MapGet("/jobs/{id}", (string id, RelaySupply supply, SupplyJobs jobs) =>
            !supply.Enabled ? Off()
            : jobs.Get(id) is { } job ? Results.Ok(job)
            : Results.NotFound(new { error = $"no supply job '{id}' (a hub restart forgets jobs, never models)" }));

        group.MapPost("", (SupplyFetchRequest request, HttpContext context, RelaySupply supply, SupplyJobs jobs, ILoggerFactory loggers) =>
            Start(supply, context, loggers, "fetch", () => jobs.Fetch(request)));

        group.MapPost("/import", (SupplyImportRequest request, HttpContext context, RelaySupply supply, SupplyJobs jobs, ILoggerFactory loggers) =>
            Start(supply, context, loggers, "import", () => jobs.Import(request)));

        group.MapDelete("/{id}", (string id, HttpContext context, RelaySupply supply, ILoggerFactory loggers) =>
        {
            if (!supply.Enabled)
            {
                return Off();
            }

            if (!supply.Delete(id))
            {
                return Results.NotFound(new { error = $"nothing supplied as '{id}'" });
            }

            loggers.CreateLogger("InferHub.Coordinator.RelaySupply").LogInformation("Supply: '{Id}' removed by {Actor}", id, Actor(context));
            return Results.Ok(new { id, removed = true });
        });
    }

    private static IResult Start(RelaySupply supply, HttpContext context, ILoggerFactory loggers, string action, Func<SupplyJob> start)
    {
        if (!supply.Enabled)
        {
            return Off();
        }

        try
        {
            var job = start();
            loggers.CreateLogger("InferHub.Coordinator.RelaySupply").LogInformation(
                "Supply: {Action} '{Name}' started by {Actor} (job {Job})", action, job.Name, Actor(context), job.Id);
            return Results.Accepted($"/api/admin/supply/jobs/{job.Id}", job);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static IResult Off()
        => Results.NotFound(new { error = "this coordinator supplies no models (Relay:Supply:Enabled=false)" });

    private static string Actor(HttpContext context)
        => context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
