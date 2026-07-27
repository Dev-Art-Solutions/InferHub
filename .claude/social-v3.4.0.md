# Social — v3.4.0

## Facebook

InferHub 3.4: the node now heals its own Ollama.

Ollama wedges sometimes — process alive, port open, nothing answering — and until now that quietly took a whole node out of the fleet until a human noticed. The node now probes its local Ollama and brings it back: connection refused means start it, accepts-but-never-answers means stop it first and then start. Two symptoms, two cures.

Restarts are budgeted (three per ten minutes, then it stops and says so) because a supervisor that restarts a server every fifteen seconds never lets a model finish loading. It keeps probing after it gives up, so a recovery is still picked up on its own.

It won't touch a shared or remote Ollama — that one isn't ours to bounce — and installing Ollama where it's missing is a separate opt-in with the command logged before it runs.

Off by default; one key turns it on. Still zero new dependencies.
👉 https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.4.0

## X

InferHub 3.4: the node supervises its own Ollama.

Wedged Ollama (port open, no answers) used to take a node out of the fleet silently. Now: probe, classify — refused = start, wedged = stop then start — restart on a budget, then keep probing so recovery is noticed anyway.

Loopback-only. Auto-install is its own opt-in. Zero new deps.

https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.4.0
