using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>Phase-25 plumbing for tests that predate it (and shorthand for the ones that don't).</summary>
internal static class TestUsage
{
    public static UsageMeter Meter(IUsageLedger? ledger = null, AdmissionControl? admission = null)
        => new(
            ledger ?? new InMemoryUsageLedger(),
            admission ?? new AdmissionControl(),
            NullLogger<UsageMeter>.Instance);

    public static IHttpContextAccessor NoHttpContext() => new HttpContextAccessor();

    public static IRequestQueue Queue(INodeRegistry registry, QueueOptions? options = null)
        => new RequestQueue(registry, Options.Create(options ?? new QueueOptions()), NullLogger<RequestQueue>.Instance);

    /// <summary>An anonymous, unlimited client over fresh services — the pre-phase-25 behaviour.</summary>
    public static InferenceCore.ClientContext Context(
        INodeRegistry registry,
        ResolvedClient? client = null,
        AdmissionControl? admission = null,
        IUsageLedger? ledger = null,
        QueueOptions? queueOptions = null)
    {
        admission ??= new AdmissionControl();
        return new InferenceCore.ClientContext(
            client ?? ResolvedClient.Anonymous,
            admission,
            Meter(ledger, admission),
            Queue(registry, queueOptions));
    }
}
