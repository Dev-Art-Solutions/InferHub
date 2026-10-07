# v3.62.0 social copy (unposted — Iliya posts; blog is live, ID 6ac69e668e9d4cc6f10fd138)

## Facebook

InferHub 3.62: many colibri models on one node.

colibri runs Mixture-of-Experts models that are bigger than your RAM by reading the experts from disk. An InferHub node used to run exactly one colibri model, chosen at boot and held in memory forever. Now it holds a catalogue: every converted model in a directory is listed to the hub, the one a request asks for is started, and only as many as you allow are in memory at once — the old one is stopped before the next one starts.

Turn on "on demand" and a model nobody has asked for in ten minutes is stopped, so the memory goes back to the machine. And the coordinator decides which models stay loaded: a new console panel with Load and Unload per model, saved in the node's profile so the choice survives a restart.

Checked against the real engine: switch, pin, idle stop and Brio scoring, all through a real hub.

https://blog.devart.solutions/blog/inferhub-3-62-many-colibri-models-one-node

## X

InferHub 3.62: a colibri node holds a whole catalogue of MoE models. The one you ask for is loaded, idle ones are stopped to free RAM, and the coordinator picks which stay loaded.

https://blog.devart.solutions/blog/inferhub-3-62-many-colibri-models-one-node
