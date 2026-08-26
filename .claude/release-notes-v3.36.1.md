# InferHub v3.36.1 — v3.36.0 fixed the 404 for sixty seconds, and then gave it back

v3.36.0 shipped this morning with one claim: a model held only by a node whose inference backend is
dead comes back as **`503` naming the backend**, never the `404` that means *no such model*.

Pulling the published image and killing a real backend under a real node shows the claim is true —
and then stops being true, about twenty seconds later:

```
T+0    healthy                                   200
T+8    backendHealth=healthy    modelCount=1      502   (probe threshold not crossed yet)
T+14   backendHealth=unreachable modelCount=1     503   every node holding model 'llama3:latest'
                                                        reports an unhealthy inference backend
T+20   backendHealth=unreachable modelCount=0     404   model 'llama3:latest' not found
T+40   backendHealth=unreachable modelCount=0     404   model 'llama3:latest' not found
```

Six seconds of the new behaviour, then the old one, permanently.

## Why

Since v3.4 a node whose backend is broken reports **zero models**, and the coordinator replaces the
node's list wholesale. That emptiness *was* the unrouting mechanism, and v3.36 did not remove it —
it added a better one beside it and left the old one running.

So the sequence is: backend dies → the heartbeat carries `unreachable` within a couple of probes →
the refusal is correct → and then, one model-refresh interval later (60s by default), the empty
report lands, the model leaves the registry, and there is no longer any model for the hub to refuse
*by name*. It falls through to `404`.

Every test in the suite passed. They had to: the unit tests drive the registry directly and never
send the empty report that a live node sends on a timer, and the mesh test's node is scripted rather
than one whose backend actually died. **This is a defect only the artefact could show**, which is
the seventh time this project has written that sentence.

## The fix

`NodeRegistry.ReportModels` now **holds** an empty model list from a node that has declared an
unhealthy backend, instead of applying it. The health field is what unroutes a sick node now, so
keeping the inventory costs nothing and buys the whole point of v3.36: the hub can still name the
model it is refusing, and say why.

Three things about it worth stating:

- **The decision is entirely hub-side.** A node older than v3.36 never declares health, so it never
  reaches this branch and behaves exactly as it always did. Nothing on the node changed, and no
  mixed fleet can end up with a hub routing at a box that cannot answer.
- **A healthy node that reports zero models is still emptied**, exactly as before — a box whose
  weights were genuinely deleted says so.
- **The held list is replaced wholesale on recovery**, never merged, which is what makes holding it
  safe. `ModelsRefreshedAt` is deliberately not advanced while holding: it says when that list was
  last true, not when it was last resent.

Three tests, one per sentence above.

## What was not established

- **The measurement above is one run on one box**, with `ProbeInterval` at 5s, `UnhealthyThreshold`
  at 2 and `ModelRefreshInterval` at 20s to compress a minute-long window into something watchable.
  On defaults the good window is up to 60 seconds rather than 6.
- **The backend killed was an HTTP stub, not Ollama.** What was exercised is the node's probe
  classification, the threshold, the heartbeat and the hub's behaviour — not Ollama's own failure
  modes, which phase 36 covers with a real socket in its own suite.
- **`wedged` was not driven end to end on the image.** The stub was stopped, which is
  `unreachable`; the wedge path is unit-tested against a real accept-and-never-answer listener and
  was not re-driven here.
