# Phase 45 §6 — what the published images found (v3.13.0 → v3.13.1)

Run on 2026-08-04, Windows 11 / Docker Desktop, against a real Ollama on the host (22 models).
Images pulled **anonymously** from GHCR — `inferhub-coordinator:3.13.0` / `-node:3.13.0`, then
`:3.13.1`. No visibility flip was needed; Gotcha 1 confirmed for the tenth time.

## The fleet

One coordinator plus three nodes on a user-defined bridge network:

| Node | Shape | Labels |
|---|---|---|
| `gpu-box` | tools on, two manifests on a read-only mount — one in `Tools:Allowed`, one deliberately not | `role=gpu` |
| `cpu-box` | plain, no tools, no corpus | `role=cpu` |
| `corpus-box` | plain, assigned a `local` corpus by profile | `role=corpus` |

The tool manifests are adversarial on purpose: `broken` points at `/definitely/not/a/real/binary`,
and `echo` is present on disk but not named in `Tools:Allowed`.

## What passed

- **All four tool states are distinguishable at the hub**, which was the gap phase 41 left.
  `broken` reported `state: running` with a populated `lastError` (the real
  `ToolStartException` text, path and all); `echo` reported `allowed: false, state: "not-allowed"`.
  The node's log named the unallowed manifest by id, exactly as phase-41 D2 promised, and now so
  does the hub.
- **The clamp holds on the real artifact.** A profile asking for `maxConcurrency: 99` against a box
  configured at 2 produced an effective cap of **2** and two refusals, each naming the operator's
  own key: `Node:MaxConcurrency on this node is 2; a profile can lower it, never raise it to 99`
  and `Tools:Allowed on this node does not name 'echo'; that list is the operator's grant and a
  profile cannot add to it`.
- **A profile that can be honoured is.** `corpus-boxes` brought a `local` corpus up on `corpus-box`
  at runtime, reported `status: applied`, and the node went on serving chat throughout.
- **Every phase-45 series is present, parseable and correctly labelled** on `/metrics` from the
  published coordinator, and `inferhub_profile_state{profile="gpu-boxes",state="refused"} 1` is the
  one an operator would alert on.
- **Absence stayed absence** where it should: no `inferhub_audio_*` (no audio ran) and no
  `inferhub_node_corpus_records` (the corpus is running but holds no collection until first ingest).

## What it found — and v3.13.1

**A `not-allowed` manifest emitted four permanent zeros.** `inferhub_tool_workers{…,state="idle"} 0`,
`{…,state="busy"} 0` and two `inferhub_tool_requests_total` zeros, for a manifest that has **no
`ToolWorkerPool` at all** — those numbers are synthesised in `ProcessToolRuntime.State` purely to
fill the record. They would have sat on a dashboard for as long as the file was on the box,
describing a pool that does not exist.

That is D2's own complaint, shipped inside the code that argues it. The
`inferhub_tool_pool{…,state="not-allowed"} 1` series already carried the whole of what was true.

Fixed in **v3.13.1** the same day: the worker and request series skip `not-allowed` rows **only**.
A *suspended* or *stopped* pool keeps its counters, because those are real history. Re-verified
against the published `3.13.1` images — `echo` now emits its `tool_pool` series and nothing else.

**A green pill for a tool that cannot run** was found earlier, from source, and fixed before the
tag: a pool inside its restart budget reports `running` (correct — it has not given up) while
holding no worker and failing every request it is declared for. The console now renders that
`running · no worker` in amber and puts it on the strip, and `ToolWorkerPool` clears `lastError` on
a successful start so the field means "the most recent thing that happened here was a failure".

## The console

**Not clicked in a browser** — fourth release running, and it should be said plainly rather than
implied. What *was* done is stronger than the served-assets check of the last three releases: the
**served** `console.js` was executed against the **served** `console.html` and a **live**
`/api/status` payload from the three-node fleet, under a minimal DOM shim, and every panel's real
rendered HTML was inspected.

All seven panels produced real markup with **zero** occurrences of `undefined`, `[object Object]`
or `NaN`. The refusals strip rendered both the profile refusal and the broken tool with their full
reasons; the tools panel rendered the amber `running · no worker` pill; the capability matrix
rendered the fleet row. `#nodes` was empty, correctly — the shim holds no admin key, so
`/api/admin/nodes` 401s, which is the read-only degradation the console is designed for.

What this does not cover: layout, colour, scrolling, the SSE reconnect path, and every click
handler. A real browser session on a real fleet is still the missing step.

## Not done

- The `:tools` image (~6 GB) was not pulled; the tool-state path was exercised with the plain node
  image and mounted manifests, which drives the same reporting code.
- No audio ran, so `inferhub_audio_seconds_total` / `_characters_total` are covered by the unit
  suite only.
- `Fleet:Profiles:Persistence=postgres` was again not run against a real database.
- A node-owned collection was never *ingested into*, so `inferhub_node_corpus_records` has no live
  observation behind it.
