# compare-engines — 2026-11-14T23:55:06Z

InferHub `f1622c1`, published images 3.100.0 (revision `fb07a72`), llama.cpp `fd27b20` (b12182), Prima.cpp `288df6a` (layers 8,8,8,8).
Model `llama31-8b.gguf` sha256 `87048bcd55216712ef14c11c2c303728463207b165bf18440b9b84b07ec00f87` (4.34 GiB). 4 CPUs per container, 2560m per worker (4g per InferHub node), host 64 CPUs. 3 repetitions, 48 tokens, temperature 0. Values are median (min–max).

| Configuration | Round trip set / measured | First token, short prompt | First token, long prompt | Generate | short / long prompt | Of its own whole model | Prefill (engine's own count) | Bytes per token | CPUs busy |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| whole | 0 ms / — | 468 ms (466–471) | 14333 ms (14301–14344) | 6.65 tok/s (6.41–6.86) | 6.77 / 6.55 | — | 30.3 tok/s (30.3–30.4) | 245 (245–245) | 3.99 (3.98–3.99) |
| whole-chat | 0 ms / — | 189 ms (178–760) | 15047 ms (14986–15054) | 6.65 tok/s (6.43–6.89) | 6.86 / 6.43 | — | 30.2 tok/s (30.2–30.3) | 339 (339–340) | 3.99 (3.97–3.99) |
| rpc-star | 0 ms / 0.07 ms | 545 ms (531–557) | 19448 ms (19393–19795) | 6.40 tok/s (6.22–6.63) | 6.58 / 6.27 | 96 % | 22.4 tok/s (22.2–22.4) | 693833 (692161–848921) | 4.11 (4.00–4.21) |
| rpc-star | 10 ms / 10.15 ms | 818 ms (816–1752) | 20134 ms (20114–20538) | 5.31 tok/s (5.19–5.52) | 5.46 / 5.20 | 80 % | 21.6 tok/s (21.6–21.7) | 691235 (691187–2007082) | 3.57 (3.14–3.75) |
| rpc-star | 30 ms / 30.16 ms | 1386 ms (1377–4172) | 21471 ms (21380–22625) | 2.73 tok/s (2.46–3.28) | 3.26 / 2.46 | 41 % | 20.4 tok/s (20.3–20.4) | 689474 (688795–2005389) | 2.33 (1.74–2.71) |
| inferhub-group | 0 ms / — | 245 ms (205–793) | 20756 ms (20637–20819) | 6.18 tok/s (5.96–6.32) | 6.28 / 6.03 | 93 % | not established | 601155 (575510–695511) | 4.17 (4.04–4.27) |
| inferhub-group | 10 ms / — | 815 ms (304–1389) | 21703 ms (20870–23424) | 5.44 tok/s (5.35–5.56) | 5.51 / 5.37 | 82 % | not established | 694169 (575511–1603125) | 3.67 (3.45–3.90) |
| inferhub-group | 30 ms / — | 1885 ms (513–22625) | 23675 ms (21798–24296) | 3.15 tok/s (2.98–4.30) | 3.26 / 3.13 | 47 % | not established | 694852 (574192–1605122) | 2.48 (1.00–3.12) |
| prima-whole | 0 ms / — | 533 ms (531–540) | 20379 ms (20365–20450) | 6.48 tok/s (6.28–6.64) | 6.64 / 6.29 | — | 21.3 tok/s (21.3–21.3) | 189 (189–189) | 3.98 (3.98–3.99) |
| prima-ring | 0 ms / 0.08 ms | 538 ms (533–543) | 20375 ms (20366–20557) | 6.55 tok/s (6.45–6.62) | 6.61 / 6.48 | 101 % | 21.3 tok/s (21.1–21.3) | 80688 (80448–80694) | 4.24 (4.09–4.39) |
| prima-ring | 10 ms / 10.13 ms | 680 ms (676–684) | 20723 ms (20713–20724) | 5.79 tok/s (5.48–5.92) | 5.88 / 5.68 | 89 % | 21.0 tok/s (21.0–21.0) | 80376 (80374–80400) | 3.87 (3.84–3.91) |
| prima-ring | 30 ms / 30.17 ms | 980 ms (979–985) | 21540 ms (21520–21580) | 4.17 tok/s (3.58–4.75) | 4.73 / 3.59 | 64 % | 20.2 tok/s (20.2–20.2) | 80497 (80492–80510) | 3.17 (3.07–3.27) |

## Load

| Configuration | Until it answers | Bytes sent between containers by then | |
|---|---:|---:|---|
| whole | 29 s | 0 MiB | one container, the file from a read-only mount |
| rpc-star | 62 s | 4171 MiB | the head sends every worker its layers |
| inferhub-group | 85 s | 2960 MiB | includes the hub start, four enrollments and the lab's two warm-up chats |
| prima-whole | 12 s | 0 MiB | one container, the file from a read-only mount |
| prima-ring | 15 s | 0 MiB | every rank reads the file itself |

## Memory (after the runs; the peak counts page cache, which a container under its limit gives back)

| Configuration | Container | Peak | Anonymous | File cache |
|---|---|---:|---:|---:|
| whole | cmp-whole | 8.70 GiB | 4.33 GiB | 4.34 GiB |
| whole-chat | cmp-whole | 8.90 GiB | 4.53 GiB | 4.34 GiB |
| rpc-star | inferhub-shard-lab-head-1 | 2.50 GiB | 0.06 GiB | 2.35 GiB |
| rpc-star | inferhub-shard-lab-worker1-1 | 2.50 GiB | 1.60 GiB | 0.86 GiB |
| rpc-star | inferhub-shard-lab-worker2-1 | 2.50 GiB | 1.57 GiB | 0.89 GiB |
| rpc-star | inferhub-shard-lab-worker3-1 | 2.51 GiB | 1.85 GiB | 0.63 GiB |
| inferhub-group | shard-a | 4.00 GiB | 2.15 GiB | 1.81 GiB |
| inferhub-group | shard-b | 2.63 GiB | 1.55 GiB | 1.04 GiB |
| inferhub-group | shard-c | 3.23 GiB | 1.83 GiB | 1.35 GiB |
| inferhub-group | shard-d | 0.07 GiB | 0.04 GiB | 0.02 GiB |
| prima-whole | cmp-prima-whole | 4.80 GiB | 0.69 GiB | 4.08 GiB |
| prima-ring | prima0 | 1.68 GiB | 0.33 GiB | 1.33 GiB |
| prima-ring | prima1 | 1.24 GiB | 0.31 GiB | 0.91 GiB |
| prima-ring | prima2 | 1.24 GiB | 0.31 GiB | 0.91 GiB |
| prima-ring | prima3 | 1.24 GiB | 0.31 GiB | 0.91 GiB |

## A cancel and a lost worker (observed, not gated)

- **rpc-star**: 0 CPUs busy in the five seconds after the client hung up; with one worker killed the head was still running and the next request did not answer (terminated).
- **prima-ring**: 0 CPUs busy in the five seconds after the client hung up; with one worker killed the head was still running and the next request did not answer (This operation was aborted).

## Skipped

- **exo** — a60b956: the only engine is MLX and it loads MLX safetensors, not GGUF - no run on the same weights and quantization exists
- **nakshatra** — 373a17d: a patched llama.cpp b9992 and per-worker sub-GGUFs cut from the model - GGUF-compatible, not built in this run (the mechanism it shares with Prima.cpp is the one measured)

## Gates

```
  PASS  baseline and candidate configurations recorded (whole, whole-chat, rpc-star, inferhub-group, prima-whole, prima-ring; skipped: exo, nakshatra)
  PASS  same test prompts, model 87048bcd5521…, quantization and output settings (72 runs of 48 tokens at temperature 0); images built from fb07a72
  PASS  correctness / compatibility thresholds held: every split answers its own whole model's text, every repetition the same · the candidate too
  PASS  latency, throughput, memory and network results written to artifacts
== decision: defer — do not adopt Prima.cpp (its ranks talk over plain ZeroMQ push/pull sockets with no authentication or encryption, it is a 2024 fork of llama.cpp (not the build a node runs) and it has no Windows build); against inferhub-group its ring keeps 1.09–1.10× at 0 ms, 1.08–1.10× at 10 ms, 1.17–1.50× at 30 ms of the share of whole-model speed, past 1.25× on one prompt and not the other: a gain on a wide network only, and not yet one that pays for a second engine
```
