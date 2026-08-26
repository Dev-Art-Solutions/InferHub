using System.Reflection;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Configuration;
using InferHub.Node.Retrieval;
using InferHub.Shared.Vector;
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
        var services = httpContext.RequestServices;
        var gate = services.GetService<LocalConcurrencyGate>();
        var models = LocalApiEndpoints.VisibleModels(
            await backend.ListModelsAsync(cancellationToken) ?? [],
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
                gpu = GpuBlock(),
                // Phase 40. In solo mode this is what the node will and will not answer — the
                // same declaration a meshed node sends the hub, enforced here at the edge instead
                // of by a router that is not there.
                //
                // The tool runtime's live capabilities are folded in, exactly as CoordinatorConnection
                // folds them into a model report (phase 41). Without them a tools-only box — no
                // Ollama, a running Whisper — reported `capabilities: []` while happily serving
                // transcriptions, which is the one page an operator checks to find out why nothing
                // is being routed to it. Found by pulling the :tools image and looking at it.
                capabilities = Capabilities.BackendCapabilities
                    .Declare(models, backend.Kinds, node.Capabilities, services.GetService<Tools.IToolRuntime>()?.Capabilities)
                    .Select(capability => capability.Kind)
                    .ToArray(),
                retrieval = await RetrievalBlockAsync(services, cancellationToken),
                models = models.Select(model => new { name = model.Name, digest = model.Digest, size = model.SizeBytes })
            },
            LocalApiEndpoints.JsonOptions);
    }

    /// <summary>
    /// Phase 39. "Is it using my card" is the single most actionable fact about a bundled node, so
    /// it is on the status document rather than only in the boot log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately <em>not</em> on <c>/health</c>, which is unauthenticated (phase-37 D5): the
    /// hardware inventory of a box is not something to hand to anyone who can reach the port.
    /// </para>
    /// <para>
    /// It reports what <em>this process</em> can see, which is the question a missing
    /// <c>--gpus all</c> turns on. It does not claim to know where a given model ended up:
    /// <c>ollama ps</c> knows the CPU/VRAM split and reaching it would mean an Ollama-specific
    /// method on <c>IInferenceBackend</c>, which is design rule 1. The docs point at
    /// <c>docker exec … ollama ps</c> for that.
    /// </para>
    /// </remarks>
    private static object GpuBlock()
    {
        var devices = Backends.CudaDeviceProbe.Current;

        return new
        {
            cuda = devices.Available,
            devices = devices.Count,
            names = devices.Names
        };
    }

    /// <summary>
    /// Phase 38. What a solo operator can actually act on — is there a corpus, how big is it, and
    /// which model embedded it.
    /// </summary>
    /// <remarks>
    /// It still does not fake a fleet (phase-37 D5). There is no replica count, no under-replication
    /// gauge and no queue block here, because a node with no coordinator has no concept of any of
    /// them, and a dashboard reading a zero from a process that cannot have a non-zero is being
    /// lied to. <c>enabled: false</c> is the honest answer for a node with retrieval off, and it is
    /// the answer that tells somebody why their <c>X-InferHub-Retrieve</c> header got a 501.
    /// </remarks>
    private static async Task<object> RetrievalBlockAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        // Phase 44: the corpus is asked for a lease rather than resolved from DI, because it can now
        // start and stop under a running node. `enabled: false` still means the same thing to whoever
        // is reading this page after a 501 — there is no corpus here right now.
        var host = services.GetService<RetrievalHost>();
        using var lease = host?.TryLease();

        if (lease is null)
        {
            return host?.LastError is { } error
                ? new { enabled = false, error }
                : new { enabled = false };
        }

        var options = services.GetRequiredService<IOptions<LocalRetrievalOptions>>().Value;
        var collections = await lease.Corpus.Store.ListCollectionsAsync(cancellationToken);

        return new
        {
            enabled = true,
            provider = lease.Corpus.Provider,
            embeddingModel = options.DefaultEmbeddingModel,
            mode = options.Retrieval.Mode,
            rerank = options.Retrieval.Rerank,
            collections = collections.Select(c => new
            {
                name = c.Name,
                dimension = c.Dimension,
                distance = c.Distance,
                records = c.RecordCount
            })
        };
    }

    private static string Version()
        => typeof(LocalStatusEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? typeof(LocalStatusEndpoints).Assembly.GetName().Version?.ToString()
            ?? "unknown";
}
