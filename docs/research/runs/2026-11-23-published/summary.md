# compare-engines — 2026-11-23T12:16:32Z

InferHub `a0d1fa2`, published images 3.111.0 (revision `a0d1fa2`), llama.cpp `fd27b20` (b12182), Prima.cpp `288df6a` (layers 8,8,8,8).
Model `llama31-8b.gguf` sha256 `87048bcd55216712ef14c11c2c303728463207b165bf18440b9b84b07ec00f87` (4.34 GiB). 4 CPUs per container, 2560m per worker (4g per InferHub node), host 64 CPUs. 3 repetitions, 48 tokens, temperature 0. Values are median (min–max).

| Configuration | Round trip set / measured | First token, short prompt | First token, long prompt | Generate | short / long prompt | Of its own whole model | Prefill (engine's own count) | Bytes per token | CPUs busy |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| whole | 0 ms / — | 475 ms (470–481) | 14361 ms (14313–14377) | 6.63 tok/s (6.40–6.83) | 6.82 / 6.45 | — | 30.3 tok/s (30.3–30.4) | 245 (245–245) | 3.98 (3.98–3.99) |
| whole-chat | 0 ms / — | 184 ms (175–771) | 15042 ms (15001–15097) | 6.64 tok/s (6.33–6.87) | 6.87 / 6.38 | — | 30.3 tok/s (30.2–30.3) | 339 (339–339) | 3.98 (3.96–3.99) |
| inferhub-group | 0 ms / — | 248 ms (202–775) | 20802 ms (20655–20839) | 6.26 tok/s (6.03–6.47) | 6.41 / 6.09 | 94 % | not established | 601160 (575507–696437) | 4.17 (4.05–4.41) |
| inferhub-group | 120 ms / — | 7050 ms (1458–22134) | 32486 ms (26479–32798) | 0.99 tok/s (0.98–1.03) | 1.00 / 0.98 | 15 % | not established | 696632 (577382–1604109) | 1.08 (0.52–1.56) |
| inferhub-group-draft | 0 ms / — | 270 ms (225–887) | 22640 ms (22602–23052) | 6.92 tok/s (6.60–7.31) | 6.67 / 7.22 | 104 % | not established | 853458 (825872–945137) | 4.07 (4.00–4.18) |
| inferhub-group-draft | 120 ms / — | 7199 ms (1488–13359) | 34226 ms (28059–34648) | 1.52 tok/s (1.26–1.71) | 1.33 / 1.71 | 23 % | not established | 947232 (826269–1856585) | 1.37 (0.61–2.13) |

## Load

| Configuration | Until it answers | Bytes sent between containers by then | |
|---|---:|---:|---|
| whole | 30 s | 0 MiB | one container, the file from a read-only mount |
| inferhub-group | 82 s | 2961 MiB | includes the hub start, four enrollments and the lab's two warm-up chats |
| inferhub-group-draft | 90 s | 2973 MiB | includes the hub start, four enrollments and the lab's two warm-up chats |

## Memory (after the runs; the peak counts page cache, which a container under its limit gives back)

| Configuration | Container | Peak | Anonymous | File cache |
|---|---|---:|---:|---:|
| whole | cmp-whole | 8.74 GiB | 4.33 GiB | 4.37 GiB |
| whole-chat | cmp-whole | 8.94 GiB | 4.53 GiB | 4.37 GiB |
| inferhub-group | shard-a | 4.00 GiB | 1.98 GiB | 1.99 GiB |
| inferhub-group | shard-b | 2.63 GiB | 1.55 GiB | 1.04 GiB |
| inferhub-group | shard-c | 3.22 GiB | 1.82 GiB | 1.35 GiB |
| inferhub-group | shard-d | 0.08 GiB | 0.05 GiB | 0.02 GiB |
| inferhub-group-draft | shard-a | 4.00 GiB | 2.72 GiB | 1.25 GiB |
| inferhub-group-draft | shard-b | 2.67 GiB | 1.59 GiB | 1.04 GiB |
| inferhub-group-draft | shard-c | 3.26 GiB | 1.86 GiB | 1.35 GiB |
| inferhub-group-draft | shard-d | 0.08 GiB | 0.04 GiB | 0.02 GiB |

## Skipped

- **exo** — a60b956: the only engine is MLX and it loads MLX safetensors, not GGUF - no run on the same weights and quantization exists
- **nakshatra** — 373a17d: a patched llama.cpp b9992 and per-worker sub-GGUFs cut from the model - GGUF-compatible, not built in this run (the mechanism it shares with Prima.cpp is the one measured)

## Gates

```
  PASS  baseline and candidate configurations recorded (whole, whole-chat, inferhub-group, inferhub-group-draft; skipped: exo, nakshatra)
  PASS  same test prompts, model 87048bcd5521…, quantization and output settings (36 runs of 48 tokens at temperature 0); images built from a0d1fa2
  PASS  correctness / compatibility thresholds held: every split answers its own whole model's text, every repetition the same · the draft runs too
  PASS  latency, throughput, memory and network results written to artifacts
== decision: defer — no candidate was measured beside the baseline in this run
== draft: inferhub-group-draft: adopt — against inferhub-group (short / long): 1.04× / 1.18× at 0 ms, 1.33× / 1.74× at 120 ms
```
