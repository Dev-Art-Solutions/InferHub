# InferHub v3.71.0 — jobs side by side, a cancel that arrives, and an Ollama stream that streams

Phase 107. One claim and three older defects that the claim exposed.

- **A meshed node runs the hub's jobs side by side.** SignalR's .NET client hands a connection's calls over one at a
  time and waits for each handler; the node's job handlers awaited the whole job, so a node ran **one job at a time**
  whatever `Node:MaxConcurrency` it declared, and a `CancelJob` waited behind the job it was meant to stop. Jobs now run
  off that loop. The hub still holds a node to its declared `MaxConcurrency`; `Node:MaxConcurrency: 1` is the way back.
- **A cancel reaches the node.** A blocking job whose caller went away — a chat whose client hung up, a picture or a
  clip `DELETE`d mid-render — was marked cancelled at the hub and **never cancelled on the node**: the hub's
  `CancelJob` lived in a `using` registration that `WaitAsync` disposed before it could run. The same hole existed for a
  stream abandoned before its first chunk. Both now send the cancel themselves.
- **A cancelled picture or clip waits for the node's answer.** The hub used to free the node at once, and the image
  pump sent the next job to a worker still winding down. Now the cancel is a request and the dispatch waits for the
  result, bounded by the dispatch deadline — so a job cancelled on its last step may still come back `succeeded`, which
  is what the API has always said.
- **An Ollama node's stream now streams.** OllamaClient 2.0.1 read a whole streamed body before returning its first
  line, so `stream: true` through any Ollama node — meshed or solo — arrived in one piece at the end. `OllamaBackend`
  now reads chat, generate and pull line by line, with the same request and the same chunk JSON.

## What changes with no config changed

| Before | After |
|---|---|
| Two chats sent to one node: the second waited for the first | both run; a local Ollama decides its own parallelism (`OLLAMA_NUM_PARALLEL`) |
| A client hanging up on a blocking chat: the node generated to the end | the node is told and the backend's request is cancelled |
| `DELETE /api/images/jobs/{id}` at step 2 of 20: the worker rendered all 20 | the worker gets its `cancel` frame; a deaf worker is killed after `Tools:CancelGraceSeconds` |
| `stream: true` through an Ollama node: the first line arrived with the last | lines arrive as Ollama writes them (warm `qwen2.5:0.5b`: 6.3 s → 0.016 s to the first line) |
| A burst at a one-worker tool: queued unseen in SignalR's loop until the hub's deadline | queued in the pool (`Tools:QueueMaxWaitSeconds`, 30 s), then `503` + `Retry-After`, as solo mode always answered |

The last row is the one to read twice: it is a behaviour change for a deployment that bursts transcriptions or
syntheses at a node with one worker, and it is the answer a solo node has given since v3.5.

## Found by running it

A real coordinator and a real meshed node from source, against the Ollama on this box (`qwen2.5:0.5b`):

- Two concurrent `/api/chat` requests: both `Running chat job` lines in the node's log before either `Completed`.
- A blocking chat with `curl -m 1`: `Canceling job … at coordinator request`, then `Inference job … was canceled`.
  **That run found a fourth thing**: the cancelled job then tried to send its result on its own, already-cancelled
  token and logged a warning with a stack trace. Code that had never been reached, because no cancel had ever arrived.
  A cancelled blocking job now sends nothing (the hub dropped it when it sent the cancel) and logs one line.
- A streamed chat with `curl -m 1` **was not cancelled** — and that is how the buffering was found: the first line
  through the hub came at 6.29 s of a 6.33 s answer, 0.45 s straight from Ollama, and the same through a solo node
  (5.68 s) and through the **pre-phase** node binary (6.47 s), so it was neither this phase nor the hub. After the fix:
  0.016 s to the first line warm, and a `curl -m 2` on a stream gave `Canceling job …` and `Streaming job … was canceled`.

## The published binaries

See the addendum below — filled in after the tag, from the images GHCR published.

## Not established

- **How a real Ollama behaves with a fleet's parallel jobs.** Before, a node never sent it more than one; now it sends
  what the hub dispatches, and `OLLAMA_NUM_PARALLEL` (and the model's memory) decide. Measured here with two requests
  on one small model only.
- **A model pull's progress** goes through the same line-by-line reader and should now move while it downloads; no
  pull was run live for this release.
- **The OllamaClient package itself is unchanged.** Its `StreamingPostAsJsonAsync` still buffers; any other consumer of
  2.0.1 streams the same way InferHub did. The fix belongs in that repository's own release.
- A `CancelJob` that reaches a node before its job does is still lost. The hub sends the job first on the same
  connection and checks the token before dispatching an image job, so the window left is the send itself.
- **Two Strata mesh tests failed once each and could not be made to fail again.**
  `AnInstallTheNodeCannotDoIsAFrameNotASilence` in one full-solution run on this box, and
  `TheHubRoutesAStrataModelInstallsAnotherFromALinkAndSwitchesToIt` (waiting for a profile switch) in CI on the phase
  commit; the next CI run on the same code passed, and so did four further full runs here and five Strata runs in
  parallel with a full Mesh run. Neither had failed in the forty CI runs before this phase. Both drive a model command or
  a profile, which this phase did not change — but a hub call can now arrive while the previous job's handler is still
  finishing, and no cause was found, so it is recorded rather than dismissed.

## Tests

`ConcurrentJobsMeshTests` (Mesh — a real hub and a real `CoordinatorConnection`: two held jobs inside the backend at
once; a cancel reaching the backend's own token; a stream abandoned before its first chunk). Reverting the node change
turns the first two red; reverting the hub's `catch` turns the cancel ones red. `OllamaBackendTests.AStreamedChatYieldsEachLineAsOllamaWritesIt`
(Node) withholds the second line until the first has been read, and goes red with `ResponseContentRead`.
`ImageJobTests.AWorkerThatIgnoresACancelIsTerminatedPastTheGraceAndTheNodeKeepsServing` now sets a one-second grace so
the grace — not the end of the job — is what stops the deaf worker; until this phase it passed on the hub's own word.

`dotnet test InferHub.sln`: **2 258 passed / 63 skipped**. Zero new `PackageReference`; `InferHub.Shared.csproj` is
still empty. Phase 102's decisions moved whole into `src/InferHub.Node/Backends/CLAUDE.md` to make room (the node's file
was at 1095 of 1100).
