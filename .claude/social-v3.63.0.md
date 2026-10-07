# v3.63.0 social copy (unposted — Iliya posts; blog is live, ID 6ac6ad0a8e9d4cc6f10fd1e9)

## Facebook

InferHub 3.63: give a node a Hugging Face link, and it gets the model.

From the coordinator's console you paste a link. The node downloads it once and keeps it. A GGUF goes straight to the node's llama.cpp server — checked file by file, resumed if the connection drops. A standard safetensors checkpoint of a Mixture-of-Experts model is converted for colibri, the engine that runs models bigger than your RAM.

One honest catch: colibri cannot read GGUF, and llama.cpp needs it. So one download never serves both engines — for the same model on both, you pull both versions.

Running it against the real Hugging Face found two bugs before release: the converter picked the wrong model family, and a half-converted model showed up as ready. Both fixed.

https://blog.devart.solutions/blog/inferhub-3-63-a-hugging-face-link-from-the-hub

## X

InferHub 3.63: paste a Hugging Face link in the console and the node fetches the model once — a GGUF for llama.cpp, a checkpoint converted for colibri. The real run caught two bugs before release.

https://blog.devart.solutions/blog/inferhub-3-63-a-hugging-face-link-from-the-hub
