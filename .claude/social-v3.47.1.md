# Social copy — v3.47.1 (phase 82: Node:ResourceLimits, a node-local CPU/GPU ceiling)

Blog: (to be filled in once published)
Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.47.1

## X / Twitter

Every InferHub node limit a coordinator could always narrow further. v3.47 adds the first one it
can't even see: an optional local CPU/GPU cap the box's own operator sets, with a page the node
serves itself to change it live, no restart.

https://blog.devart.solutions/blog/inferhub-3-47-...

## Facebook / LinkedIn

**A coordinator can lower an InferHub node's concurrency, its VRAM budget, which tools it may run —
every ceiling has a field a hub-sent profile can narrow further. v3.47 adds the first one that isn't
on the wire at all.**

`Node:ResourceLimits` is a purely local cap on how much of a box's own CPU and GPU it lets itself be
routed to consume — optional, off by default, and untouchable from a coordinator, trusted or
compromised. Set it and the node polls its own usage (hand-rolled per platform, zero new
dependencies) and withdraws itself from new placement after a few consecutive over-cap readings —
the same way it already announces an unhealthy backend, and with the same 503 + Retry-After shape a
standalone node's concurrency gate has always used. Nothing already running is touched.

A second, harder mechanism sits beside it for Windows: a real OS-level CPU rate limit on the node's
own tool-worker processes.

And instead of a separate desktop app, the node grows a small page it serves itself — live CPU/GPU
readings, the current throttle state, and a save that reaches the running node within seconds, no
restart.

Verified live: a node capped at an unreachable 1% tripped in two polls, refused a real request with
the 503 above, and then accepted a raised cap through the page with zero downtime.

https://blog.devart.solutions/blog/inferhub-3-47-...
