# InferHub v3.36.2 — "could not ask" is not "has none"

v3.36.1 fixed half of a race. This fixes the other half, at its root.

## What v3.36.1 missed

The hub was taught to **hold** an empty model report from a node that had already declared an
unhealthy backend. Running the artefact again showed why that is not enough:

```
T+0    healthy                                   200
T+8    backendHealth=healthy    modelCount=0     404   model 'llama3:latest' not found
T+14   backendHealth=unreachable modelCount=0    404   model 'llama3:latest' not found
T+70   backendHealth=unreachable modelCount=0    404   model 'llama3:latest' not found
```

The empty report arrived at T+4 — **before** the probe threshold had been crossed. At that moment
the hub's picture of the node was still `healthy`, so the hold did not fire, the inventory was
emptied, and there was nothing left to hold by the time the verdict arrived.

On default settings that is not an edge case, it is the *usual* case: the threshold takes
`ProbeInterval × UnhealthyThreshold` (45s) to declare while the model refresh fires every 60s, so a
refresh lands in the window roughly three times out of four.

## The root cause

`OllamaBackend.ListModelsAsync` caught a failed listing and returned an **empty list**. So did
`UpstreamBackend`. The node was reporting a *failure* to the coordinator **as data**, and no hub can
tell that apart from a box whose weights were genuinely deleted.

This is a rule this codebase already has, from phase 23: *"not fetched" must not be confusable with
"not there"* — the reason `VectorEntry` exists as a record minus its vector. The node's model
listing had the same shape and the opposite answer.

## The fix

`IInferenceBackend.ListModelsAsync` returns `IReadOnlyList<ModelInfo>?`, and **null means "could not
be asked"**:

- `ReportModelsAsync` **sends nothing at all** in that case, leaving the coordinator's inventory
  exactly as it is. The heartbeat carries the health verdict within a probe or two, and *that* is
  what unroutes the node — which is the whole design of v3.36.
- The five solo-mode callers that only want to render a list say `?? []`. The compiler found every
  one of them, which is the argument for a nullable return over a sentinel value.
- A backend that genuinely holds nothing still reports an empty list, distinctly, and still empties
  the registry. Two tests, one per sentence.

v3.36.1's hub-side hold stays. It is now defence in depth, and it is what protects a fleet running
3.36.0 or 3.36.1 nodes against a 3.36.2 hub.

## The cost, stated rather than buried

A **v3.36.2 node against a pre-v3.36 hub** no longer unroutes itself through the empty report, so
that hub keeps dispatching to it: a fast `502` naming the connection failure when the backend is
unreachable, and the timeouts phase 36 feared when it is wedged. That is the unusual upgrade
direction — hubs are normally updated first — and it is the trade for the `404` going away
everywhere else.

## What was not established

- **The re-check on the 3.36.2 image is this release's own artefact check**, and it is the third
  time this week that step has found something. It is named here rather than assumed.
- **`wedged` still has not been driven end to end on an image.** The stub is *stopped*, which is
  `unreachable`; the wedge path has a real accept-and-never-answer listener in the node suite and
  nothing more.
- The timings quoted above are one run on one box with compressed intervals (probe 5s, threshold 2,
  refresh 20s), chosen to make a minute-long window watchable.
