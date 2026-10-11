# InferHub v3.67.0 — social copy (unposted)

## Facebook

InferHub 3.67: each model runtime on a node gets hard limits.

A GPU box often runs Ollama, llama.cpp, colibri, Strata and speech or image workers side by side. Now the node can cap what each of them may use — CPU, cores, memory, VRAM and which GPUs — for all of that runtime's processes together.

• Windows holds CPU, cores and memory in a Job Object; an allocation past the limit fails.
• No OS caps GPU memory, so the node watches it and stops a runtime that goes over.
• The node says how each limit is held — and never claims one it can't hold.

The coordinator sees every node's limits and can set lower ones of its own. It can never raise the node's: ask for more and it's refused, with the node's number.

Measured on a 3090 Ti: the node read 255 MiB of VRAM against a 100 MiB limit and stopped the runtime; with no GPU allowed, it restarted on the CPU at zero.

https://blog.devart.solutions/blog/inferhub-3-67-hard-limits-per-runtime

## X

InferHub 3.67: a node caps CPU, cores, memory, VRAM and GPUs for each runtime it runs — Ollama, llama.cpp, colibri, Strata. The coordinator sees the caps and may only set lower ones; it can never raise the node's.

https://blog.devart.solutions/blog/inferhub-3-67-hard-limits-per-runtime
