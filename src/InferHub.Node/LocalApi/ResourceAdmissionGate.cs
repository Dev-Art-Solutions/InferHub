using InferHub.Node.Resources;

namespace InferHub.Node.LocalApi;

/// <summary>
/// Solo mode's half of phase 82's enforcement (D4): refuses a new request while this node's own
/// <see cref="IResourceGovernor"/> says it is over its own CPU/GPU cap. Same 503 + Retry-After shape
/// as <see cref="LocalConcurrencyGate"/>, consulted first inside
/// <see cref="LocalApiEndpoints.WithSlotAsync"/> — so a client's retry logic cannot tell a resource
/// refusal from a concurrency one, it is a different reason to wait, not a different way of waiting.
/// </summary>
/// <remarks>
/// Registered only when <c>Node:ResourceLimits</c> is actually configured (see
/// <c>NodeHostBuilderExtensions.AddLocalApi</c>) — the same "absent means no gate object at all"
/// rule <see cref="LocalConcurrencyGate"/> itself uses for an unset <c>Node:MaxConcurrency</c>.
/// A request already in flight is never touched; this only ever refuses admission of a new one.
/// </remarks>
internal sealed class ResourceAdmissionGate(IResourceGovernor governor)
{
    public int RetryAfterSeconds => 15;

    public bool TryEnter(out string reason)
    {
        if (!governor.IsThrottled)
        {
            reason = string.Empty;
            return true;
        }

        reason = governor.Reason ?? "this node is over its own configured resource cap";
        return false;
    }
}
