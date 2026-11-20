# v3.108.0 — social copy

Blog: https://blog.devart.solutions/blog/inferhub-3-108-a-member-loads-from-its-own-disk

## Facebook

InferHub 3.108: when one model is split across several machines, a machine that already has the model file now loads its share from its own disk. The head no longer has to send it over the network.

On an 8B model over three machines, the first load sent 473 MiB instead of 2 868. Over a 50 Mbit/s link it answered after 165 seconds instead of 582, with the same text as the whole model.

No change to llama.cpp: each machine fills the worker's own cache with the exact bytes the head would have sent.

https://blog.devart.solutions/blog/inferhub-3-108-a-member-loads-from-its-own-disk

## X

InferHub 3.108: a machine in a split-model group now loads its layers from its own copy of the file.

8B over 3 machines, first load: 473 MiB sent instead of 2 868, 165 s instead of 582 s at 50 Mbit/s. Same text, no patched llama.cpp.

https://blog.devart.solutions/blog/inferhub-3-108-a-member-loads-from-its-own-disk
