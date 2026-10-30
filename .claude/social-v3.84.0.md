# v3.84.0 social copy (unposted)

## Facebook

InferHub 3.84: models supplied by the hub.

A sealed node downloads only through its coordinator. Until now the coordinator still fetched from the internet at the moment a node asked, so a site with no internet at all could not manage models through InferHub.

Now an admin fetches a model once, or imports it from a disk: an Ollama models folder, a Hugging Face cache, or another hub's supply. Every byte is checked against the digest or sha256 the upstream named, and a model is supplied whole or not at all. The hub then answers its sealed nodes from its own disk and does not ask the upstream. Turn on Only, and anything not supplied is refused, with a sentence the node's pull ends with.

Live: with every upstream unreachable, a sealed node pulled a supplied model and answered through the hub, and a 398 MB model was imported from a real Ollama folder.

https://blog.devart.solutions/blog/inferhub-3-84-models-supplied-by-the-hub

## X

InferHub 3.84: the coordinator supplies models to a sealed fleet. Fetched and checked once, or imported from a disk where there is no internet, and nodes pull only from there.

https://blog.devart.solutions/blog/inferhub-3-84-models-supplied-by-the-hub
