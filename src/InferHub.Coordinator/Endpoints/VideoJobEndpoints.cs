using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;

namespace InferHub.Coordinator.Endpoints;

/// <summary>
/// The one route the console needs that OpenAI's Videos dialect does not have (phase 59, D4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly one route, deliberately.</b> Phase 47 built a whole <c>/api/images/jobs</c> surface
/// because OpenAI had no asynchronous Images API to adopt; it has an asynchronous <em>Videos</em> API,
/// so the console submits, polls, fetches and cancels over <c>/v1/videos</c> — the same surface a
/// customer's SDK speaks, which is what makes the panel a test of the real thing rather than of an
/// admin shortcut. Enumeration is the single thing that dialect refuses (<c>GET /v1/videos</c> is a
/// 501 naming the reason), and a panel cannot be built without it.
/// </para>
/// <para>
/// <b>Considered and rejected: <c>GET /api/images/jobs?media=video</c>.</b> One route returning two
/// kinds of job whose bytes are fetched from two different places is a query parameter standing in
/// for a scope — and the store's scoping predicate already has the right shape, so the honest version
/// costs one <c>ForClient</c> call.
/// </para>
/// <para>
/// <b>Client-scoped, like the images listing it mirrors</b> (51): never a fleet-wide view, because a
/// video id <em>is</em> the capability to fetch the bytes and an admin console showing every tenant's
/// ids would be phase-25 D4 undone by a UI. The console therefore holds its own client key.
/// </para>
/// </remarks>
public static class VideoJobEndpoints
{
    public static IEndpointRouteBuilder MapVideoJobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/videos/jobs", List);
        return app;
    }

    /// <summary>
    /// This client's video jobs, oldest first.
    /// </summary>
    /// <remarks>
    /// Scoped by capability as well as by client (57 D10): an image job is not in here and its id on
    /// this route is the same 404 a stranger's job earns. <b>It is not a gallery</b> — a finished
    /// clip's bytes are read-once and expire (47 D6), so this lists <em>work</em>, and a delivered
    /// job is still here and still says so with nothing to fetch.
    /// </remarks>
    private static IResult List(HttpContext httpContext, ImageJobRegistry jobs)
    {
        var client = BearerApiKeyMiddleware.ClientOf(httpContext);

        var records = jobs.Store.ForClient(client.Id, CapabilityKinds.IsVideo)
            .Select(record => ImageJobView.Describe(record, jobs.QueuePosition(record.Id)))
            .ToArray();

        return Results.Text(
            JsonSerializer.Serialize(
                new
                {
                    jobs = records,

                    // The same four fleet-wide numbers the images listing reports, and the same
                    // reason: the queue is ONE queue (47 D1), so a panel that showed a video-only
                    // depth would be describing something that does not exist.
                    queued = jobs.Store.Queued().Count,
                    active = jobs.Store.ActiveCount(),
                    retainedBytes = jobs.Store.RetainedBytes(),
                    retentionSeconds = jobs.Store.Options.RetentionSeconds,
                    persistence = jobs.Store.Options.NormalizedPersistence()
                },
                ImageJobView.JsonOptions),
            ImageJobView.ContentType);
    }
}
