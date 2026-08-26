# Social copy — v3.36.0

Unposted. Iliya posts by hand (no connector).

## X / Twitter (280)

> Your node is up. Its Ollama died 20 min ago.
>
> InferHub used to answer `404 model 'llama3' not found` — for a model sitting on that box's disk.
>
> v3.36: the node declares healthy/unreachable/wedged on its heartbeat. Now it's a 503 that names
> the backend.
>
> [link]

*(Counted: 279 characters including the placeholder link as 23.)*

## Facebook / LinkedIn

> **InferHub v3.36 — the node was up, its Ollama was dead, and we told you the model didn't exist.**
>
> Since v3.4 an InferHub node has watched its own Ollama and restarted it. When the backend breaks,
> the node reports zero models, and that is what stops the fleet dispatching to it. That part
> worked.
>
> But at the coordinator, "a node reporting zero models" and "a node with nothing installed" are
> the same thing. So the model dropped out of the registry and the client got:
>
> `404 model 'llama3' not found`
>
> …for a model on that machine's disk, three feet from an inference server that needed restarting.
> The routing was right. The diagnosis sent you to re-download nine gigabytes you already had.
>
> v3.36 puts the node's verdict on the heartbeat — healthy / unreachable / wedged, three states
> because the cures differ. The hub stops routing there, **keeps the model listed** (a client that
> can't see a model can't be told why it's unavailable), and refuses with
> `503 every node holding model 'llama3' reports an unhealthy inference backend`. The console shows
> which state it is, beside "online" rather than instead of it — the connection genuinely is up,
> and saying so is half the diagnosis.
>
> One more split worth naming: **watching is not restarting.** Bouncing a shared Ollama because one
> node's link hiccuped is a four-node outage, so that still needs consent and still has to be
> local. But asking a server whether it's alive is what the next request does anyway — so watching
> is on by default now. A node backed by a cloud vendor is never probed: a poll every fifteen
> seconds against a vendor is a billed request.
>
> The omission was written down as a deliberate non-goal in v3.4 and sat there for thirty-three
> releases. It was right to defer — and wrong about what would eventually break the deferral. Not
> routing. The 404.
>
> Zero new dependencies.
>
> [link]

## Notes

- **No image.** The honest visual would be a screenshot of a 404, which is the thing being removed.
  A console screenshot with a red "backend wedged" pill would be a fair one if a screenshot is
  wanted — `scratchpad/` has the empty-hub console shot from phase 68, not this.
