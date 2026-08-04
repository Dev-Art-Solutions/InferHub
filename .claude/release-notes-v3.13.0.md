# InferHub v3.13.0 — one box, one container: chat, RAG and speech, driven from one page

This closes the six-release **tools-and-fleet** track. v3.8 taught routing *what a node can do*; v3.9
gave a node supervised subprocess workers; v3.10 made the first real ones speech; v3.11 let the hub
configure a fleet within what each operator allowed; v3.12 put a corpus on every node. And the
console still showed a table of nodes and models.

So every "I turned it on and nothing happened" was a support conversation, because the operator's
only evidence was a box behaving exactly as it did before. **v3.13 makes it a row.**

Open `http://your-hub:5080/console.html`.

## The console drives the whole track, with no curl

| Panel | What it answers |
|---|---|
| **Needs attention** | Everything that is *not* doing what it was told, **with the reason**. Above the fold; hidden when there is nothing to say. |
| **Capabilities** | Node × capability, plus a fleet row: how many boxes serve `chat`, `embed`, `transcribe`, `speak`, over how many models. |
| **Tools** | Per node and manifest: allowed or not; running / suspended / stopped / not-allowed; live workers; requests and failures; the last error in the worker's own words. |
| **Node retrieval** | Which node hosts which corpus, on which engine, with how many records — and why it is not running, when it is not. |
| **Node profiles** | The profile book, an editor, apply and delete — and which boxes took which revision, and what each refused. |

### Desired beside effective, always

A profile that asks for `maxConcurrency: 8` against a box whose own config caps it at 2 is not an
error and not a silent no-op. The node applies the 2, reports the refusal **with the key that stopped
it**, and the console shows both. That is the design the whole second half of this track turns on —
the hub can only ever *narrow* a node, and the clamp runs on the node — so "it did not take" without
"and here is what stopped it" reads as a bug.

The worked example this removes: you drop `whisper.json` into the manifest directory and nothing
happens. The node logs it — on a box you are not tailing. The console now shows a **not-allowed** row
and one sentence: *the manifest is on the box but `Tools:Allowed` does not name it.* One config line
instead of an afternoon.

And a pool that is inside its restart budget still reports `running`, because it has not given up.
It also holds no worker and will fail every request it is declared for, so the console renders it
`running · no worker` in amber and puts it on the strip. A green pill there would be exactly the lie
this panel exists to stop telling.

## The hub now knows what a node's tools are doing

Until this release the only thing a coordinator learned about a node's tools was the capability
declaration folded into its model report. A manifest present but not allowed, a pool a profile had
suspended, and a pool that had given up were **all the same thing at the hub: nothing** — and each
has a different fix.

Nodes now report their tool runtime up the connection they already opened, on the model-refresh loop
and immediately after a profile touches it. It is the v3.12 corpus mailbox verbatim: the node
reports, the hub records, and **the hub never asks**. A console that dialled the fleet could not show
you the node that stopped answering.

A node running v3.12 or earlier against a v3.13 hub is fine — it reports nothing and the panel says
so, rather than claiming the box has no tools.

## `/metrics` gained the series to alert on

| Series | Labels |
|---|---|
| `inferhub_capability_nodes`, `inferhub_capability_models` | `capability` |
| `inferhub_tool_requests_total` | `node`, `tool`, `outcome` |
| `inferhub_tool_workers` | `node`, `tool`, `state` |
| `inferhub_tool_pool` | `node`, `tool`, `state` |
| `inferhub_audio_seconds_total`, `inferhub_audio_characters_total` | `kind`, `model` |
| `inferhub_profile_state` | `profile`, `state` |
| `inferhub_node_corpus_records` | `node`, `collection` |

**Absence stays absence.** A capability nobody serves, a tool nobody loaded, a profile nobody wrote
and a corpus nobody assigned each produce **no series at all** rather than a zero — the rule the
per-node throughput gauges have followed since v2.10. `transcription capacity: 0` on a fleet that was
never asked to transcribe pages somebody at three in the morning about a feature nobody turned on.

The two audio counters are separate on purpose: a transcription is metered in **seconds** and a
synthesis in **characters**, and one summed `units` series would add the two into a number nobody can
tell is wrong. Alert on `inferhub_profile_state{state="refused"}` and `{state="conflict"}` — both
mean a box is not doing what your fleet configuration says it should.

The formatter still measures nothing. Every number comes from something that already counted it.

## Four images, and now a chooser

Four artifacts with no decision table is how somebody pulls 6 GB to run a 340 MB workload, or pulls
the small one and wonders where the audio went.

| Pull this | Size | Arch | When it is the right one |
|---|---:|---|---|
| `inferhub-coordinator` | ~120 MB | amd64 + arm64 | The always-on host. No GPU — it routes. |
| `inferhub-node` | ~340 MB | amd64 + arm64 | You already run Ollama, vLLM or a hosted endpoint. Also solo mode, and a vector-store-only box. |
| `inferhub-node:ollama` | ~4 GB | amd64 | One `docker run` on a GPU box with nothing on the host. |
| `inferhub-node:tools` | ~6 GB | amd64 | The above **plus speech** — `faster-whisper` and `piper`. |

Do not pull `:tools` for chat; do not pull `:ollama` to sit next to an Ollama you already run. Both
are in the README with the reason.

## One end-to-end walkthrough

The track's story is a single narrative and the docs told it in five pieces. The README now tells it
once, top to bottom: a coordinator on a small always-on host, one GPU box, chat, then a corpus and
speech configured from the hub — no file edited on the GPU machine after the first `docker run`, no
restart, and inference never stops while you do it.

## Upgrading

Nothing to do. **A deployment that changes no config behaves exactly as it did on 3.12.** No new
configuration keys, no behaviour change on any request path, no new dependency, and the UI is still
plain HTML, CSS and JavaScript with no build step.

---

## v3.13.1 — a dashboard fix found by scraping the published image

A tool manifest that `Tools:Allowed` does not name has **no worker pool at all**. v3.13.0 still
emitted `inferhub_tool_workers{…,state="idle"} 0` and two `inferhub_tool_requests_total` zeros for
it — four permanent zeros per unallowed manifest per node, describing a pool that does not exist.

That is the exact thing this release argues against, shipped inside the code that argues it. The
`inferhub_tool_pool{…,state="not-allowed"} 1` series already carried the whole of what is true.

v3.13.1 drops the worker and request series for `not-allowed` rows **only**. A **suspended** or
**stopped** pool keeps its counters, because those are real history. Nothing else changed.
