# compare-engines — 2026-11-23T11:25:26Z

InferHub `e68ed58`, published images dev (revision ``), llama.cpp `fd27b20` (b12182), Prima.cpp `288df6a` (layers 8,8,8,8).
Model `llama31-8b.gguf` sha256 `87048bcd55216712ef14c11c2c303728463207b165bf18440b9b84b07ec00f87` (4.34 GiB). 4 CPUs per container, 2560m per worker (4g per InferHub node), host 64 CPUs. 3 repetitions, 48 tokens, temperature 0. Values are median (min–max).

| Configuration | Round trip set / measured | First token, short prompt | First token, long prompt | Generate | short / long prompt | Of its own whole model | Prefill (engine's own count) | Bytes per token | CPUs busy |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| whole | 0 ms / — | 510 ms (506–522) | 14400 ms (14384–14419) | 6.63 tok/s (6.35–6.99) | 6.89 / 6.39 | — | 30.2 tok/s (30.2–30.2) | 245 (245–245) | 3.99 (3.98–3.99) |
| whole-chat | 0 ms / — | 186 ms (181–821) | 15112 ms (15061–15118) | 6.56 tok/s (6.33–6.80) | 6.78 / 6.34 | — | 30.1 tok/s (30.0–30.1) | 339 (339–339) | 3.99 (3.97–3.99) |
| inferhub-group | 0 ms / — | 248 ms (204–782) | 20880 ms (20814–21023) | 6.16 tok/s (5.98–6.20) | 6.18 / 6.08 | 94 % | not established | 602404 (575058–694303) | 4.16 (4.05–4.38) |
| inferhub-group | 120 ms / — | 7043 ms (1464–12637) | 32591 ms (26486–33325) | 1.00 tok/s (0.98–1.02) | 1.01 / 0.98 | 15 % | not established | 696623 (577392–1603258) | 1.07 (0.58–1.56) |
| inferhub-group-draft | 0 ms / — | 269 ms (242–883) | 23336 ms (23256–23550) | 6.91 tok/s (6.52–7.51) | 6.60 / 7.25 | 105 % | not established | 853891 (826999–946216) | 4.07 (4.01–4.21) |
| inferhub-group-draft | 120 ms / — | 7188 ms (1485–20372) | 35077 ms (28728–35399) | 1.53 tok/s (1.18–1.72) | 1.34 / 1.71 | 23 % | not established | 947235 (826233–1857214) | 1.37 (0.53–2.15) |

## Load

| Configuration | Until it answers | Bytes sent between containers by then | |
|---|---:|---:|---|
| whole | 32 s | 0 MiB | one container, the file from a read-only mount |
| inferhub-group | 90 s | 2962 MiB | includes the hub start, four enrollments and the lab's two warm-up chats |
| inferhub-group-draft | 88 s | 2972 MiB | includes the hub start, four enrollments and the lab's two warm-up chats |

## Memory (after the runs; the peak counts page cache, which a container under its limit gives back)

| Configuration | Container | Peak | Anonymous | File cache |
|---|---|---:|---:|---:|
| whole | cmp-whole | 8.70 GiB | 4.33 GiB | 4.34 GiB |
| whole-chat | cmp-whole | 8.91 GiB | 4.53 GiB | 4.34 GiB |
| inferhub-group | shard-a | 4.00 GiB | 1.98 GiB | 1.99 GiB |
| inferhub-group | shard-b | 2.63 GiB | 1.55 GiB | 1.04 GiB |
| inferhub-group | shard-c | 3.23 GiB | 1.83 GiB | 1.35 GiB |
| inferhub-group | shard-d | 0.08 GiB | 0.05 GiB | 0.02 GiB |
| inferhub-group-draft | shard-a | 4.00 GiB | 2.71 GiB | 1.25 GiB |
| inferhub-group-draft | shard-b | 2.67 GiB | 1.59 GiB | 1.04 GiB |
| inferhub-group-draft | shard-c | 3.25 GiB | 1.85 GiB | 1.35 GiB |
| inferhub-group-draft | shard-d | 0.08 GiB | 0.05 GiB | 0.02 GiB |

## Skipped

- **exo** — a60b956: the only engine is MLX and it loads MLX safetensors, not GGUF - no run on the same weights and quantization exists
- **nakshatra** — 373a17d: a patched llama.cpp b9992 and per-worker sub-GGUFs cut from the model - GGUF-compatible, not built in this run (the mechanism it shares with Prima.cpp is the one measured)

## Gates

```
  PASS  baseline and candidate configurations recorded (whole, whole-chat, inferhub-group, inferhub-group-draft; skipped: exo, nakshatra)
  PASS  same test prompts, model 87048bcd5521…, quantization and output settings (36 runs of 48 tokens at temperature 0)
  PASS  correctness / compatibility thresholds held: every split answers its own whole model's text, every repetition the same · the draft runs too
  PASS  latency, throughput, memory and network results written to artifacts
== decision: defer — no candidate was measured beside the baseline in this run
== draft: inferhub-group-draft: adopt — against inferhub-group (short / long): 1.07× / 1.19× at 0 ms, 1.33× / 1.74× at 120 ms
```
