using InferHub.Node.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node.LocalApi;

/// <summary>
/// Enforces <c>Node:MaxConcurrency</c> locally, which is a thing only solo mode does (phase-37 D9).
/// </summary>
/// <remarks>
/// <para>
/// <c>MaxConcurrency</c> has always been <em>advisory</em>: a number reported at registration that
/// the coordinator's router and <c>FleetSaturation</c> respect. In solo mode nobody is respecting
/// it, so fifty concurrent requests all land on one Ollama and the box thrashes. The key's meaning
/// — "this many at once is what this machine can take" — is unchanged; only the enforcer moved,
/// from the hub that is no longer there to the node that is.
/// </para>
/// <para>
/// Over the cap, a request waits up to <c>LocalApi:MaxWaitSeconds</c> and then gets <c>503</c> with
/// <c>Retry-After</c> — the same status and the same header as the hub's <c>RequestQueue</c>
/// (phase-25 D5), so a client's existing retry logic behaves identically against either.
/// </para>
/// <para>
/// Unset means unbounded, and then this type is never registered at all rather than registered with
/// an infinite count: a semaphore nobody can exhaust is still a lock everybody takes.
/// </para>
/// </remarks>
public sealed class LocalConcurrencyGate : IDisposable
{
    private readonly SemaphoreSlim slots;
    private readonly TimeSpan maxWait;

    public LocalConcurrencyGate(IOptions<NodeOptions> nodeOptions, IOptions<LocalApiOptions> localApiOptions)
    {
        var cap = nodeOptions.Value.MaxConcurrency
            ?? throw new InvalidOperationException(
                "LocalConcurrencyGate must not be registered when Node:MaxConcurrency is unset.");

        slots = new SemaphoreSlim(cap, cap);
        maxWait = TimeSpan.FromSeconds(localApiOptions.Value.MaxWaitSeconds);
        Capacity = cap;
    }

    public int Capacity { get; }

    public int InFlight => Capacity - slots.CurrentCount;

    /// <summary>Null when the wait expired — the caller renders a 503 in its own dialect.</summary>
    public async Task<IDisposable?> TryEnterAsync(CancellationToken cancellationToken)
    {
        if (!await slots.WaitAsync(maxWait, cancellationToken))
        {
            return null;
        }

        return new Slot(slots);
    }

    public int RetryAfterSeconds => Math.Max(1, (int)maxWait.TotalSeconds);

    public void Dispose() => slots.Dispose();

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            // A streaming response disposes this when the enumeration ends *and* the endpoint may
            // dispose it on an error path; releasing twice would hand out a slot that does not
            // exist and quietly raise the cap for the life of the process.
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}
