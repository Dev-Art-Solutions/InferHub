# v3.61.0 social copy (unposted — Iliya posts; blog is live, ID 6ac582f689927765fb687a0e)

## Facebook

InferHub 3.61: all of llama.cpp.

A node used to start one llama.cpp server per model file. Now it runs llama.cpp's router: one process over a whole directory of GGUF models, each loaded the first time somebody asks for it. From the hub you can pull a model straight from Hugging Face, warm it, unload it and delete it. And llama.cpp's own features reach clients through the fleet: grammar-constrained completions, code infill for editor plugins, tokenize, and reranking.

Running it against the real thing found three bugs before release. The worst: a node with several engines had never offered model management at all, because it answered "can you manage models?" before its engines had started.

https://blog.devart.solutions/blog/inferhub-3-61-all-of-llama-cpp

## X

InferHub 3.61: one llama.cpp router per node instead of one process per model. Pull from Hugging Face, warm, unload and delete from the hub; grammars, infill and rerank through the fleet.

The real run found a node that had never offered model management. Fixed.

https://blog.devart.solutions/blog/inferhub-3-61-all-of-llama-cpp
