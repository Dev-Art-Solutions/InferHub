# InferHub v3.0.0 — social copy

**Status: written, NOT posted.** No FB/X connector — the standing debt across every release since v2.4.
Post by hand.

Blog post is **live**: https://devart.solutions/blog/inferhub-3-0-warm-standby
("InferHub 3.0: the hub stops being the single point of failure")

---

## Facebook

> InferHub 3.0 removes the last single point of failure.
>
> The always-on coordinator was the mesh's durability anchor by design — which also meant losing it
> lost everything. Nodes could come and go; the hub could not.
>
> Now you can run a second coordinator as a warm standby over the same Postgres. One holds a lease
> and serves; the other waits. Kill the active one and the standby takes the lease, your nodes
> reconnect to it on their own, and the mesh keeps serving — no manual promotion, no data migration,
> because the durable state was already shared.
>
> The part I care most about is the fencing. A coordinator that can't *prove* it still holds the
> lease demotes itself within the TTL, on its own clock, whether or not it can reach anything to
> ask. So a network partition stops a hub serving rather than letting two of them both answer.
>
> That guard had a real bug, and only running it found it: with Postgres pulled out from under a
> live stack, the renew attempt burned its own connect timeout and demotion landed at 23s on a 15s
> lease — an 8-second window where the standby had already taken over and the old primary still
> thought it was in charge. Exactly the split brain the fence exists to prevent. Fixed, and now
> measured at 15s. A fence that can be outrun by its own health check isn't a fence.
>
> Honestly scoped: this is the *foundation* of the HA track. Active-active load sharing and
> clustering the local vector provider are still to come. Off by default — single-coordinator
> setups are byte-identical to 2.13.
>
> Then it happened again after I tagged. I'd fixed the same class of race in one place and written
> a note saying two other places needed it too — and shipped without acting on the note. Pulling the
> published images and cold-booting two hubs killed one of them, so the mesh looked healthy while
> the HA had quietly turned itself off. Pull 3.0.1, not 3.0.0. A hazard you've written down but not
> fixed is still shipped.
>
> Self-hosted, MIT, .NET 10. Still zero new dependencies — the lease is one Postgres row.
> 👉 https://github.com/Dev-Art-Solutions/InferHub

## X

> InferHub v3.0: a warm-standby coordinator over shared Postgres.
>
> Primary dies → standby takes the lease → nodes reconnect on their own → mesh keeps serving. No
> promotion step, no migration. Fenced against split-brain on the hub's own clock.
>
> HA foundation, off by default. Zero new deps.
> https://github.com/Dev-Art-Solutions/InferHub
