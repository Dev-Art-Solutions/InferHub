# v3.60.0 social copy (unposted — Iliya posts; blog is live, ID 6ac3afb45a1fa97c305284c9)

## Facebook

InferHub 3.60: Ollama, llama.cpp and colibri on one node, and the hub decides which are running.

A node used to run exactly one inference engine. Now one node can run several by name: an Ollama, a GGUF served by llama.cpp's own server (the node launches it), and a colibri MoE. Each request goes to the engine that holds its model, and the coordinator starts and stops each engine from the console. The node's own list is the limit: the hub can never add an engine or name a binary.

Running it against the real engines found two bugs before release: a killed node left llama-server running, and a booting node briefly launched an engine its profile had stopped. Both fixed.

https://blog.devart.solutions/blog/inferhub-3-60-three-engines-one-node

## X

InferHub 3.60: one node, three engines — Ollama, llama.cpp and colibri — routed by model, started and stopped from the hub. The node's config stays the ceiling.

The real-engine run caught a killed node orphaning llama-server. Fixed before release.

https://blog.devart.solutions/blog/inferhub-3-60-three-engines-one-node
