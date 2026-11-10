# InferHub v3.96.1: a stopped group leaves the catalogue

A fix to v3.96.0, found by running the published 3.96.0 images, which is the last step of every release here.

**What was wrong.** The lab revoked one member of a running group. The coordinator took the group back and the head stopped
it, as designed, but the model stayed in `/v1/models` for good. The head's own engine (an Ollama that is not in the lab's
container) is down, and since phase 69 the coordinator *holds* the last model list of a node whose engine is unhealthy, so it
can still name a model it refuses and say why. That held list also kept the group.

**The fix.** A head reports its groups exactly, even while its own engine is down (v3.96.0). So when the coordinator holds a
node's list, it now holds the engine's models only, and drops the models marked as a shard group. Measured on images built
from this commit: the model left `/v1/models` within 10 seconds of the revoke and stayed gone at 60.

Why the source-built lab for v3.96.0 did not show it: there the head's empty model report arrived before its heartbeat had
marked its engine unreachable, so nothing was held. On the published images the order was the other way round.

Everything else is v3.96.0. Release notes: <https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.96.0>.
