# Social copy — v3.50.0 (phase 85: Node:OnDemand, one GPU service at a time)

Blog: https://blog.devart.solutions/blog/inferhub-3-50-one-card-one-service-at-a-time
Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.50.0

## X / Twitter

InferHub v3.50: your desktop GPU back between jobs. Audio, video, LLM take the card one at a time, and each is released when it goes quiet. It unloads only the models the node loaded, not the ones your other apps use.

https://blog.devart.solutions/blog/inferhub-3-50-one-card-one-service-at-a-time

## Facebook / LinkedIn

**One card, one service at a time.**

Every InferHub node so far has assumed its GPU belongs to it: Ollama keeps a chat model loaded, and each tool (transcription, speech, images, video) keeps a warm worker. That is the right default on a dedicated box. It is the wrong one on a desktop, where the one card is also yours.

v3.50 adds an opt-in mode, Node:OnDemand. The local Ollama and every tool now take the card in turn. A request for a different service waits for the current job to finish (nothing is cut off mid-render), the current service is released, and the new one loads and runs. When a service has been quiet for 30 seconds, it gives the card back even if nobody else asked. A tool's worker process is stopped outright, because a live process holds GPU memory that only its exit returns.

One check changed the design before it shipped. The first version unloaded every model Ollama had loaded. On the real machine, another program had a 22 GB model loaded through the same Ollama, and unloading it would have made that program load it all over again. So the node now unloads only the models it loaded itself. The live run confirmed it: our test model was gone five seconds after its answer, and the other program's model never moved.

The price is cold starts: every switch reloads a model. It is meant for a box that does one kind of job at a time. Still to be verified: a real speech or image worker switching on a GPU. The tool path is tested with a real process, but not a GPU one yet.

https://blog.devart.solutions/blog/inferhub-3-50-one-card-one-service-at-a-time
