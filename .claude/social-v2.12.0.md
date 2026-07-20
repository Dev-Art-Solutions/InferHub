# InferHub v2.12.0 — social copy

**Status: written, NOT posted.** No FB/X connector — the standing debt across every release since v2.4.
Post by hand.

---

## Facebook

> InferHub 2.12 makes sticky routing survive a restart.
>
> Sticky conversations keep a chat warm on the node that already loaded its model. Until now that
> pin was tied to the SignalR connection — so it broke twice: a coordinator restart dropped every
> warm conversation, and (the sneaky one) a node bouncing its own connection lost its warm
> conversations even while it stayed up, because a connection id isn't stable across a reconnect.
>
> 2.12 re-keys affinity onto the stable node identity. A node reconnecting with a new connection
> keeps its conversations, full stop. And with opt-in file persistence, a coordinator restart keeps
> them pinned too — no cold model reload for a chat that was mid-flight.
>
> It's off by default and byte-identical to 2.11 when off. When on, the persisted map is a derived
> cache of routing hints — a lost or stale entry costs one cold model load, never a wrong answer, so
> it never becomes a second source of truth. The affinity key is still a header or a hash of the
> opening message; no conversation content is stored.
>
> This is also the groundwork for the warm-standby coordinator coming in 3.0 — failover needs warm
> routing that can survive a hub switch, and that starts here.
>
> Self-hosted, MIT, .NET 10. Still zero new dependencies.
> 👉 https://github.com/Dev-Art-Solutions/InferHub

## X

> InferHub v2.12: affinity that survives reconnects and (opt-in) restarts.
>
> Sticky conversations now key on the stable node id, so a node reconnecting keeps them — and with
> file persistence a coordinator bounce keeps them pinned too. Derived hints, nothing retained.
>
> Off by default. Zero new deps. Groundwork for 3.0 warm failover.
> https://github.com/Dev-Art-Solutions/InferHub
