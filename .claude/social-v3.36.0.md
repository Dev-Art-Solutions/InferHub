# Social copy — v3.36.0 (and the v3.36.1 / v3.36.2 patches)

Unposted. Iliya posts by hand (no connector).

**Post link:** https://blog.devart.solutions/blog/inferhub-3-36-the-node-was-up-and-could-not-answer
(slug `inferhub-3-36-the-node-was-up-and-could-not-answer`, ID `6a8f6c533d44b417d01c162d`,
EN-visible / BG-hidden.)

## X — the bug angle (261 chars; the link counts as 23)

> Your node is up. Its Ollama died 20 min ago.
>
> InferHub answered: 404 model 'llama3' not found — for a model sitting on that box's disk.
>
> v3.36: the node declares healthy/unreachable/wedged on its heartbeat. Now it's a 503 that names
> the backend.
>
> [link]

## X — the artefact angle (247 chars), stronger for people who ship their own software

> We shipped a release that stops InferHub saying "model not found" when a node's inference server
> is dead.
>
> Then we pulled the image and ran it. The fix lasted 6 seconds.
>
> Two patches later it holds for 90+. The tests were green the whole time.
>
> [link]

*Counts include the URL as 23. No backticks: X renders them literally.*

## Facebook / LinkedIn

> **InferHub v3.36 — your node was up, its Ollama was dead, and we told you the model did not
> exist.**
>
> Since v3.4 an InferHub node has watched its own Ollama and restarted it. When the backend breaks,
> the node reports zero models, and that is what stops the fleet dispatching to it. That part
> worked.
>
> But at the coordinator, "a node reporting zero models" and "a node with nothing installed" are the
> same thing. So the model dropped out of the registry and the client got:
>
> `404 model 'llama3' not found`
>
> …for a model on that machine's disk, three feet from an inference server that needed restarting.
> The routing was right. The diagnosis sent you to re-download nine gigabytes you already had.
>
> v3.36 puts the node's verdict on the heartbeat — healthy / unreachable / wedged, three states
> because the cures differ. The hub stops routing there, **keeps the model listed** (a client that
> can't see a model can't be told why it's unavailable), and refuses with `503 every node holding
> model 'llama3' reports an unhealthy inference backend`. The console shows which state it is,
> beside "online" rather than instead of it — the connection genuinely is up, and saying so is half
> the diagnosis.
>
> One more split worth naming: **watching is not restarting.** Bouncing a shared Ollama because one
> node's link hiccuped is a four-node outage, so that still needs consent and still has to be local.
> But asking a server whether it's alive is what the next request does anyway — so watching is on by
> default now. A node backed by a cloud vendor is never probed: a poll every fifteen seconds against
> a vendor is a billed request.
>
> And then the part we'd rather tell you than have you find.
>
> We shipped it, then pulled the published container and killed a real backend under a real node.
> The new 503 arrived at T+14 — and at T+20 the old 404 came back, permanently. **Six seconds of the
> release.** A second, older mechanism was still running and undid it one refresh interval later.
>
> The first patch half-fixed it. The second went at the root: the node's model listing was catching
> its own failure and returning an empty list — reporting a failure to the coordinator **as data**,
> which no hub can tell from a box whose weights were deleted. *"Could not ask" is not "has none".*
> It now says null, and the node sends nothing at all.
>
> Every test passed through all of it, and had to: they drive the registry directly and never send
> the report a live node sends on a timer. Seventh time this project has needed the published
> container to see something a green suite could not.
>
> Full write-up, with the timings: [link]

## Notes

- **No image.** The honest visual would be a screenshot of a 404, which is the thing being removed.
  A console screenshot with a red `backend wedged` pill would be the fair one — it does not exist
  yet; `scratchpad/` only has the empty-hub console shot from phase 68.
- The FB copy leads with the bug and lands on the artefact story. If a shorter version is wanted,
  cut everything between "watching is not restarting" and "And then the part we'd rather tell you" —
  the two halves stand on their own.
