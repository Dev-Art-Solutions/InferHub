# Social copy — v3.13.0 (unposted; Iliya posts by hand)

## X / Twitter

**Thread, 4 posts.**

1/
InferHub 3.13 is out, and it closes a six-release track.

3.8 taught routing what a node can *do*. 3.9 gave nodes supervised subprocess workers. 3.10 made
them speak. 3.11 let a coordinator configure the fleet. 3.12 put a corpus on every node.

And the console still showed a table of nodes and models. 🧵

2/
That gap is not aesthetic. Every one of those features can be switched on and quietly not happen —
a manifest the operator never allow-listed, a profile item the node clamped, a corpus that failed
to start.

In all three cases the box behaves exactly as before. There is nothing to see.

3/
So 3.13 makes it a row, with the *reason* attached:

"the manifest is on the box but Tools:Allowed does not name it, so it was never started"

Real string, real fleet. One config line instead of an afternoon reading logs on a machine you are
not tailing.

4/
Also: a capability matrix, a tools panel, a profile editor with the refusals each node sent back,
and Prometheus series that emit *nothing* for a capability nobody serves — because
"transcription capacity: 0" on a fleet nobody asked to transcribe pages someone at 3am.

MIT, self-hosted: github.com/Dev-Art-Solutions/InferHub

## LinkedIn / Facebook

**InferHub 3.13 — the release that makes the last five usable.**

Over six releases InferHub learned what a node can *do* rather than just what it holds, how to run
supervised child processes, how to transcribe and speak, how to take its configuration from a
coordinator, and how to host a corpus per node.

The console still showed a table of nodes and models.

That gap has a specific cost. Every one of those features can be switched on and quietly not
happen — a tool manifest sitting on a box that was never added to the allow-list, a profile item
the node clamped, a corpus that failed to start. In all three cases the machine behaves exactly as
it did before. There is nothing to see, so it becomes a support conversation.

3.13 makes each of them a row, and — this is the part that matters — a row with the *reason* on it
rather than a status word.

The design underneath is why. A coordinator in InferHub can only ever **narrow** a node, and the
check that enforces it runs on the node, not the hub: a compromised coordinator must not become
fleet-wide remote code execution. The direct consequence for a user interface is that **refusals
are normal**. A profile asking for a concurrency of 8 against a box capped at 2 is not an error and
not a silent no-op — the node applies the 2 and says which key stopped it. So every panel shows
what the hub asked for beside what the node is actually doing.

Also in 3.13: a capability matrix, per-tool worker and error state, a profile editor, a node
retrieval panel, Prometheus series for all of it, a four-image decision table, and one end-to-end
walkthrough instead of five feature sections.

One thing worth admitting publicly. The release argues that a zero on a dashboard is a claim and
should only be made when it is true — and then shipped four permanent zeros describing a worker
pool that does not exist. A green test suite did not catch it; scraping the published image did.
Fixed in 3.13.1 the same day.

Build-free UI, zero new dependencies, and a deployment that changes no config behaves exactly as it
did on 3.12.

MIT-licensed and self-hosted: https://github.com/Dev-Art-Solutions/InferHub
Docs: https://inferhub.devart.solutions
