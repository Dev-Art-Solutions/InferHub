# v3.58.0 social copy (unposted — Iliya posts; blog is live, ID 6ac18794d74b2be3ebc66b2f)

## Facebook

InferHub 3.58: a model bigger than your RAM.

An InferHub node can now drive colibri, an open-source engine that runs Mixture-of-Experts models by keeping the always-used part in RAM and reading the experts from disk only when they are needed. A 744B model needs about 25 GB of RAM and a fast SSD instead of a rack of GPUs. The new inferhub-node:colibri image starts the engine, restarts it if it exits, and tells the hub when the model is ready.

Running a real engine through InferHub found three bugs before release. The worst was ours, not colibri's: when any backend refused a streamed request, OpenAI clients got 200 OK and an empty answer. Now they get a 502 with the backend's own message.

https://blog.devart.solutions/blog/inferhub-3-58-a-model-bigger-than-your-ram-and-the-errors-that-looked-like-answers

## X

InferHub 3.58: a node can drive colibri, which runs MoE models bigger than your RAM by reading experts from disk.

Testing it found that a refused OpenAI stream, from any backend, reached clients as an empty success. Now it is a 502.

https://blog.devart.solutions/blog/inferhub-3-58-a-model-bigger-than-your-ram-and-the-errors-that-looked-like-answers
