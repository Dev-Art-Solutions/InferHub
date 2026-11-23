# compare-engines — 2026-11-23T10:52:28Z

InferHub `e68ed58`, published images 3.100.0 (revision `fb07a72`), llama.cpp `fd27b20` (b12182), Prima.cpp `288df6a` (layers 8,8,8,8).
Model `llama31-8b.gguf` sha256 `87048bcd55216712ef14c11c2c303728463207b165bf18440b9b84b07ec00f87` (4.34 GiB). 4 CPUs per container, 2560m per worker (4g per InferHub node), host 64 CPUs. 3 repetitions, 48 tokens, temperature 0. Values are median (min–max).

| Configuration | Round trip set / measured | First token, short prompt | First token, long prompt | Generate | short / long prompt | Of its own whole model | Prefill (engine's own count) | Bytes per token | CPUs busy |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| whole | 0 ms / — | 465 ms (465–470) | 14338 ms (14337–14442) | 6.75 tok/s (6.55–6.93) | 6.93 / 6.62 | — | 30.3 tok/s (30.1–30.3) | 245 (245–245) | 3.99 (3.98–3.99) |
| whole-chat | 0 ms / — | 177 ms (177–762) | 15004 ms (14960–15020) | 6.86 tok/s (6.50–7.08) | 7.07 / 6.67 | — | 30.3 tok/s (30.3–30.3) | 339 (339–339) | 3.99 (3.97–3.99) |
| rpc-star | 0 ms / 0.07 ms | 538 ms (535–555) | 19386 ms (19381–19728) | 6.48 tok/s (6.16–6.69) | 6.64 / 6.26 | 96 % | 22.4 tok/s (22.3–22.4) | 693888 (692112–848957) | 4.11 (4.00–4.21) |
| rpc-star | 30 ms / 30.17 ms | 1397 ms (1372–4474) | 21446 ms (21430–22854) | 2.71 tok/s (2.46–3.21) | 2.96 / 2.48 | 40 % | 20.3 tok/s (20.3–20.4) | 688788 (688691–2005500) | 2.26 (1.74–2.70) |
| rpc-star | 120 ms / 120.18 ms | 3906 ms (3891–14885) | 26350 ms (26332–31775) | 0.62 tok/s (0.62–0.62) | 0.62 / 0.62 | 9 % | 16.6 tok/s (16.6–16.6) | 697560 (696702–1999983) | 0.71 (0.36–1.06) |
| rpc-draft | 0 ms / 0.08 ms | 644 ms (617–646) | 21156 ms (21134–21503) | 6.80 tok/s (6.40–7.25) | 7.24 / 6.49 | 101 % | 20.5 tok/s (20.3–20.6) | 895646 (887411–1044199) | 4.04 (3.99–4.11) |
| rpc-draft | 30 ms / 30.14 ms | 1464 ms (1460–4314) | 23556 ms (23370–24981) | 2.91 tok/s (2.73–3.07) | 3.06 / 2.77 | 43 % | 18.6 tok/s (18.5–18.6) | 885001 (884423–2200405) | 2.23 (1.47–2.85) |
| rpc-draft | 120 ms / 120.20 ms | 3988 ms (3985–14883) | 29644 ms (28073–33885) | 0.88 tok/s (0.86–0.89) | 0.89 / 0.86 | 13 % | 15.3 tok/s (14.7–15.5) | 896685 (893133–2200414) | 0.90 (0.44–1.43) |

## Load

| Configuration | Until it answers | Bytes sent between containers by then | |
|---|---:|---:|---|
| whole | 31 s | 0 MiB | one container, the file from a read-only mount |
| rpc-star | 59 s | 4176 MiB | the head sends every worker its layers |
| rpc-draft | 62 s | 4167 MiB | as rpc-star, plus the draft read from the head's own disk |

## Memory (after the runs; the peak counts page cache, which a container under its limit gives back)

| Configuration | Container | Peak | Anonymous | File cache |
|---|---|---:|---:|---:|
| whole | cmp-whole | 8.70 GiB | 4.33 GiB | 4.34 GiB |
| whole-chat | cmp-whole | 8.91 GiB | 4.53 GiB | 4.34 GiB |
| rpc-star | inferhub-shard-lab-head-1 | 2.50 GiB | 0.06 GiB | 2.35 GiB |
| rpc-star | inferhub-shard-lab-worker1-1 | 2.50 GiB | 1.60 GiB | 0.86 GiB |
| rpc-star | inferhub-shard-lab-worker2-1 | 2.50 GiB | 1.57 GiB | 0.89 GiB |
| rpc-star | inferhub-shard-lab-worker3-1 | 2.50 GiB | 1.85 GiB | 0.63 GiB |
| rpc-draft | inferhub-shard-lab-head-1 | 2.50 GiB | 0.75 GiB | 1.66 GiB |
| rpc-draft | inferhub-shard-lab-worker1-1 | 2.50 GiB | 1.70 GiB | 0.76 GiB |
| rpc-draft | inferhub-shard-lab-worker2-1 | 2.50 GiB | 1.65 GiB | 0.81 GiB |
| rpc-draft | inferhub-shard-lab-worker3-1 | 2.50 GiB | 1.92 GiB | 0.56 GiB |

## A cancel and a lost worker (observed, not gated)

- **rpc-star**: 0 CPUs busy in the five seconds after the client hung up; with one worker killed the head was still running and the next request did not answer (terminated).
- **rpc-draft**: 0 CPUs busy in the five seconds after the client hung up; with one worker killed the head was gone and the next request did not answer (terminated).

## Skipped

- **exo** — a60b956: the only engine is MLX and it loads MLX safetensors, not GGUF - no run on the same weights and quantization exists
- **nakshatra** — 373a17d: a patched llama.cpp b9992 and per-worker sub-GGUFs cut from the model - GGUF-compatible, not built in this run (the mechanism it shares with Prima.cpp is the one measured)

## Gates

```
  PASS  baseline and candidate configurations recorded (whole, whole-chat, rpc-star, rpc-draft; skipped: exo, nakshatra)
  PASS  same test prompts, model 87048bcd5521…, quantization and output settings (48 runs of 48 tokens at temperature 0)
  PASS  correctness / compatibility thresholds held: every split answers its own whole model's text, every repetition the same · the draft runs too
  PASS  latency, throughput, memory and network results written to artifacts
== decision: defer — no candidate was measured beside the baseline in this run
== draft: rpc-draft: adopt — against rpc-star (short / long): 1.09× / 1.04× at 0 ms, 1.03× / 1.12× at 30 ms, 1.44× / 1.39× at 120 ms
```
