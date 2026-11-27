# InferHub v3.117.0: a node with no language engine still says what its tools do

**Release candidate: `v3.117.0-rc.1`.** Its images carry the version tag only (`:3.117.0-rc.1`, `:3.117.0-rc.1-diffusion`,
…). `:latest`, `:diffusion` and the other floating tags stay on v3.116.0 until the final tag. There is no Windows setup
for a candidate. The node's updater offers stable releases only, so no node is moved onto it.

Phase 156. v3.115 found this and did not change it. A networked node whose language backend could not be asked for its
models sent no model report at all. The report is also what carries the tools' capabilities, so those never reached the
hub either. The example that found it was a `:diffusion` image with no Ollama beside it. It registered and stayed
visible, and the hub never routed a picture to it. Solo mode was unaffected.

## What changed

- **The model report goes out even when the backend cannot list.** It repeats the list this connection last sent, at
  the time that list was true, or an empty list if there was none. The tools are reported as they are now.
- **Nothing the hub already had is lost.** v3.36.2's rule stands: "could not ask" is not "has none". A node whose Ollama
  goes down keeps its models in the hub's inventory, and the heartbeat says why they are unroutable (the `503` that
  names the backend, not a `404`). The only new part is that a tool which changes in the meantime, such as a diffusion
  recipe whose weights finished downloading, now reaches the hub as well.
- **A new connection starts from nothing.** A node that reconnects to a hub while its backend is down reports no models
  until the backend lists again. It never sends a list it did not send on that connection.
- **Release candidates are safe to tag.** A tag with a `-` publishes only its own image tags. Before this, a candidate
  would have moved `:latest`, `:ollama`, `:tools`, `:diffusion` and the rest, and the Windows setup build would have
  failed on the version.

## Tested

- `ANodeWhoseBackendCannotListStillDeclaresItsTools`: a real hub, a real node and a real tool child process, with a
  backend that lists `null`. On v3.116 the hub never learned the tool and the test fails. On v3.117 the tool is declared
  and a job is served end to end.
- `dotnet test InferHub.sln`: 3 050 passed, 74 skipped, 0 failed (Desktop 36, Shared 484, Coordinator 1 131, Node 839,
  Mesh 560).

## What was not established

- The published candidate image, at the time these notes were written. It is checked before the final tag.
- A `:diffusion` node on a GPU drawing a picture through the hub with no Ollama. The test uses the echo tool. The path
  that changed is the same, but the diffusion worker itself was not run.
