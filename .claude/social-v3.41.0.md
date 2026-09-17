# Social copy — v3.41.0

**Post link:** not yet published — connector unreachable at release time (`CLIENT_HTTP_NOT_IMPLEMENTED`
dialing `blog.devart.solutions/api/mcp`), a different failure than the known sessionId/org-membership
blips. Draft parked here; do not duplicate — publish once, then fill in the slug/ID/link above.
Intended slug: `inferhub-3-41-the-fleet-turns-a-model-back-on-by-itself`.

## X — the product angle (243 chars; the link counts as 23)

> v3.39 let an admin disable one model on one node. Nothing watched the fleet and turned it back on.
>
> v3.41 does: a background loop re-enables a disabled model the moment real demand hits it — same
> VRAM check a human's own click uses, never overridden.
>
> [link]

## X — the honesty angle (251 chars), the most distinctive of the three

> Shipped this with the wrong trigger first: a fallback counter that structurally could never fire
> for a disabled-but-held model. Cloud burst checks "does anyone hold it," not "does anyone route
> it."
>
> Caught before the tag, by reading the routing code instead of trusting the name.
>
> [link]

## X — the scope angle (232 chars), for people who'd ask "does it also do X"

> No auto-pulls. No scale-in — nothing in this codebase says which node's idle copy to turn back
> off, and guessing would be the one thing the VRAM precheck already refuses to do.
>
> Off and dry-run by default. Log lines first, real writes only if you ask.
>
> [link]

*Counts include the URL as 23. No backticks: X renders them literally.*

## Facebook / LinkedIn

> **InferHub v3.41 — the fleet turns a disabled model back on by itself.**
>
> v3.39 gave an admin a switch: disable one model on one node, no restart, no touching disk.
> Useful for freeing VRAM on purpose or pulling a model out of rotation for testing. What it didn't
> have was a way back — a client kept asking for that model, got a clean 503, and the fix (if the
> model was still sitting there, disabled) needed a human to notice.
>
> v3.41 is that human, automated. A background loop counts "can't serve this right now" refusals
> per model, and past a threshold looks for a node that already holds the model, is healthy, and
> just has it turned off — then runs it through the exact VRAM precheck the admin endpoint already
> uses. A refusal is logged, never overridden: no force=true, ever.
>
> **The part worth telling on its own:** the first version of this read the wrong number — a
> fallback-to-cloud counter, on the assumption a disabled model would trigger it the same way an
> unheld one does. It doesn't. The check that decides whether to burst to a cloud provider asks
> "does any node hold this model," never "does any node currently route it" — so that counter would
> have sat at zero forever, for exactly the case this feature exists to fix. Caught by reading the
> routing code the trigger depends on, before a tag went out, not by trusting that "fallback" was
> the right word for what was needed.
>
> **Scoped on purpose.** No auto-pulls — a candidate must already hold the model on disk.
> No scale-in — there's no per-node signal anywhere in this codebase to justify turning a model
> back *off* somewhere, and inventing one would be exactly the guess the VRAM precheck already
> refuses to make about a model's footprint. And the original idea — move an unhealthy node's
> vector collection to a healthier one — didn't survive contact with how collection ownership
> actually works: the data lives only on the owning node, so reassigning ownership without moving
> it would leave a collection silently empty on its new "owner." Left for a real replication
> feature, not folded in here.
>
> **Off, then quiet, then real.** `AutoScaling:Enabled` defaults false. Switched on,
> `AutoScaling:DryRun` still defaults true — log lines only, until an operator turns real writes on
> too.
>
> Verified twice, the same sequence both times: a from-source coordinator and node, then again
> against the published images. Disable a model → get refused → watch the dry-run log name the
> decision → turn dry-run off → watch a real re-enable → get a real answer on the next request.
> Nobody touched anything in between.
>
> **What wasn't established:** only tried against a single node (so "pick the least-loaded
> candidate" is checked by reading the code, not by watching it choose), and the test node declared
> no VRAM budget, so the "this would not fit" refusal branch wasn't exercised live this time.
>
> Zero new dependencies. Full write-up: [link]

## Notes

- **No image.** This is a background service with log-line visibility, not a console panel yet —
  the honest visual doesn't exist. Don't post a mockup of a UI that isn't shipped.
- Lead with the honesty angle on X if there's only room for one — a shipped-the-wrong-trigger,
  caught-it-ourselves story reads as credible in a way a plain feature announcement doesn't.
- **Blog post itself is not live** — the connector failed to connect this session
  (`CLIENT_HTTP_NOT_IMPLEMENTED`, not the usual sessionId blip). Content is finished in
  `.claude/blog-v3.41.0.md`. Publish it, then come back and fill in the slug/ID/link at the top of
  this file before posting any of the above — the [link] placeholders need the real URL.
