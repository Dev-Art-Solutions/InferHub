# InferHub v3.63.1 — a long download or conversion no longer stops a node answering

Found by running the published `3.63.0` images, the same evening.

## Fixed

- **A node ran nothing else while a model command ran.** SignalR hands a client the hub's calls one at a
  time and waits for each handler, and the node's handler for a model command awaited the whole command.
  So while a node converted a checkpoint for colibri (four minutes for OLMoE) — or pulled a large model into
  Ollama, which has been true since model commands shipped in phase 26 — every chat sent to it waited, and
  the measured symptom was a GGUF chat that timed out at 120 s during a conversion. Model commands now run
  off the dispatch: the command reports its progress and outcome on its own stream, as before.
- The colibri catalogue no longer warns about its own `.converting-<name>` staging directory as if it
  were a misnamed model.

## Found, and deliberately not fixed here

The same one-at-a-time dispatch applies to **inference and tool jobs**: a node runs one hub job at a time,
whatever its `Node:MaxConcurrency` (measured: a chat for a loaded colibri model waited 5.6 s behind another
model's load). Running jobs concurrently was tried and broke three cancel tests: the hub sends the next image
job the moment one is cancelled, while that job's worker is still finishing — an ordering the one-at-a-time
dispatch had been hiding. Changing how every node in a fleet takes work is its own phase, with its own
tests, and is not a side effect of a patch release. Recorded for the next phase.

## Verified

Unit and mesh suites: 1956 passed, 61 skipped. A new mesh test holds a conversion open and checks that a
chat to the same node is answered while it runs. 

## The published-image check

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.63.1` and `inferhub-node:3.63.1-colibri`, both with
`org.opencontainers.image.revision` = `67f66e6` (the tag), keys on, a fresh volume, a llama.cpp router and a
colibri catalogue. The GGUF was pulled and routable; then the OLMoE conversion was started, and **while
`convert_olmoe_merged.py` was running** three chats to the GGUF model through the hub answered in 0.49 s,
0.06 s and 0.06 s (under 3.63.0 the same chat timed out at 120 s). The staging directory was not listed
and the catalogue logged no warning about it.

## Upgrading

Nothing to change.
