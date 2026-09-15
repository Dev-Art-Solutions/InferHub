# Social copy — v3.39.0

Unposted. Iliya posts by hand (no connector).

**Post link:** https://blog.devart.solutions/blog/inferhub-3-39-routing-narrows-per-model
(slug `inferhub-3-39-routing-narrows-per-model`, ID `6aa948d99ec94037300ad294`, EN-visible /
BG-hidden. Note: a first `create_post` at slug `inferhub-3-39-a-model-your-node-holds-is-not-one-it-has-to-serve`
was HTML-escaped and shows its own tags as text — dead slug, needs manual delete in the admin UI,
same v3.34 mistake.)

## X — the product angle (248 chars; the link counts as 23)

> InferHub routing used to be all-or-nothing per capability: a node serves chat, or it doesn't.
>
> v3.39 lets you turn off ONE model on ONE node — stage a 70B for testing without routing traffic
> to it, no restart, no un-pull.
>
> [link]

## X — the honesty angle (256 chars), the most distinctive of the three

> There is no measured VRAM figure for a model anywhere in our codebase. Only disk size.
>
> So when v3.39 lets you enable a model on a node, it estimates, says so out loud, blocks if it
> doesn't fit, and takes force=true if you know better.
>
> [link]

## X — the zero-change angle (243 chars), for people who build routers themselves

> Added a routing lever — "don't send this model to this node" — and the router's code didn't
> change at all.
>
> The node just stops declaring the model. The hub already only ever trusted what a node says it
> has.
>
> [link]

*Counts include the URL as 23. No backticks: X renders them literally.*

## Facebook / LinkedIn

> **InferHub v3.39 — a model your node holds is not one it has to serve.**
>
> Routing in InferHub has always been fully automatic and, until now, coarse. A node declares what
> it holds, and an operator could switch a whole *capability* off for that node — "chat" or "not
> chat" — but never say the sentence that actually comes up in practice: keep this box answering
> chat, just not with the 70B model somebody staged on it for testing.
>
> v3.39 adds that lever, plus two admin actions so using it doesn't mean hand-editing a profile's
> JSON:
>
> ```
> POST /api/admin/nodes/{id}/models/{model}/disable
> POST /api/admin/nodes/{id}/models/{model}/enable?force=false
> ```
>
> Disabling never touches disk and never stops a pull already asked for — a model can be staged
> and hidden from routing at the same time. What changes is only what the node **declares**, which
> means the coordinator's own routing code needed **zero changes** to honour it. The node was
> always the one deciding what it provides; this just gives an operator one more thing to tell it
> not to provide.
>
> The interesting decision is what happens when you flip a model back **on**. There is no measured
> VRAM figure for an Ollama model anywhere in this codebase — only the disk size Ollama reports,
> and the VRAM budget an operator already declares for the image catalogue. `enable` now estimates
> off that and refuses if it doesn't fit:
>
> ```json
> {
>   "error": "'llama3.1:405b' is estimated at 220523 MiB and this node budgets 768 MiB",
>   "hint": "retry with ?force=true to enable anyway"
> }
> ```
>
> It blocks by default, the same posture every resource ceiling in this project takes — and it's
> the one refusal here that ships with an override. Every other narrowing check gates on a fact an
> operator declared: a licence accepted, a tool allow-listed, a real VRAM figure on an image
> recipe. This one gates on a **guess** — a disk size standing in for a resident footprint,
> sometimes borrowed from a different node's copy of the same model — so someone who knows their
> own quantization better than the estimate needs a way past it. The response always says whether
> the check passed or was overridden. Never just "ok".
>
> Vector-collection assignment got the same treatment: `assign`/`unassign` do the exact
> read-modify-write that hand-editing a profile's `retrieval.collections` list always could, with a
> `409` naming the current owner instead of a silent re-parent.
>
> **What was actually checked**, against a real coordinator and a real Ollama-backed node holding
> twenty-nine live models: disabling and re-enabling a model changed routing without touching
> anything on disk; the precheck blocked a genuine 220 GB model against a deliberately tiny test
> budget, with the real reported size in the refusal message; `force=true` pushed it through
> anyway; and collection ownership flipped cleanly between the hub and the node, both directions.
>
> Zero new dependencies. A fleet that defines no `models.disabled` behaves exactly as 3.38.
>
> Full write-up: [link]

## Notes

- **No image.** The honest visual would be a console screenshot of the new toggle, and the console
  was not clicked in a browser this release — verification was curl against a real fleet, not the
  UI. Don't post a mockup of a panel nobody has looked at.
- The zero-change angle is the one worth leading with if there's only room for one X post — it's
  the most InferHub-specific claim (narrow-only profiles, node as the enforcement boundary) and the
  one a technical audience will actually stop scrolling for.
- If the FB copy needs shortening, cut the "What was actually checked" paragraph last — it's the
  credibility anchor, not filler.
