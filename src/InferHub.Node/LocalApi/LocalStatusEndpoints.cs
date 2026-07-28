using System.Reflection;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace InferHub.Node.LocalApi;

/// <summary>
/// <c>/health</c>, <c>/api/version</c> and a deliberately reduced <c>/api/status</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong><c>/api/status</c> is where this phase could most easily lie, so it does not pretend to
/// be the hub's.</strong> Returning the coordinator's document with zeros in the fleet fields would
/// be believed by anything scraping it: a dashboard reading <c>nodesEvicted: 0</c> from a process
/// that has no concept of nodes is worse than one that gets a 404 for a key that was never there.
/// So this is a smaller, different document with a <c>mode</c> discriminator at the top, and a
/// client can branch on it.
/// </para>
/// <para>
/// <c>/health</c> stays open and unauthenticated, exactly as it is on the hub, so a monitor can
/// poll it without holding a key that can spend GPU time.
/// </para>
/// </remarks>
internal static class LocalStatusEndpoints
{
    public static IEndpointRouteBuilder MapLocalStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", (IInferenceBackend backend) => Results.Json(
            new
            {
                status = "ok",
                mode = "solo",
                backend = backend.Name,
                version = Version()
            },
            LocalApiEndpoints.JsonOptions));

        app.MapGet("/api/version", () => Results.Json(
            new { version = Version() },
            LocalApiEndpoints.JsonOptions));

        app.MapGet("/api/status", HandleStatusAsync);

        return app;
    }

    private static async Task<IResult> HandleStatusAsync(
        IInferenceBackend backend,
        IBackendSupervisor supervisor,
        IOptions<NodeOptions> nodeOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var node = nodeOptions.Value;
        var gate = httpContext.RequestServices.GetService<LocalConcurrencyGate>();
        var models = LocalApiEndpoints.VisibleModels(
            await backend.ListModelsAsync(cancellationToken),
            node);

        return Results.Json(
            new
            {
                mode = "solo",
                nodeVersion = Version(),
                nowUtc = DateTimeOffset.UtcNow,
                name = node.Name,
                backend = new
                {
                    name = backend.Name,
                    endpoint = backend.Endpoint,
                    // Null unless the phase-36 supervisor is watching. Absence is a fact and is
                    // reported as absence rather than as "healthy" (phase-28 D5).
                    health = supervisor.IsSupervising ? supervisor.Health?.ToString().ToLowerInvariant() : null
                },
                concurrency = gate is null
                    ? null
                    : new { limit = gate.Capacity, inFlight = gate.InFlight },
                models = models.Select(model => new { name = model.Name, digest = model.Digest, size = model.SizeBytes })
            },
            LocalApiEndpoints.JsonOptions);
    }

    private static string Version()
        => typeof(LocalStatusEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? typeof(LocalStatusEndpoints).Assembly.GetName().Version?.ToString()
            ?? "unknown";
}
